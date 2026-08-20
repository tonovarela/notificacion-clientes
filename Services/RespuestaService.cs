using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using notificacion_clientes.Configuracion;
using notificacion_clientes.Entity;

namespace notificacion_clientes.Services
{
    /// <summary>
    /// Lee el buzón que manda los correos y averigua cuáles de los envíos pendientes ya tienen
    /// respuesta del cliente.
    ///
    /// El buzón se abre SIEMPRE en sólo lectura: es el inbox que usa cobranza a diario y un
    /// proceso automático no tiene por qué marcarle correos como leídos.
    /// </summary>
    public class RespuestaService
    {
        /// <summary>Prefijos de respuesta y reenvío que hay que quitar antes de comparar asuntos.</summary>
        private static readonly Regex PrefijosAsunto = new(
            @"^\s*(re|rv|fwd|fw|ref)\s*(\[\d+\])?\s*:\s*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Buzones que mandan rebotes. Un rebote no es una respuesta.</summary>
        private static readonly string[] BuzonesDeRebote = { "mailer-daemon", "postmaster" };

        /// <summary>Headers que delatan una respuesta automática. Nunca se baja el cuerpo del correo.</summary>
        private static readonly string[] HeadersDeAutorespuesta = { "Auto-Submitted", "Precedence", "X-Autoreply" };

        private readonly ImapSettings _configuracion;

        public RespuestaService(ImapSettings configuracion)
        {
            _configuracion = configuracion;
        }

        /// <summary>
        /// Busca en el buzón las respuestas a los envíos indicados.
        ///
        /// Sólo se leen los correos posteriores al envío pendiente más viejo: más atrás no puede
        /// haber nada que conciliar, y recorrer el buzón entero de cobranza cada mañana sería
        /// caro y lento sin ganar nada.
        /// </summary>
        public async Task<IReadOnlyList<RespuestaDetectada>> Conciliar(
            IReadOnlyList<EnvioNotificacion> pendientes,
            int diasVentanaMaxima,
            CancellationToken cancelacion = default)
        {
            if (pendientes.Count == 0)
                return Array.Empty<RespuestaDetectada>();

            // Un día de colchón: la fecha de entrega del servidor y la nuestra pueden no coincidir
            // por zona horaria, y perder una respuesta cuesta insistirle a quien ya contestó.
            var desdeElPendiente = pendientes.Min(p => p.FechaEnvio).Date.AddDays(-1);

            // Tope duro. Si un envío se quedara abierto por error, sin esto la búsqueda crecería
            // sin límite hasta descargar el buzón entero en cada corrida.
            var tope = DateTime.Today.AddDays(-diasVentanaMaxima);
            var desde = desdeElPendiente < tope ? tope : desdeElPendiente;

            using var imap = new ImapClient();

            // Mismo criterio que CorreoService: la cadena del certificado se sigue validando, sólo
            // se omite la consulta de revocación (CRL/OCSP), que desde esta red no siempre se
            // puede completar y tumba el handshake antes de llegar a autenticar.
            imap.CheckCertificateRevocation = false;

            await imap.ConnectAsync(_configuracion.Host, _configuracion.Puerto, SecureSocketOptions.SslOnConnect, cancelacion);
            await imap.AuthenticateAsync(_configuracion.Usuario, _configuracion.Password, cancelacion);

            try
            {
                var carpeta = await imap.GetFolderAsync(_configuracion.Carpeta, cancelacion);
                await carpeta.OpenAsync(FolderAccess.ReadOnly, cancelacion);

                var uids = await carpeta.SearchAsync(SearchQuery.DeliveredAfter(desde), cancelacion);

                if (uids.Count == 0)
                    return Array.Empty<RespuestaDetectada>();

                // Envelope trae From/Subject/Date/InReplyTo; References, la cadena del hilo.
                // Los headers extra sirven para descartar autorrespuestas.
                var resumenes = await carpeta.FetchAsync(
                    uids,
                    MessageSummaryItems.Envelope | MessageSummaryItems.References,
                    HeadersDeAutorespuesta,
                    cancelacion);

                return Cruzar(pendientes, resumenes);
            }
            finally
            {
                await imap.DisconnectAsync(true, cancelacion);
            }
        }

        /// <summary>
        /// Casa cada correo del buzón con el envío que lo provocó. Un envío se cierra con la
        /// primera respuesta que aparece: las siguientes del mismo hilo ya no cambian nada.
        /// </summary>
        private static IReadOnlyList<RespuestaDetectada> Cruzar(
            IReadOnlyList<EnvioNotificacion> pendientes,
            IEnumerable<IMessageSummary> resumenes)
        {
            var porMessageId = new Dictionary<string, EnvioNotificacion>(StringComparer.OrdinalIgnoreCase);

            // Los ids de recordatorio se indexan aparte: hay que saber cuál de los dos casó, porque
            // una respuesta al recordatorio cierra todos los envíos que aquel correo cubría y una
            // al envío original cierra sólo ése.
            var idsDeRecordatorio = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pendiente in pendientes)
            {
                porMessageId[Normalizar(pendiente.MessageId)] = pendiente;

                // Se indexan TODOS los recordatorios que ha recibido, no sólo el último: el
                // cliente puede contestar el correo de hace tres semanas.
                foreach (var recordatorio in pendiente.RecordatorioMessageIds)
                {
                    if (string.IsNullOrWhiteSpace(recordatorio))
                        continue;

                    var idRecordatorio = Normalizar(recordatorio);
                    idsDeRecordatorio.Add(idRecordatorio);

                    // Varios envíos comparten el mismo id; con quedarse con uno basta para casar.
                    porMessageId.TryAdd(idRecordatorio, pendiente);
                }
            }

            var detectadas = new Dictionary<int, RespuestaDetectada>();

            foreach (var resumen in resumenes.OrderBy(r => r.Envelope?.Date ?? DateTimeOffset.MinValue))
            {
                if (resumen.Envelope is null)
                    continue;

                var deEmail = PrimerRemitente(resumen);
                if (deEmail is null)
                    continue;

                var esRebote = EsRebote(deEmail);

                // Un "estoy fuera de la oficina" cerraría el pendiente y el cliente nunca recibiría
                // el recordatorio. Ante la duda se descarta: fallar del lado de insistir es barato.
                if (!esRebote && EsAutorespuesta(resumen))
                    continue;

                var envio = CasarPorHilo(resumen, porMessageId, out var criterio, out var idCasado)
                            ?? CasarPorRemitenteYAsunto(resumen, pendientes, deEmail, out criterio);

                if (envio is null || detectadas.ContainsKey(envio.IdEnvio))
                    continue;

                var fueRecordatorio = idCasado is not null && idsDeRecordatorio.Contains(idCasado);

                detectadas[envio.IdEnvio] = new RespuestaDetectada
                {
                    RespondioARecordatorio = fueRecordatorio ? idCasado : null,
                    Envio = envio,
                    DeEmail = deEmail,
                    Fecha = (resumen.Envelope.Date ?? DateTimeOffset.Now).LocalDateTime,
                    Asunto = resumen.Envelope.Subject ?? string.Empty,
                    MessageId = Normalizar(resumen.Envelope.MessageId ?? string.Empty),
                    Criterio = criterio,
                    EsRebote = esRebote
                };
            }

            return detectadas.Values.ToList();
        }

