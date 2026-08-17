
using notificacion_clientes.Configuracion;
using notificacion_clientes.DAO;
using notificacion_clientes.Entity;
using notificacion_clientes.Services;

namespace notificacion_clientes
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var inicio = DateTime.Now;
            var settings = AppSettings.Cargar();
            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(settings.TimeoutApiSegundos)
            };
            var facturaDAO = new FacturaDAO(settings.CadenaSqlServer);
            var descargaService = new FacturaDescargaService(httpClient, settings.UrlDescargaFacturas);
            var notificacionService = new NotificacionService(facturaDAO, descargaService, new LectorCfdi());
            var revisionVendedorService = new RevisionVendedorService(facturaDAO);
            var plantillaService = new PlantillaService(settings.RutaPlantilla);
            var plantillaVendedorService = new PlantillaVendedorService(settings.RutaPlantillaVendedor);
            var correoService = new CorreoService(settings.Smtp, plantillaService, plantillaVendedorService, settings.RutaLogo);
            var bitacora = new BitacoraService(settings.RutaBitacora, settings.Smtp);
            var reporte = new ReporteConsola();
            var previsualizar = args.Contains("--previsualizar");

            // Dos procesos distintos comparten el mismo ejecutable porque comparten configuración,
            // SMTP y bitácora. Se eligen por argumento para poder programarlos en horarios distintos.
            if (args.Contains("--vendedores"))
            {
                await EjecutarVendedores(
                    revisionVendedorService, plantillaVendedorService, correoService,
                    bitacora, reporte, settings, inicio, previsualizar);
                return;
            }

            await EjecutarClientes(
                notificacionService, plantillaService, correoService,
                bitacora, reporte, settings, inicio, previsualizar);
        }

        private static async Task EjecutarClientes(
            NotificacionService notificacionService,
            PlantillaService plantillaService,
            CorreoService correoService,
            BitacoraService bitacora,
            ReporteConsola reporte,
            AppSettings settings,
            DateTime inicio,
            bool previsualizar)
        {
            // Si algo truena a medio camino igual se deja la evidencia en disco con el motivo:
            // una bitácora que falta no sirve para aclarar nada después.
            IReadOnlyList<NotificacionCliente> notificaciones = Array.Empty<NotificacionCliente>();
            IReadOnlyList<ResultadoEnvio> resultados = Array.Empty<ResultadoEnvio>();
            string? errorFatal = null;

            try
            {
                Console.WriteLine("Obteniendo facturas...");
                notificaciones = await notificacionService.Preparar();
                reporte.Imprimir(notificaciones);

                // Con --previsualizar se genera el HTML en disco y no se envía nada: sirve para revisar la plantilla.
                if (previsualizar)
                {
                    foreach (var notificacion in notificaciones)
                        await GuardarPrevisualizacion(
                            $"previsualizacion-cliente-{notificacion.Cliente}.html",
                            plantillaService.Renderizar(notificacion));

                    return;
                }

                Console.WriteLine();
                Console.WriteLine("Enviando correos...");
                resultados = await correoService.Enviar(notificaciones);
                reporte.ImprimirEnvios(resultados, settings.Smtp.ModoPrueba);
            }
            catch (Exception ex)
            {
                errorFatal = ex.Message;
                Console.WriteLine($"ERROR: la ejecución no se completó: {ex.Message}");
                Environment.ExitCode = 1;
            }

            var rutaBitacora = await bitacora.Escribir(notificaciones, resultados, inicio, errorFatal);
            Console.WriteLine();
            Console.WriteLine($"Bitácora: {rutaBitacora}");
        }

        /// <summary>Avisa a cada vendedor de las facturas que sus clientes no han ingresado a revisión.</summary>
        private static async Task EjecutarVendedores(
            RevisionVendedorService revisionVendedorService,
            PlantillaVendedorService plantillaVendedorService,
            CorreoService correoService,
            BitacoraService bitacora,
            ReporteConsola reporte,
            AppSettings settings,
            DateTime inicio,
            bool previsualizar)
        {
            IReadOnlyList<NotificacionVendedor> notificaciones = Array.Empty<NotificacionVendedor>();
            IReadOnlyList<ResultadoEnvio> resultados = Array.Empty<ResultadoEnvio>();
            string? errorFatal = null;

            try
            {
                Console.WriteLine("Obteniendo facturas sin ingresar a revisión...");
                notificaciones = await revisionVendedorService.Preparar();
                reporte.ImprimirVendedores(notificaciones);

                if (previsualizar)
                {
                    foreach (var notificacion in notificaciones)
                        await GuardarPrevisualizacion(
                            $"previsualizacion-vendedor-{Sanear(notificacion.Email)}.html",
                            plantillaVendedorService.Renderizar(notificacion));

                    return;
                }

                Console.WriteLine();
                Console.WriteLine("Enviando correos a vendedores...");
                resultados = await correoService.EnviarVendedores(notificaciones);
                reporte.ImprimirEnvios(resultados, settings.Smtp.ModoPruebaVendedores);
            }
            catch (Exception ex)
            {
                errorFatal = ex.Message;
                Console.WriteLine($"ERROR: la ejecución no se completó: {ex.Message}");
                Environment.ExitCode = 1;
            }

            var rutaBitacora = await bitacora.EscribirVendedores(notificaciones, resultados, inicio, errorFatal);
            Console.WriteLine();
            Console.WriteLine($"Bitácora: {rutaBitacora}");
        }

        private static async Task GuardarPrevisualizacion(string nombreArchivo, string html)
        {
            var ruta = Path.Combine(AppContext.BaseDirectory, nombreArchivo);
            await File.WriteAllTextAsync(ruta, html);
            Console.WriteLine($"Previsualización: {ruta}");
        }

        /// <summary>El correo del vendedor se usa como nombre de archivo; la arroba y el punto estorban.</summary>
        private static string Sanear(string valor) =>
            string.Concat(valor.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
    }
}
