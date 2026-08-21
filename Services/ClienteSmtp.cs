using System.Collections.Generic;
using MailKit;
using MailKit.Net.Smtp;
using MimeKit;

namespace notificacion_clientes.Services
{
    /// <summary>
    /// El SmtpClient de MailKit, pero tolerante a un destinatario que el servidor no acepta.
    ///
    /// De fábrica, el primer RCPT TO rechazado aborta el mensaje completo: un correo mal
    /// capturado en el CRM deja sin estado de cuenta a los otros dos contactos del mismo
    /// cliente, que sí estaban bien. Aquí el rechazo se anota y el envío sigue con los demás.
    ///
    /// Si al final no quedó ningún destinatario, MailKit lanza igual desde OnNoRecipientsAccepted
    /// —que no se toca a propósito— y el envío se registra FALLIDO como siempre.
    ///
    /// Además le pide al servidor que avise cuando un correo no se pueda entregar, y sella el
    /// sobre con nuestro token para que ese aviso se pueda reconocer al leerlo del buzón.
    /// </summary>
    public class ClienteSmtp : SmtpClient
    {
        private readonly List<DestinatarioRechazado> _rechazados = new();

        /// <summary>Las direcciones que el servidor no aceptó del mensaje en curso.</summary>
        public IReadOnlyList<DestinatarioRechazado> Rechazados => _rechazados;

        /// <summary>
        /// Vacía la lista antes de cada mensaje. La conexión se reutiliza para todo el lote, así
        /// que sin esto el rechazo de un cliente se le atribuiría también al siguiente.
        /// </summary>
        public void Reiniciar() => _rechazados.Clear();

        /// <summary>
        /// True si el servidor anuncia la extensión DSN (RFC 3461). Sin ella no se puede pedir
        /// el aviso de no entrega ni sellar el sobre: MailKit omite ENVID y NOTIFY en silencio,
        /// y el rebote —si llega— habrá que casarlo por el hilo, que es el camino aproximado.
        /// Se consulta después de conectar, que es cuando el servidor ya declaró qué soporta.
        /// </summary>
        public bool SoportaAvisoDeNoEntrega => Capabilities.HasFlag(SmtpCapabilities.Dsn);

        /// <summary>
        /// El identificador del sobre (ENVID del MAIL FROM). Se usa nuestro token y no el
        /// Message-Id porque el token es el único id que ningún servidor reescribe.
        ///
        /// Es lo que convierte el rebote en un dato exacto: el aviso de no entrega devuelve este
        /// mismo valor en Original-Envelope-Id, así que se sabe de qué envío es sin depender de
        /// que el servidor del cliente haya conservado los headers del hilo.
        /// </summary>
        protected override string GetEnvelopeId(MimeMessage mensaje) =>
            mensaje.Headers[CorreoService.HeaderToken] ?? string.Empty;

        /// <summary>
        /// Qué avisos se le piden al servidor: los fracasos y los retrasos, nunca los éxitos.
        ///
        /// Un acuse por cada entrega correcta inundaría el buzón de cobranza —que es el mismo
        /// que se lee para detectar respuestas— y no diría nada que el envío no sepa ya.
        /// </summary>
        protected override DeliveryStatusNotification? GetDeliveryStatusNotifications(
            MimeMessage mensaje,
            MailboxAddress destinatario) =>
            DeliveryStatusNotification.Failure | DeliveryStatusNotification.Delay;

        /// <summary>
        /// A propósito no llama a base: la implementación de MailKit lanza la excepción, y con
        /// ella se pierde el correo para los destinatarios que el servidor sí había aceptado.
        /// </summary>
        protected override void OnRecipientNotAccepted(MimeMessage mensaje, MailboxAddress destinatario, SmtpResponse respuesta)
        {
            _rechazados.Add(new DestinatarioRechazado
            {
                Email = destinatario.Address,
                Codigo = (int)respuesta.StatusCode,
                Respuesta = respuesta.Response?.Trim() ?? string.Empty
            });
        }
    }

    /// <summary>Una dirección que el servidor de salida rechazó al recibir el RCPT TO.</summary>
    public class DestinatarioRechazado
    {
        public required string Email { get; init; }

        /// <summary>
        /// El código SMTP. Un 5xx es definitivo —550 típicamente es buzón inexistente— y hay que
        /// corregir el dato en el CRM; un 4xx es temporal y puede llegar en el siguiente envío.
        /// </summary>
        public required int Codigo { get; init; }

        /// <summary>El texto tal cual lo devolvió el servidor; es lo que se le muestra a cobranza.</summary>
        public required string Respuesta { get; init; }

        /// <summary>Los 4xx son temporales: no ameritan corregir nada en el CRM.</summary>
        public bool EsDefinitivo => Codigo >= 500;

        public override string ToString() => $"{Email} ({Codigo} {Respuesta})";
    }
}
