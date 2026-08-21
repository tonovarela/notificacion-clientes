using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MimeKit;
using notificacion_clientes.Entity;

namespace notificacion_clientes.Services
{
    /// <summary>
    /// Reconoce un aviso de no entrega en el buzón y le saca lo que dice.
    ///
    /// El camino anterior era mirar de quién venía el correo: si el remitente era mailer-daemon
    /// o postmaster, se daba por rebote. Eso falla en las dos direcciones —Exchange Online manda
    /// sus avisos desde direcciones que no se llaman así, y un correo de postmaster puede no ser
    /// un rebote— y además no distingue "el buzón no existe" de "vuelvo a intentar en una hora".
    ///
    /// Aquí se usa la estructura formal que define el RFC 3464: el aviso es un multipart/report
    /// con una parte message/delivery-status que trae la dirección, el código y de qué envío era.
    /// Eso no depende del idioma del servidor ni de cómo redactó el texto para el humano.
    /// </summary>
    public class LectorRebote
    {
        /// <summary>
        /// Si el correo tiene forma de aviso de no entrega. Se decide con la estructura que ya
        /// vino en el FETCH, sin bajar nada: la mayoría de los correos del buzón no son rebotes
        /// y no vale la pena pedirle al servidor el cuerpo de cada uno para descartarlos.
        /// </summary>
        public static bool PareceAvisoDeNoEntrega(IMessageSummary resumen) =>
            LocalizarReporte(resumen.Body) is not null;

        /// <summary>
        /// Baja el reporte y lo interpreta. Sólo se descarga la parte del delivery-status —unos
        /// cientos de bytes—, nunca el correo completo: los avisos suelen traer adjunto el
        /// mensaje original entero, y ése ya lo mandamos nosotros.
        /// </summary>
        public async Task<InformeRebote?> Leer(
            IMailFolder carpeta,
            IMessageSummary resumen,
            CancellationToken cancelacion = default)
        {
            var reporte = LocalizarReporte(resumen.Body);

            if (reporte is null)
                return null;

            var parte = await carpeta.GetBodyPartAsync(resumen.UniqueId, reporte, cancelacion);

            return parte is MessageDeliveryStatus estado ? Interpretar(estado) : null;
        }

        /// <summary>
        /// Lee los grupos de campos del reporte. Vienen en dos bloques: uno del mensaje —que trae
        /// el Original-Envelope-Id, o sea nuestro token— y uno por cada destinatario.
        ///
        /// Cuando el aviso cubre a varios destinatarios se devuelve el más grave: un correo que
        /// falló para uno y se retrasó para otro es, para efectos de cobranza, un correo que falló.
        /// </summary>
        public static InformeRebote? Interpretar(MessageDeliveryStatus estado)
        {
            string? envelopeId = null;
            string? servidor = null;
            var destinatarios = new List<InformeRebote>();

            foreach (var grupo in estado.StatusGroups)
            {
                // Los campos del bloque del mensaje no se repiten por destinatario, así que se
                // recogen de donde aparezcan y luego se le pegan a cada uno.
                envelopeId ??= Limpiar(grupo["Original-Envelope-Id"]);
                servidor ??= SinTipo(Limpiar(grupo["Reporting-MTA"]));

                var accion = Limpiar(grupo["Action"]);
                var codigo = Limpiar(grupo["Status"]);

                // Sin ninguno de los dos no es un bloque de destinatario, es el del mensaje.
                if (accion is null && codigo is null)
                    continue;

                destinatarios.Add(new InformeRebote
                {
                    Resultado = Clasificar(accion, codigo),
                    // El original es el que nosotros escribimos; el final, al que acabó
                    // resolviéndose tras los reenvíos. Se prefiere el nuestro para poder casarlo.
                    Destinatario = SinTipo(Limpiar(grupo["Original-Recipient"]))
                                   ?? SinTipo(Limpiar(grupo["Final-Recipient"])),
                    Estado = codigo,
                    Diagnostico = Limpiar(grupo["Diagnostic-Code"])
                });
            }

            if (destinatarios.Count == 0)
                return null;

            var peor = destinatarios
                .OrderBy(d => d.Resultado switch
                {
                    ResultadoEntrega.Fallida => 0,
                    ResultadoEntrega.Retrasada => 1,
                    _ => 2
                })
                .First();

            return new InformeRebote
            {
                Resultado = peor.Resultado,
                Destinatario = peor.Destinatario,
                Estado = peor.Estado,
                Diagnostico = peor.Diagnostico,
                EnvelopeIdOriginal = envelopeId,
                ServidorQueReporta = servidor
            };
        }

        /// <summary>
        /// El campo Action es el que manda: dice si el servidor se rindió o si sigue intentando.
        /// El código sólo se usa cuando el aviso viene sin Action, que no debería pasar pero pasa.
        /// </summary>
        private static ResultadoEntrega Clasificar(string? accion, string? codigo)
        {
            if (accion is not null)
            {
                if (accion.Equals("failed", StringComparison.OrdinalIgnoreCase))
                    return ResultadoEntrega.Fallida;

                if (accion.Equals("delayed", StringComparison.OrdinalIgnoreCase))
                    return ResultadoEntrega.Retrasada;

                // delivered, relayed y expanded: el correo siguió su camino.
                return ResultadoEntrega.Entregada;
            }

            return codigo?.FirstOrDefault() switch
            {
                '5' => ResultadoEntrega.Fallida,
                '4' => ResultadoEntrega.Retrasada,
                _ => ResultadoEntrega.Entregada
            };
        }

        /// <summary>
        /// La parte message/delivery-status dentro de un multipart/report, si la hay.
        ///
        /// Se exige el report-type=delivery-status del RFC 3462: hay otros multipart/report que
        /// no son rebotes —los acuses de lectura, por ejemplo— y confundirlos cerraría envíos
        /// que en realidad sí llegaron.
        /// </summary>
        public static BodyPart? LocalizarReporte(BodyPart? cuerpo)
        {
            if (cuerpo is not BodyPartMultipart multiparte)
                return null;

            if (multiparte.ContentType.IsMimeType("multipart", "report"))
            {
                var tipo = multiparte.ContentType.Parameters["report-type"];

                if (!string.IsNullOrWhiteSpace(tipo)
                    && !tipo.Equals("delivery-status", StringComparison.OrdinalIgnoreCase))
                    return null;

                var reporte = multiparte.BodyParts
                    .FirstOrDefault(p => p.ContentType.IsMimeType("message", "delivery-status"));

                if (reporte is not null)
                    return reporte;
            }

            // Algunos servidores envuelven el reporte en otro multipart (por ejemplo cuando le
            // agregan una firma), así que se busca hacia adentro antes de darlo por perdido.
            return multiparte.BodyParts
                .Select(LocalizarReporte)
                .FirstOrDefault(encontrado => encontrado is not null);
        }

        /// <summary>
        /// Los campos vienen calificados por tipo: 'rfc822; cliente@dominio.com', 'dns; correo.com'.
        /// Al cruzar sólo sirve lo que va después del punto y coma.
        /// </summary>
        private static string? SinTipo(string? valor)
        {
            if (valor is null)
                return null;

            var separador = valor.IndexOf(';');

            return separador < 0 ? valor : valor[(separador + 1)..].Trim();
        }

        private static string? Limpiar(string? valor) =>
            string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();
    }
}
