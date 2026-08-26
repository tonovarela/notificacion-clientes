using System;
using System.IO;
using System.Net.Http;
using notificacion_clientes.Configuracion;
using notificacion_clientes.DAO;
using notificacion_clientes.Services;

namespace notificacion_clientes.Comandos
{
    /// <summary>
    /// Arma todo lo que la aplicación necesita, una sola vez y en un solo lugar.
    ///
    /// Vive aparte de Program porque los comandos comparten casi todas las piezas —SMTP,
    /// bitácora, plantillas— y tenerlas en el Main hacía crecer un método que ya sólo debería
    /// decidir qué comando corre.
    /// </summary>
    public sealed class Dependencias : IDisposable
    {
        private readonly HttpClient _http;

        public Dependencias(AppSettings settings)
        {
            Settings = settings;

            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(settings.TimeoutApiSegundos) };

            var usaDatosPrueba = !string.IsNullOrWhiteSpace(settings.RutaDatosPrueba);
            FacturaDAO = usaDatosPrueba
                ? new FacturaDAOJson(settings.RutaDatosPrueba!)
                : new FacturaDAO(settings.CadenaSqlServer);
            var facturaDAO = FacturaDAO;
            var descargaService = new FacturaDescargaService(_http, settings.UrlDescargaFacturas);

            SeguimientoDAO = usaDatosPrueba
                ? new SeguimientoDAOJson(Path.Combine(settings.RutaDatosPrueba!, "envios.json"))
                : new SeguimientoDAO(settings.CadenaSqlServer);

            Notificacion = new NotificacionService(
                facturaDAO, descargaService, new LectorCfdi(), 
                
                omitirDescargaArchivos: usaDatosPrueba);

            RevisionVendedor = new RevisionVendedorService(facturaDAO);

            Plantilla = new PlantillaService(settings.RutaPlantilla);
            PlantillaVendedor = new PlantillaVendedorService(settings.RutaPlantillaVendedor);
            PlantillaCobranza = new PlantillaCobranzaService(
                settings.RutaPlantillaCobranza, settings.RutaPlantillaCobranzaRecordatorio);

            Correo = new CorreoService(
                settings.Smtp, Plantilla, PlantillaVendedor, PlantillaCobranza, settings.RutaLogo);

            Bitacora = new BitacoraService(settings.RutaBitacora, settings.Smtp);
            Reporte = new ReporteConsola();

            Respuesta = new RespuestaService(settings.Imap);
            CobranzaVencida = new CobranzaVencidaService(facturaDAO);
        }

        public AppSettings Settings { get; }

        public IFacturaDAO FacturaDAO { get; }

        public ISeguimientoDAO SeguimientoDAO { get; }

        public NotificacionService Notificacion { get; }

        public RevisionVendedorService RevisionVendedor { get; }

        public PlantillaService Plantilla { get; }

        public PlantillaVendedorService PlantillaVendedor { get; }

        public PlantillaCobranzaService PlantillaCobranza { get; }

        public CorreoService Correo { get; }

        public BitacoraService Bitacora { get; }

        public ReporteConsola Reporte { get; }

        public RespuestaService Respuesta { get; }

        public CobranzaVencidaService CobranzaVencida { get; }

        public void Dispose() => _http.Dispose();
    }
}
