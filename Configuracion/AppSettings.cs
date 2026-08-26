using System;
using System.IO;
using System.Linq;
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

        /// <summary>Política de acuse y recordatorio.</summary>
        /// <summary>Lectura del buzón para detectar quién contestó.</summary>
        public required ImapSettings Imap { get; init; }

        public required SeguimientoSettings Seguimiento { get; init; }

        /// <summary>Ruta absoluta de la plantilla HTML del estado de cuenta vencido (martes).</summary>
        public required string RutaPlantillaCobranza { get; init; }

        /// <summary>Ruta absoluta de la plantilla del recordatorio de cobranza (viernes).</summary>
        public required string RutaPlantillaCobranzaRecordatorio { get; init; }

        /// <summary>
        /// Carpeta con JSON de prueba (facturas.json, cobranza-vencida.json,
        /// revision-vendedores.json) que sustituye a la consulta SQL cuando no es null. Se
        /// activa con 'DatosPrueba:Ruta' (variable DatosPrueba__Ruta); útil por VPN, donde el
        /// servidor de base de datos responde lento.
        /// </summary>
        public string? RutaDatosPrueba { get; init; }

        public static AppSettings Cargar()
        {
            // El archivo es opcional: dentro del contenedor toda la configuración llega por variables de entorno.
            var configuracion = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            // Se resuelve antes del objeto porque Imap hereda de aquí usuario y contraseña.
            var smtp = SmtpSettings.Cargar(configuracion);

            return new AppSettings
            {
                CadenaSqlServer = configuracion.GetConnectionString("SqlServer")
                    ?? throw new InvalidOperationException("Falta la cadena de conexión 'SqlServer' (variable ConnectionStrings__SqlServer)"),

                UrlDescargaFacturas = configuracion["ApiFacturas:UrlDescarga"]
                    ?? throw new InvalidOperationException("Falta 'ApiFacturas:UrlDescarga' (variable ApiFacturas__UrlDescarga)"),

                TimeoutApiSegundos = int.TryParse(configuracion["ApiFacturas:TimeoutSegundos"], out var segundos)
                    ? segundos
                    : 60,

                Smtp = smtp,


                Imap = ImapSettings.Cargar(configuracion, smtp),

                Seguimiento = SeguimientoSettings.Cargar(configuracion),

                RutaPlantilla = Path.Combine(
                    AppContext.BaseDirectory,
                    configuracion["Correo:Plantilla"] ?? Path.Combine("Plantillas", "notificacion-cliente.html")),

                RutaPlantillaCobranza = Path.Combine(
                    AppContext.BaseDirectory,
                    configuracion["Correo:PlantillaCobranza"] ?? Path.Combine("Plantillas", "cobranza-vencida.html")),

                RutaPlantillaCobranzaRecordatorio = Path.Combine(
                    AppContext.BaseDirectory,
                    configuracion["Correo:PlantillaCobranzaRecordatorio"] ?? Path.Combine("Plantillas", "cobranza-recordatorio.html")),

                RutaPlantillaVendedor = Path.Combine(
                    AppContext.BaseDirectory,
                    configuracion["Correo:PlantillaVendedor"] ?? Path.Combine("Plantillas", "notificacion-vendedor.html")),

                RutaDatosPrueba = string.IsNullOrWhiteSpace(configuracion["DatosPrueba:Ruta"])
                    ? null
                    : Path.Combine(RaizProyecto, configuracion["DatosPrueba:Ruta"]!),

                RutaLogo = Path.Combine(
                    AppContext.BaseDirectory,
                    configuracion["Correo:Logo"] ?? Path.Combine("Recursos", "logo.png")),

                // Path.Combine respeta la ruta si viene absoluta, así que sirve para montar un volumen.
                // La ruta relativa cuelga de la raíz del proyecto, no del ejecutable: si no, en
                // desarrollo las bitácoras quedarían enterradas en bin/Debug/net8.0/Logs.
                RutaBitacora = Path.Combine(
                    RaizProyecto,
                    configuracion["Bitacora:Ruta"] ?? "Logs")
            };
        }

        /// <summary>
        /// Directorio donde se resuelven las rutas relativas que escribe el programa.
        /// Se sube por el árbol desde el ejecutable hasta encontrar el .csproj: eso deja las
        /// bitácoras en la raíz del proyecto durante el desarrollo, en vez de dentro de bin/.
        /// En la imagen publicada no hay .csproj, así que se queda en /app y nada cambia.
        /// </summary>
        private static string RaizProyecto { get; } = LocalizarRaizProyecto();

        private static string LocalizarRaizProyecto()
        {
            var directorio = new DirectoryInfo(AppContext.BaseDirectory);

            while (directorio is not null)
            {
                if (directorio.EnumerateFiles("*.csproj").Any())
                    return directorio.FullName;

                directorio = directorio.Parent;
            }

            return AppContext.BaseDirectory;
        }
    }
}
