using System;
using Microsoft.Extensions.Configuration;

namespace notificacion_clientes.Configuracion
{
    public class SmtpSettings
    {
        public required string Host { get; init; }

        public required int Puerto { get; init; }

        public required string Usuario { get; init; }

        public required string Password { get; init; }

        public required bool UsarSsl { get; init; }

        public required string RemitenteNombre { get; init; }

        public required string RemitenteEmail { get; init; }

        /// <summary>
        /// Cuando es true, ningún correo llega al cliente: todo se redirige a CorreoPrueba.
        /// Viene activado por omisión para no notificar clientes reales por accidente.
        /// </summary>
        public required bool ModoPrueba { get; init; }

        public string? CorreoPrueba { get; init; }

        public static SmtpSettings Cargar(IConfiguration configuracion)
        {
            var seccion = configuracion.GetSection("Smtp");

            var modoPrueba = !bool.TryParse(seccion["ModoPrueba"], out var prueba) || prueba;
            var correoPrueba = seccion["CorreoPrueba"];

            if (modoPrueba && string.IsNullOrWhiteSpace(correoPrueba))
                throw new InvalidOperationException("Con 'Smtp:ModoPrueba' activo hay que definir 'Smtp:CorreoPrueba'");

            return new SmtpSettings
            {
                Host = seccion["Host"]
                    ?? throw new InvalidOperationException("Falta 'Smtp:Host' en appsettings.json"),

                Puerto = int.TryParse(seccion["Puerto"], out var puerto) ? puerto : 587,

                Usuario = seccion["Usuario"] ?? string.Empty,

                Password = seccion["Password"] ?? string.Empty,

                UsarSsl = !bool.TryParse(seccion["UsarSsl"], out var ssl) || ssl,

                RemitenteNombre = seccion["RemitenteNombre"] ?? "Facturación",

                RemitenteEmail = seccion["RemitenteEmail"]
                    ?? throw new InvalidOperationException("Falta 'Smtp:RemitenteEmail' en appsettings.json"),

                ModoPrueba = modoPrueba,

                CorreoPrueba = correoPrueba
            };
        }
    }
}
