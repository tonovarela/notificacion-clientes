using System;
using System.IO;
using Microsoft.Extensions.Configuration;

namespace notificacion_clientes.Configuracion
{
    /// <summary>
    /// Lee y valida appsettings.json. Es el único lugar que conoce los nombres de las llaves.
    /// </summary>
    public class AppSettings
    {
        public required string CadenaSqlServer { get; init; }

        public required string UrlDescargaFacturas { get; init; }

        public required int TimeoutApiSegundos { get; init; }

        public required SmtpSettings Smtp { get; init; }

        /// <summary>Ruta absoluta de la plantilla HTML del correo.</summary>
        public required string RutaPlantilla { get; init; }

        /// <summary>Ruta absoluta del logo que se incrusta en el correo.</summary>
        public required string RutaLogo { get; init; }

        public static AppSettings Cargar()
        {
            var configuracion = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            return new AppSettings
            {
                CadenaSqlServer = configuracion.GetConnectionString("SqlServer")
                    ?? throw new InvalidOperationException("Falta la cadena de conexión 'SqlServer' en appsettings.json"),

                UrlDescargaFacturas = configuracion["ApiFacturas:UrlDescarga"]
                    ?? throw new InvalidOperationException("Falta 'ApiFacturas:UrlDescarga' en appsettings.json"),

                TimeoutApiSegundos = int.TryParse(configuracion["ApiFacturas:TimeoutSegundos"], out var segundos)
                    ? segundos
                    : 60,

                Smtp = SmtpSettings.Cargar(configuracion),

                RutaPlantilla = Path.Combine(
                    AppContext.BaseDirectory,
                    configuracion["Correo:Plantilla"] ?? Path.Combine("Plantillas", "notificacion-cliente.html")),

                RutaLogo = Path.Combine(
                    AppContext.BaseDirectory,
                    configuracion["Correo:Logo"] ?? Path.Combine("Recursos", "logo.png"))
            };
        }
    }
}
