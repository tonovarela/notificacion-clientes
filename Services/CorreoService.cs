using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
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

        private readonly SmtpSettings _settings;
        private readonly PlantillaService _plantillaService;
        private readonly PlantillaVendedorService _plantillaVendedorService;
        private readonly string _rutaLogo;

        public CorreoService(
            SmtpSettings settings,
            PlantillaService plantillaService,
            PlantillaVendedorService plantillaVendedorService,
            string rutaLogo)
        {
            _settings = settings;
            _plantillaService = plantillaService;
            _plantillaVendedorService = plantillaVendedorService;
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

                try
                {
                    var mensaje = armarMensaje(notificacion, copiaOculta);

                    if (mensaje.To.Count == 0)
                    {
                        resultados.Add(ResultadoEnvio.Fallido(clave, "No hay ninguna dirección de correo válida a la cual enviar"));
                        continue;
                    }

                    await cliente.SendAsync(mensaje, cancelacion);
                    resultados.Add(ResultadoEnvio.Exitoso(
                        clave,
                        mensaje.To.Mailboxes.Select(m => m.Address).ToList(),
                        mensaje.Bcc.Mailboxes.Select(m => m.Address).ToList()));
                }
                catch (Exception ex)
                {
                    resultados.Add(ResultadoEnvio.Fallido(clave, ex.Message));
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

    public class ResultadoEnvio
    {
        /// <summary>A quién iba el correo: el número de cliente, o el correo del vendedor en la cartera.</summary>
        public required string Cliente { get; init; }

        public required bool Enviado { get; init; }

        public IReadOnlyList<string> Destinatarios { get; init; } = Array.Empty<string>();

        /// <summary>Direcciones que recibieron copia oculta de este correo.</summary>
        public IReadOnlyList<string> CopiaOculta { get; init; } = Array.Empty<string>();

        public string? Error { get; init; }

        public static ResultadoEnvio Exitoso(
            string cliente,
            IReadOnlyList<string> destinatarios,
            IReadOnlyList<string> copiaOculta) =>
            new() { Cliente = cliente, Enviado = true, Destinatarios = destinatarios, CopiaOculta = copiaOculta };

        public static ResultadoEnvio Fallido(string cliente, string error) =>
            new() { Cliente = cliente, Enviado = false, Error = error };
    }
}
