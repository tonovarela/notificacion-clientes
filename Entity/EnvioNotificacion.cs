using System;
using System.Collections.Generic;

namespace notificacion_clientes.Entity
{
    /// <summary>
    /// En qué punto del ciclo está un correo que ya salió.
    ///
    ///   ENVIADO ──(N días)──┬── CONTESTADO
    ///                       └── RECORDADO ──(N días)──┬── CONTESTADO
    ///                                                 └── SIN_RESPUESTA
    ///
    /// FALLIDO queda fuera del ciclo: el correo nunca salió del SMTP, así que no hay nada
    /// que esperar ni a quién insistirle. Eso se atiende a mano con la bitácora del día.
    /// </summary>
    /// <summary>
    /// Qué proceso generó el envío. Vive en la tabla porque los dos comparten buzón y ciclo de
    /// respuestas, pero no comparten política: sólo CLIENTES tiene recordatorio automático, y
    /// COBRANZA usa las respuestas para lo contrario —excluir del correo del viernes a quien ya
    /// contestó el del martes—.
    /// </summary>
    public enum ProcesoEnvio
    {
        /// <summary>Las facturas del día (--clientes).</summary>
        Clientes,

        /// <summary>El estado de cuenta vencido (--cobranza).</summary>
        Cobranza
    }

    public enum EstadoEnvio
    {
        Enviado,
        Fallido,
        Contestado,
        Recordado,
        SinRespuesta
    }

    /// <summary>
    /// Un renglón de CorreosCXC.notif.Envio: un correo que salió y lo que se sabe de su respuesta.
    /// El recordatorio es un renglón aparte, ligado al original por IdEnvioOriginal.
    /// </summary>
    public class EnvioNotificacion
    {
        public int IdEnvio { get; init; }

        public required string Cliente { get; init; }

        public string? RazonSocial { get; init; }

        /// <summary>Qué proceso mandó este correo. Por omisión, las facturas del día.</summary>
        public ProcesoEnvio Proceso { get; init; } = ProcesoEnvio.Clientes;

        /// <summary>Sin los &lt;&gt;, tal como lo expone MimeKit. Es la llave del cruce por hilo.</summary>
        public required string MessageId { get; init; }

        /// <summary>Viaja como header X-Notificacion-Id. Es el id que sí controlamos nosotros.</summary>
        public required Guid Token { get; init; }

        /// <summary>NULL en un envío original; en un recordatorio, el envío al que responde.</summary>
        public int? IdEnvioOriginal { get; init; }

        /// <summary>1 = original, 2 = recordatorio. No existe un 3 por construcción de la consulta.</summary>
        public byte Intento { get; init; } = 1;

        public required string Asunto { get; init; }

        /// <summary>A quién se le mandó de verdad; en modo prueba es el buzón de pruebas.</summary>
        public required string Destinatarios { get; init; }

        /// <summary>True si la corrida fue de prueba. Estos envíos nunca generan recordatorio.</summary>
        public required bool ModoPrueba { get; init; }

        public required DateTime FechaEnvio { get; init; }

        public required EstadoEnvio Estado { get; init; }

        public string? Error { get; init; }

        public DateTime? FechaRespuesta { get; init; }

        public string? RespondioEmail { get; init; }

        public string? RespuestaMessageId { get; init; }

        public string? RespuestaAsunto { get; init; }

        /// <summary>Las facturas que iban en ese correo. Sólo se llena cuando hace falta reenviarlas.</summary>
        public IReadOnlyList<FacturaEnviada> Facturas { get; init; } = Array.Empty<FacturaEnviada>();

        /// <summary>Días naturales transcurridos desde que salió el correo.</summary>
        public int DiasTranscurridos(DateTime ahora) => (int)(ahora.Date - FechaEnvio.Date).TotalDays;
    }

    /// <summary>Una factura que viajó en un envío. Permite rearmar los mismos adjuntos al recordar.</summary>
    public class FacturaEnviada
    {
        public required string MovID { get; init; }

        public required decimal Total { get; init; }

        public required string Moneda { get; init; }
    }

    /// <summary>
    /// Cómo se logró casar una respuesta con su envío. Va a la bitácora porque distingue el
    /// camino exacto del aproximado, y ante una duda es lo primero que se revisa.
    /// </summary>
    public enum CriterioCruce
    {
        /// <summary>El In-Reply-To de la respuesta apunta a nuestro Message-Id. Es el camino normal.</summary>
        InReplyTo,

        /// <summary>Nuestro Message-Id aparece en la cadena References del hilo.</summary>
        References,

        /// <summary>Sin headers de hilo: casó por remitente + asunto. Es el aproximado.</summary>
        RemitenteYAsunto
    }

    /// <summary>Una respuesta detectada en el buzón, ya casada con el envío que la provocó.</summary>
    public class RespuestaDetectada
    {
        public required EnvioNotificacion Envio { get; init; }

        public required string DeEmail { get; init; }

        public required DateTime Fecha { get; init; }

        public required string Asunto { get; init; }

        public required string MessageId { get; init; }

        public required CriterioCruce Criterio { get; init; }

        /// <summary>
        /// True cuando el correo es un rebote (mailer-daemon / postmaster). No es una respuesta:
        /// el envío se marca FALLIDO para que cobranza corrija la dirección en el CRM.
        /// </summary>
        public bool EsRebote { get; init; }
    }
}
