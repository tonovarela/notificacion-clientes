using System;
using System.Collections.Generic;

namespace notificacion_clientes.Entity
{
    /// <summary>
    /// Qué proceso generó el envío. Vive en la tabla porque los dos comparten buzón, pero no
    /// comparten política: sólo COBRANZA vuelve a leer estos renglones, y lo hace desde la
    /// consulta de facturas para saber qué ya se notificó y sigue sin respuesta.
    /// </summary>
    public enum ProcesoEnvio
    {
        /// <summary>Las facturas del día (--clientes).</summary>
        Clientes,

        /// <summary>El estado de cuenta vencido (--cobranza).</summary>
        Cobranza
    }

    /// <summary>
    /// En qué acabó un correo que ya salió.
    ///
    /// Sólo ENVIADO y FALLIDO los escribe la aplicación sola, al registrar la corrida. Los otros
    /// tres exigen que alguien más los ponga: desde que se quitó la conciliación por IMAP no hay
    /// proceso que lea el buzón, así que un envío se queda en ENVIADO hasta que se actualice a
    /// mano o desde otro sistema. Eso importa porque el recordatorio de cobranza descarta lo que
    /// esté en CONTESTADO: mientras nadie lo marque, se sigue insistiendo.
    /// </summary>
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
    /// Sólo el primer aviso llega aquí: el recordatorio no se registra, así que una factura tiene
    /// a lo más un renglón por más veces que se le insista.
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

        /// <summary>
        /// El envío al que responde. Hoy siempre NULL: para ligarlos había que recuperar el envío
        /// del martes desde el seguimiento, y esa consulta desapareció junto con la conciliación.
        /// La columna se conserva porque los renglones viejos sí la traen.
        /// </summary>
        public int? IdEnvioOriginal { get; init; }

        /// <summary>
        /// Siempre 1 hoy: el recordatorio dejó de registrarse, así que no se escribe ningún 2.
        /// La columna se conserva porque los renglones viejos sí lo traen.
        /// </summary>
        public byte Intento { get; init; } = 1;

        public required string Asunto { get; init; }

        /// <summary>A quién se le mandó de verdad; en modo prueba es el buzón de pruebas.</summary>
        public required string Destinatarios { get; init; }

        /// <summary>True si la corrida fue de prueba.</summary>
        public required bool ModoPrueba { get; init; }

        public required DateTime FechaEnvio { get; init; }

        public required EstadoEnvio Estado { get; init; }

        public string? Error { get; init; }

        public DateTime? FechaRespuesta { get; init; }

        public string? RespondioEmail { get; init; }

        public string? RespuestaMessageId { get; init; }

        public string? RespuestaAsunto { get; init; }

        /// <summary>
        /// Los Message-Id de TODOS los recordatorios que han cubierto a este envío, del más viejo
        /// al más reciente. Vacío si nunca se le ha insistido.
        ///
        /// El recordatorio no genera renglón propio —duplicaría MovIDs en EnvioFactura y partiría
        /// el estado del cliente entre varios envíos—, así que su identidad se guarda aquí. Se
        /// conservan todos y no sólo el último: un cliente que arrastra el correo viejo en su
        /// bandeja y contesta ahí quedaría sin detectar si sólo se recordara el más reciente.
        ///
        /// Un mismo recordatorio puede abarcar facturas de semanas distintas, y por tanto varios
        /// envíos: todos comparten ese id, así que una sola respuesta los cierra de golpe.
        /// </summary>
        public IReadOnlyList<string> RecordatorioMessageIds { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Cuándo salió el último recordatorio que cubrió a este envío. Null si nunca se le ha
        /// insistido, en cuyo caso el mensaje más reciente es el envío mismo y la fecha que cuenta
        /// es <see cref="FechaEnvio"/>.
        ///
        /// Es lo que decide si el envío sigue dentro de la ventana de conciliación. Medirla contra
        /// FechaEnvio dejaba fuera justo a los morosos más viejos: un envío de hace meses al que se
        /// le sigue insistiendo cada viernes no entraba al cruce, así que su respuesta al
        /// recordatorio de ayer no se detectaba y se le volvía a escribir.
        /// </summary>
        public DateTime? FechaUltimoRecordatorio { get; init; }

        /// <summary>
        /// Las facturas que iban en ese correo. Es lo que hace posible el recordatorio: la consulta
        /// de cobranza sin contestar cruza contra estos renglones para saber qué ya se reclamó.
        /// </summary>
        public IReadOnlyList<FacturaEnviada> Facturas { get; init; } = Array.Empty<FacturaEnviada>();
    }

    /// <summary>Una factura que viajó en un envío.</summary>
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
        RemitenteYAsunto,

        /// <summary>
        /// El aviso de no entrega devolvió el identificador de sobre con el que salió el correo,
        /// que es nuestro token. Es el cruce más exacto de todos: no pasa por ningún header que
        /// el servidor del cliente pueda haber perdido.
        /// </summary>
        EnvelopeId,

        /// <summary>
        /// Sólo para rebotes: el aviso no traía con qué identificar el envío, pero la dirección
        /// que reporta como fallida es una de las que se notificaron. Es el aproximado del rebote.
        /// </summary>
        DestinatarioDelRebote
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
        /// Cuando la respuesta casó contra el Message-Id de un recordatorio y no contra el del
        /// envío original. Trae ese id, y es la señal de que hay que cerrar todos los envíos que
        /// aquel recordatorio cubría, no sólo el que casó.
        /// </summary>
        public string? RespondioARecordatorio { get; init; }

        /// <summary>
        /// True cuando el correo es un aviso de no entrega y no una respuesta del cliente.
        /// </summary>
        public bool EsRebote { get; init; }

        /// <summary>
        /// Lo que dice el reporte formal del rebote. Null cuando el correo se identificó como
        /// rebote sólo por venir de mailer-daemon, sin un delivery-status que leer: en ese caso
        /// no hay forma de saber si el fallo era definitivo, y se trata como si lo fuera.
        /// </summary>
        public InformeRebote? Rebote { get; init; }

        /// <summary>
        /// Si este rebote cierra el envío. Un retraso no cierra nada: el correo sigue en cola del
        /// otro lado y puede entregarse solo, así que marcarlo FALLIDO sería adelantarse.
        /// </summary>
        public bool CierraElEnvio => EsRebote && (Rebote?.EsDefinitivo ?? true);
    }
}
