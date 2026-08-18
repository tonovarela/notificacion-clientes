using System;
using Microsoft.Extensions.Configuration;

namespace notificacion_clientes.Configuracion
{
    /// <summary>
    /// Acceso de lectura al buzón que envía los correos, para detectar quién contestó.
    ///
    /// Por omisión reutiliza el usuario y la contraseña de Smtp: es la misma cuenta y tener la
    /// credencial escrita dos veces es cómo se terminan desincronizando. Se pueden separar si
    /// algún día el buzón de lectura es otro.
    /// </summary>
    public class ImapSettings
    {
        public required string Host { get; init; }

        public required int Puerto { get; init; }

        public required string Usuario { get; init; }

        public required string Password { get; init; }

        /// <summary>Carpeta a leer. INBOX salvo que las respuestas se archiven en otro lado.</summary>
        public required string Carpeta { get; init; }

        public static ImapSettings Cargar(IConfiguration configuracion, SmtpSettings smtp)
        {
            var seccion = configuracion.GetSection("Imap");

            return new ImapSettings
            {
                Host = string.IsNullOrWhiteSpace(seccion["Host"]) ? "imap.gmail.com" : seccion["Host"]!,

                Puerto = int.TryParse(seccion["Puerto"], out var puerto) ? puerto : 993,

                // Vacío en la configuración significa "la misma cuenta que manda", no "sin usuario".
                Usuario = string.IsNullOrWhiteSpace(seccion["Usuario"]) ? smtp.Usuario : seccion["Usuario"]!,

                Password = string.IsNullOrWhiteSpace(seccion["Password"]) ? smtp.Password : seccion["Password"]!,

                Carpeta = string.IsNullOrWhiteSpace(seccion["Carpeta"]) ? "INBOX" : seccion["Carpeta"]!
            };
        }
    }
}