        /// <summary>
        /// Camino normal y exacto: el In-Reply-To de la respuesta, o cualquier id de su cadena
        /// References, es uno de los Message-Id que mandamos nosotros.
        /// </summary>
        private static EnvioNotificacion? CasarPorHilo(
            IMessageSummary resumen,
            IReadOnlyDictionary<string, EnvioNotificacion> porMessageId,
            out CriterioCruce criterio,
            out string? idCasado)
        {
            criterio = CriterioCruce.InReplyTo;
            idCasado = null;

            var enRespuestaA = Normalizar(resumen.Envelope?.InReplyTo ?? string.Empty);

            if (enRespuestaA.Length > 0 && porMessageId.TryGetValue(enRespuestaA, out var porInReplyTo))
            {
                idCasado = enRespuestaA;
                return porInReplyTo;
            }

            if (resumen.References is null)
                return null;

            // Se recorre al revés: el último id de la cadena es el mensaje al que se contesta.
            foreach (var referencia in resumen.References.Reverse())
            {
                var id = Normalizar(referencia);

                if (porMessageId.TryGetValue(id, out var porReferencia))
                {
                    criterio = CriterioCruce.References;
                    idCasado = id;
                    return porReferencia;
                }
            }

            return null;
        }

        /// <summary>
        /// Camino aproximado, para los clientes cuyo programa de correo no conserva los headers
        /// del hilo: el remitente es uno de los destinatarios del envío, el asunto coincide una
        /// vez quitados los "Re:", y el correo es posterior al envío.
        /// </summary>
        private static EnvioNotificacion? CasarPorRemitenteYAsunto(
            IMessageSummary resumen,
            IReadOnlyList<EnvioNotificacion> pendientes,
            string deEmail,
            out CriterioCruce criterio)
        {
            criterio = CriterioCruce.RemitenteYAsunto;

            var fecha = (resumen.Envelope?.Date ?? DateTimeOffset.Now).LocalDateTime;
            var asunto = SinPrefijos(resumen.Envelope?.Subject ?? string.Empty);

            if (asunto.Length == 0)
                return null;

            return pendientes.FirstOrDefault(envio =>
                envio.FechaEnvio <= fecha
                && SinPrefijos(envio.Asunto).Equals(asunto, StringComparison.OrdinalIgnoreCase)
                && DestinatariosDe(envio).Contains(deEmail, StringComparer.OrdinalIgnoreCase));
        }

