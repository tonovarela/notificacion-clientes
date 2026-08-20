using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MimeKit.Utils;
using notificacion_clientes.Configuracion;
using notificacion_clientes.Entity;

namespace notificacion_clientes.Services
{
    /// <summary>
    /// Envía un correo HTML por cliente, con el XML y el PDF de sus facturas adjuntos.
    /// Reutiliza una sola conexión SMTP para toda la lista.
    /// </summary>
    public class CorreoService
    {
        /// <summary>Identificador con el que la plantilla referencia el logo: src="cid:logo".</summary>
        private const string ContentIdLogo = "logo";

        /// <summary>
        /// Header propio con un GUID por correo. El Message-Id lo puede reescribir el servidor
        /// de salida; éste no lo toca nadie, así que es con lo que se reconcilia si algo no casa.
        /// </summary>
        public const string HeaderToken = "X-Notificacion-Id";

        private readonly SmtpSettings _settings;
        private readonly PlantillaService _plantillaService;
        private readonly PlantillaVendedorService _plantillaVendedorService;
        private readonly PlantillaCobranzaService _plantillaCobranzaService;
        private readonly string _rutaLogo;

        public CorreoService(
            SmtpSettings settings,
            PlantillaService plantillaService,
            PlantillaVendedorService plantillaVendedorService,
            PlantillaCobranzaService plantillaCobranzaService,
            string rutaLogo)
        {
            _settings = settings;
            _plantillaService = plantillaService;
            _plantillaVendedorService = plantillaVendedorService;
            _plantillaCobranzaService = plantillaCobranzaService;
            _rutaLogo = rutaLogo;
        }

        /// <summary>Un correo por cliente con sus facturas del día adjuntas.</summary>
        public Task<IReadOnlyList<ResultadoEnvio>> Enviar(
            IReadOnlyList<NotificacionCliente> notificaciones,
            CancellationToken cancelacion = default) =>
            EnviarLote(notificaciones, n => n.Cliente, ArmarMensaje, cancelacion);

        /// <summary>
        /// Un correo por vendedor con su cartera pendiente de ingresar a revisión.
        /// La llave del resultado es el correo del vendedor: es lo único único por grupo,
        /// porque el mismo nombre puede venir capturado de varias formas en el CRM.
        /// </summary>
        public Task<IReadOnlyList<ResultadoEnvio>> EnviarVendedores(
            IReadOnlyList<NotificacionVendedor> notificaciones,
            CancellationToken cancelacion = default) =>
            EnviarLote(notificaciones, n => n.Email, ArmarMensajeVendedor, cancelacion);

        /// <summary>Un correo por cliente con su estado de cuenta vencido. Sin adjuntos.</summary>
        public Task<IReadOnlyList<ResultadoEnvio>> EnviarCobranza(
            IReadOnlyList<NotificacionCobranza> notificaciones,
            CancellationToken cancelacion = default) =>
            EnviarLote(notificaciones, n => n.Cliente, ArmarMensajeCobranza, cancelacion);

        /// <summary>
        /// Abre una sola conexión SMTP para todo el lote y manda un correo por elemento.
        /// Un envío que falla queda registrado y no detiene a los demás.
        /// </summary>
        private async Task<IReadOnlyList<ResultadoEnvio>> EnviarLote<T>(
            IReadOnlyList<T> notificaciones,
            Func<T, string> obtenerClave,
            Func<T, IReadOnlyList<MailboxAddress>, MimeMessage> armarMensaje,
            CancellationToken cancelacion)
        {
            var resultados = new List<ResultadoEnvio>();

            if (notificaciones.Count == 0)
                return resultados;

            // Se resuelve antes de conectar: una dirección mal escrita en la configuración se
            // reclama de una vez y no a la mitad de los envíos.
            var copiaOculta = ObtenerCopiaOculta();

            using var cliente = new SmtpClient();

            // La cadena del certificado se sigue validando; solo se omite la consulta de revocación (CRL/OCSP),
            // que no siempre se puede completar desde la red interna y tumba el handshake.
            cliente.CheckCertificateRevocation = false;

            await cliente.ConnectAsync(_settings.Host, _settings.Puerto, ObtenerModoSeguridad(), cancelacion);

            if (!string.IsNullOrWhiteSpace(_settings.Usuario))
                await cliente.AuthenticateAsync(_settings.Usuario, _settings.Password, cancelacion);

            foreach (var notificacion in notificaciones)
            {
                var clave = obtenerClave(notificacion);

                // Fuera del try: el catch lo necesita para registrar qué correo fue el que falló.
                MimeMessage? mensaje = null;

                try
                {
                    mensaje = armarMensaje(notificacion, copiaOculta);

                    if (mensaje.To.Count == 0)
                    {
                        resultados.Add(ResultadoEnvio.Fallido(clave, "No hay ninguna dirección de correo válida a la cual enviar"));
                        continue;
                    }

                    await cliente.SendAsync(mensaje, cancelacion);
                    resultados.Add(ResultadoEnvio.Exitoso(
                        clave,
                        mensaje.To.Mailboxes.Select(m => m.Address).ToList(),
                        mensaje.Bcc.Mailboxes.Select(m => m.Address).ToList(),
                        Identificar(mensaje)));
                }
                catch (Exception ex)
                {
                    // El identificador se conserva también al fallar: el envío se registra como
                    // FALLIDO y sin él no habría con qué relacionarlo si después aparece un rebote.
                    resultados.Add(ResultadoEnvio.Fallido(clave, ex.Message, Identificar(mensaje)));
                }
            }

            await cliente.DisconnectAsync(true, cancelacion);

            return resultados;
        }

