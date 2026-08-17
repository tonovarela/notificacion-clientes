using System;
using System.IO;
using Microsoft.Extensions.Configuration;

namespace notificacion_clientes.Configuracion
{
    /// <summary>
    /// Lee y valida la configuración. Es el único lugar que conoce los nombres de las llaves.
    /// Las variables de entorno pisan a appsettings.json usando '__' como separador de sección
    /// (por ejemplo Smtp__Host o ConnectionStrings__SqlServer), que es como se configura en Docker.
    /// </summary>
    public class AppSettings
    {
        public required string CadenaSqlServer { get; init; }

        public required string UrlDescargaFacturas { get; init; }

        public required int TimeoutApiSegundos { get; init; }

        public required SmtpSettings Smtp { get; init; }

        /// <summary>Ruta absoluta de la plantilla HTML del correo al cliente.</summary>
        public required string RutaPlantilla { get; init; }

        /// <summary>Ruta absoluta de la plantilla HTML de la cartera que se manda al vendedor.</summary>
        public required string RutaPlantillaVendedor { get; init; }

        /// <summary>Ruta absoluta del logo que se incrusta en el correo.</summary>
        public required string RutaLogo { get; init; }

        /// <summary>Directorio donde queda la bitácora de cada ejecución.</summary>
        public required string RutaBitacora { get; init; }

        public static AppSettings Cargar()
        {
            // El archivo es opcional: dentro del contenedor toda la configuración llega por variables de entorno.
            var configuracion = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            return new AppSettings
            {
                CadenaSqlServer = configuracion.GetConnectionString("SqlServer")
                    ?? throw new InvalidOperationException("Falta la cadena de conexión 'SqlServer' (variable ConnectionStrings__SqlServer)"),

                UrlDescargaFacturas = configuracion["ApiFacturas:UrlDescarga"]
                    ?? throw new InvalidOperationException("Falta 'ApiFacturas:UrlDescarga' (variable ApiFacturas__UrlDescarga)"),

                TimeoutApiSegundos = int.TryParse(configuracion["ApiFacturas:TimeoutSegundos"], out var segundos)
                    ? segundos
                    : 60,

                Smtp = SmtpSettings.Cargar(configuracion),

                RutaPlantilla = Path.Combine(
                    AppContext.BaseDirectory,
                    configuracion["Correo:Plantilla"] ?? Path.Combine("Plantillas", "notificacion-cliente.html")),

                RutaPlantillaVendedor = Path.Combine(
                    AppContext.BaseDirectory,
                    configuracion["Correo:PlantillaVendedor"] ?? Path.Combine("Plantillas", "notificacion-vendedor.html")),

                RutaLogo = Path.Combine(
                    AppContext.BaseDirectory,
                    configuracion["Correo:Logo"] ?? Path.Combine("Recursos", "logo.png")),

                // Path.Combine respeta la ruta si viene absoluta, así que sirve para montar un volumen.
                RutaBitacora = Path.Combine(
                    AppContext.BaseDirectory,
                    configuracion["Bitacora:Ruta"] ?? "Logs")
            };
        }
    }
}