        private static IEnumerable<string> DestinatariosDe(EnvioNotificacion envio) =>
            envio.Destinatarios
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static string? PrimerRemitente(IMessageSummary resumen) =>
            resumen.Envelope?.From?.Mailboxes?.FirstOrDefault()?.Address;

        /// <summary>Un rebote viene del sistema de correo, no del cliente: la dirección está mal.</summary>
        private static bool EsRebote(string email)
        {
            var local = email.Split('@')[0];
            return BuzonesDeRebote.Any(buzon => local.Equals(buzon, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Vacaciones, acuses automáticos y listas de correo. Auto-Submitted con cualquier valor
        /// distinto de 'no' significa que lo generó una máquina; así lo define el RFC 3834.
        /// </summary>
        private static bool EsAutorespuesta(IMessageSummary resumen)
        {
            var headers = resumen.Headers;
            if (headers is null)
                return false;

            var autoSubmitted = headers["Auto-Submitted"];
            if (!string.IsNullOrWhiteSpace(autoSubmitted)
                && !autoSubmitted.Trim().Equals("no", StringComparison.OrdinalIgnoreCase))
                return true;

            var precedence = headers["Precedence"]?.Trim();
            if (!string.IsNullOrWhiteSpace(precedence)
                && (precedence.Equals("bulk", StringComparison.OrdinalIgnoreCase)
                    || precedence.Equals("auto_reply", StringComparison.OrdinalIgnoreCase)
                    || precedence.Equals("junk", StringComparison.OrdinalIgnoreCase)))
                return true;

            return !string.IsNullOrWhiteSpace(headers["X-Autoreply"]);
        }

        /// <summary>Los Message-Id viajan entre &lt;&gt; en los headers; en la base se guardan sin ellos.</summary>
        private static string Normalizar(string messageId) =>
            messageId.Trim().Trim('<', '>').Trim();

        /// <summary>Quita los "Re:", "RV:", "Fwd:" encadenados que se acumulan en un hilo largo.</summary>
        private static string SinPrefijos(string asunto)
        {
            var limpio = asunto.Trim();
            string anterior;

            do
            {
                anterior = limpio;
                limpio = PrefijosAsunto.Replace(limpio, string.Empty).Trim();
            }
            while (limpio != anterior);

            return limpio;
        }
    }
}