        /// <summary>
        /// El logo viaja dentro del correo (no como URL): Gmail y Outlook bloquean las imágenes
        /// remotas por omisión, y así tampoco depende de que el sitio esté disponible.
        /// Si el archivo no está, se manda el correo sin logo antes que no mandarlo.
        /// </summary>
        private void IncrustarLogo(BodyBuilder cuerpo)
        {
            if (!File.Exists(_rutaLogo))
                return;

            var logo = cuerpo.LinkedResources.Add(_rutaLogo);
            logo.ContentId = ContentIdLogo;
            logo.ContentDisposition = new ContentDisposition(ContentDisposition.Inline);
        }

        /// <summary>
        /// El 465 es SSL implícito (TLS desde que abre el socket); el 587 y demás negocian con STARTTLS.
        /// Usar el modo equivocado hace que la conexión falle aunque las credenciales sean correctas.
        /// </summary>
        private SecureSocketOptions ObtenerModoSeguridad()
        {
            if (!_settings.UsarSsl)
                return SecureSocketOptions.None;

            return _settings.Puerto == 465
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;
        }

        /// <summary>
        /// Las direcciones de copia oculta se validan aquí y no al leer la configuración, para que
        /// el error salga dentro del flujo normal y quede registrado en la bitácora.
        /// </summary>
        private IReadOnlyList<MailboxAddress> ObtenerCopiaOculta()
        {
            var direcciones = new List<MailboxAddress>();

            foreach (var correo in _settings.CopiaOculta)
            {
                if (!MailboxAddress.TryParse(correo, out var direccion))
                    throw new InvalidOperationException($"La dirección '{correo}' de 'Smtp:CopiaOculta' no es válida");

                direcciones.Add(direccion);
            }

            return direcciones;
        }

        private MimeMessage ArmarMensaje(NotificacionCliente notificacion, IReadOnlyList<MailboxAddress> copiaOculta)
        {
            var mensaje = new MimeMessage();
            mensaje.From.Add(new MailboxAddress(_settings.RemitenteNombre, _settings.RemitenteEmail));
            mensaje.Subject = $"Facturas del día - {notificacion.RazonSocial}";

            Sellar(mensaje);

            foreach (var destinatario in ObtenerDestinatarios(notificacion))
                mensaje.To.Add(destinatario);

            // La copia oculta se manda siempre, incluso en modo prueba: es el registro de la
            // facturación y debe recibir lo mismo que se envió, sin importar el modo de la corrida.
            foreach (var copia in copiaOculta)
                mensaje.Bcc.Add(copia);

            var cuerpo = new BodyBuilder { HtmlBody = _plantillaService.Renderizar(notificacion) };

            IncrustarLogo(cuerpo);

            foreach (var archivo in notificacion.Documentos.SelectMany(d => d.Archivos))
                cuerpo.Attachments.Add(archivo.NombreArchivo, archivo.Contenido, ContentType.Parse(archivo.ContentType));

            mensaje.Body = cuerpo.ToMessageBody();

            return mensaje;
        }

