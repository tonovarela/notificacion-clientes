
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
            var plantillaService = new PlantillaService(settings.RutaPlantilla);
            var correoService = new CorreoService(settings.Smtp, plantillaService, settings.RutaLogo);
            var bitacora = new BitacoraService(settings.RutaBitacora, settings.Smtp);
            var reporte = new ReporteConsola();

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
                // if (args.Contains("--previsualizar"))
                // {
                //     foreach (var notificacion in notificaciones)
                //     {
                //         var ruta = Path.Combine(AppContext.BaseDirectory, $"previsualizacion-{notificacion.Cliente}.html");
                //         await File.WriteAllTextAsync(ruta, plantillaService.Renderizar(notificacion));
                //         Console.WriteLine($"Previsualización: {ruta}");
                //     }
                //     return;
                // }
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
    }
}