        private MimeMessage ArmarMensajeVendedor(NotificacionVendedor notificacion, IReadOnlyList<MailboxAddress> copiaOculta)
        {
            var mensaje = new MimeMessage();
            mensaje.From.Add(new MailboxAddress(_settings.RemitenteNombre, _settings.RemitenteEmail));
            mensaje.Subject = $"Facturas pendientes de ingresar a revisión - {notificacion.Vendedor}";

            // El aviso a vendedores no entra al seguimiento, pero se sella igual: si no, MimeKit
            // genera el Message-Id con el nombre de la máquina, que no tiene por qué salir del host.
            Sellar(mensaje);

            foreach (var destinatario in ObtenerDestinatariosVendedor(notificacion))
                mensaje.To.Add(destinatario);

            foreach (var copia in copiaOculta)
                mensaje.Bcc.Add(copia);

            var cuerpo = new BodyBuilder { HtmlBody = _plantillaVendedorService.Renderizar(notificacion) };

            IncrustarLogo(cuerpo);

            // La cartera va completa en el cuerpo: aquí no se adjunta ningún CFDI.
            mensaje.Body = cuerpo.ToMessageBody();

            return mensaje;
        }

        private MimeMessage ArmarMensajeCobranza(NotificacionCobranza notificacion, IReadOnlyList<MailboxAddress> copiaOculta)
        {
            var mensaje = new MimeMessage();
            mensaje.From.Add(new MailboxAddress(_settings.RemitenteNombre, _settings.RemitenteEmail));

            // El recordatorio ya no cuelga del hilo del martes. Para poner In-Reply-To y References
            // hacía falta el Message-Id de aquel correo, que se recuperaba del seguimiento; ahora la
            // población del viernes sale de la consulta de facturas y ese envío no se busca. Se
            // distingue por el asunto, que es lo único que queda para avisarle al cliente que es
            // insistencia y no un duplicado.
            mensaje.Subject = notificacion.EsRecordatorio
                ? $"Recordatorio: estado de cuenta con saldo vencido - {notificacion.RazonSocial}"
                : $"Estado de cuenta con saldo vencido - {notificacion.RazonSocial}";

            Sellar(mensaje);

            foreach (var destinatario in ObtenerDestinatariosCobranza(notificacion))
                mensaje.To.Add(destinatario);

            foreach (var copia in copiaOculta)
                mensaje.Bcc.Add(copia);

            var cuerpo = new BodyBuilder { HtmlBody = _plantillaCobranzaService.Renderizar(notificacion) };

            IncrustarLogo(cuerpo);

            // El estado de cuenta va completo en el cuerpo: aquí no se adjunta ningún CFDI.
            mensaje.Body = cuerpo.ToMessageBody();

            return mensaje;
        }

        /// <summary>En modo prueba todo se redirige al buzón de pruebas, nunca al cliente.</summary>
        private IEnumerable<MailboxAddress> ObtenerDestinatariosCobranza(NotificacionCobranza notificacion)
        {
            if (_settings.ModoPrueba)
            {
                yield return CorreoDePrueba();
                yield break;
            }

            foreach (var contacto in notificacion.Contactos)
            {
                if (Parsear(contacto.Email) is { } direccion)
                    yield return direccion;
            }
        }

        /// <summary>
        /// En modo prueba el correo del vendedor no se usa: el aviso completo se manda a un solo
        /// destinatario, el buzón de pruebas de vendedores.
        /// </summary>
        private IEnumerable<MailboxAddress> ObtenerDestinatariosVendedor(NotificacionVendedor notificacion)
        {
            if (_settings.ModoPruebaVendedores)
            {
                yield return CorreoDePruebaVendedor();
                yield break;
            }

            if (Parsear(notificacion.Email) is { } direccion)
                yield return direccion;
        }

        /// <summary>En modo prueba todo se redirige al buzón de pruebas, nunca al cliente.</summary>
        private IEnumerable<MailboxAddress> ObtenerDestinatarios(NotificacionCliente notificacion)
        {
            if (_settings.ModoPrueba)
            {
                yield return CorreoDePrueba();
                yield break;
            }

            foreach (var contacto in notificacion.Contactos)
            {
                if (Parsear(contacto.Email) is { } direccion)
                    yield return direccion;
            }
        }

        /// <summary>
        /// Le fija al mensaje su identidad antes de enviarlo. Dos datos, a propósito redundantes:
        ///
        ///   Message-Id  el que usa el cliente al contestar (In-Reply-To). Es la llave del cruce,
        ///               pero el servidor de salida lo puede reemplazar al aceptar el mensaje.
        ///   token       header propio que ningún servidor toca. Si el Message-Id se reescribe,
        ///               es lo único que sigue relacionando el correo con su renglón en la base.
        ///
        /// Se fija aquí y no se deja al azar porque MimeKit genera uno solo, en silencio, al
        /// serializar: nos enteraríamos del valor después de enviarlo.
        /// </summary>
        private void Sellar(MimeMessage mensaje)
        {
            mensaje.MessageId = MimeUtils.GenerateMessageId(DominioRemitente());
            mensaje.Headers.Add(HeaderToken, Guid.NewGuid().ToString("N"));
        }

        /// <summary>
        /// El dominio del remitente, para que el Message-Id no delate el nombre de la máquina,
        /// que es lo que MimeKit usa si no se le dice otra cosa.
        /// </summary>
        private string DominioRemitente()
        {
            var partes = _settings.RemitenteEmail.Split('@');
            return partes.Length == 2 && !string.IsNullOrWhiteSpace(partes[1])
                ? partes[1]
                : "localhost";
        }

        /// <summary>Lee del mensaje ya armado los tres datos con que se le sigue la pista después.</summary>
        private static IdentidadMensaje Identificar(MimeMessage? mensaje)
        {
            if (mensaje is null)
                return new IdentidadMensaje { MessageId = null, Token = null, Asunto = null };

            var token = mensaje.Headers[HeaderToken];

            return new IdentidadMensaje
            {
                // MimeKit expone el Message-Id sin los <>, que es como se guarda en la base.
                MessageId = mensaje.MessageId,
                Token = Guid.TryParseExact(token, "N", out var guid) ? guid : null,
                Asunto = mensaje.Subject
            };
        }

        private MailboxAddress CorreoDePrueba() =>
            MailboxAddress.Parse(_settings.CorreoPrueba
                ?? throw new InvalidOperationException("Falta 'Smtp:CorreoPrueba' en appsettings.json"));

        private MailboxAddress CorreoDePruebaVendedor() =>
            MailboxAddress.Parse(_settings.CorreoPruebaVendedores
                ?? throw new InvalidOperationException("Falta 'Smtp:CorreoPruebaVendedores' (o 'Smtp:CorreoPrueba') en appsettings.json"));

        /// <summary>Un correo mal capturado en el CRM no debe tumbar el envío del resto.</summary>
        private static MailboxAddress? Parsear(string? email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return null;

            return MailboxAddress.TryParse(email, out var direccion) ? direccion : null;
        }
    }

    /// <summary>Los tres datos con que un correo enviado se vuelve a encontrar después.</summary>
    public class IdentidadMensaje
    {
        public required string? MessageId { get; init; }

        public required Guid? Token { get; init; }

        public required string? Asunto { get; init; }
    }

    public class ResultadoEnvio
    {
        /// <summary>A quién iba el correo: el número de cliente, o el correo del vendedor en la cartera.</summary>
        public required string Cliente { get; init; }

        public required bool Enviado { get; init; }

        public IReadOnlyList<string> Destinatarios { get; init; } = Array.Empty<string>();

        /// <summary>Direcciones que recibieron copia oculta de este correo.</summary>
        public IReadOnlyList<string> CopiaOculta { get; init; } = Array.Empty<string>();

        public string? Error { get; init; }

        /// <summary>Message-Id con el que salió el correo. Es la llave del cruce con las respuestas.</summary>
        public string? MessageId { get; init; }

        /// <summary>El GUID que viajó en el header X-Notificacion-Id.</summary>
        public Guid? Token { get; init; }

        /// <summary>Asunto tal como se envió; el recordatorio lo reutiliza para quedarse en el hilo.</summary>
        public string? Asunto { get; init; }

        public static ResultadoEnvio Exitoso(
            string cliente,
            IReadOnlyList<string> destinatarios,
            IReadOnlyList<string> copiaOculta,
            IdentidadMensaje identidad) =>
            new()
            {
                Cliente = cliente,
                Enviado = true,
                Destinatarios = destinatarios,
                CopiaOculta = copiaOculta,
                MessageId = identidad.MessageId,
                Token = identidad.Token,
                Asunto = identidad.Asunto
            };

        public static ResultadoEnvio Fallido(string cliente, string error, IdentidadMensaje? identidad = null) =>
            new()
            {
                Cliente = cliente,
                Enviado = false,
                Error = error,
                MessageId = identidad?.MessageId,
                Token = identidad?.Token,
                Asunto = identidad?.Asunto
            };
    }
}
