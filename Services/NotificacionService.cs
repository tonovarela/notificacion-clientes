using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using notificacion_clientes.DAO;
using notificacion_clientes.Entity;

namespace notificacion_clientes.Services
{
    /// <summary>
    /// Orquesta el proceso: consulta las facturas del día, las agrupa por cliente
    /// y descarga los archivos de cada CFDI. No imprime nada: solo devuelve el resultado.
    /// </summary>
    public class NotificacionService
    {
        private readonly IFacturaDAO _facturaDAO;
        private readonly FacturaDescargaService _descargaService;
        private readonly LectorCfdi _lectorCfdi;
        private readonly int _diasAtras;
        private readonly bool _omitirDescargaArchivos;

        public NotificacionService(
            IFacturaDAO facturaDAO,
            FacturaDescargaService descargaService,
            LectorCfdi lectorCfdi,
            int diasAtras = 0,
            bool omitirDescargaArchivos = false)
        {
            _facturaDAO = facturaDAO;
            _descargaService = descargaService;
            _lectorCfdi = lectorCfdi;
            _diasAtras = diasAtras;
            _omitirDescargaArchivos = omitirDescargaArchivos;
        }

        public async Task<IReadOnlyList<NotificacionCliente>> Preparar(CancellationToken cancelacion = default)
        {
            var facturas = await _facturaDAO.Obtener(_diasAtras);

            var porCliente = facturas
                .Where(f => !string.IsNullOrWhiteSpace(f.MovID) && !string.IsNullOrWhiteSpace(f.Cliente))
                .GroupBy(f => f.Cliente!.Trim());

            var notificaciones = new List<NotificacionCliente>();

            foreach (var grupo in porCliente)
            {
                notificaciones.Add(new NotificacionCliente
                {
                    Cliente = grupo.Key,
                    RazonSocial = ObtenerRazonSocial(grupo),
                    Contactos = ObtenerContactos(grupo),
                    Documentos = await ObtenerDocumentos(grupo, cancelacion)
                });
            }

            return notificaciones;
        }

        /// <summary>El left join puede dejar la razón social vacía; en ese caso se usa el número de cliente.</summary>
        private static string ObtenerRazonSocial(IGrouping<string, Factura> grupo) =>
            grupo
                .Select(f => f.RazonSocial?.Trim())
                .FirstOrDefault(razon => !string.IsNullOrWhiteSpace(razon))
                ?? grupo.Key;

        /// <summary>La consulta trae una fila por contacto y factura, así que hay que quitar repetidos por correo.</summary>
        private static List<Contacto> ObtenerContactos(IEnumerable<Factura> filas) =>
            filas
                .Where(f => !string.IsNullOrWhiteSpace(f.Email))
                .GroupBy(f => f.Email!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => new Contacto
                {
                    Nombre = g.First().Nombre,
                    Cargo = g.First().Cargo,
                    Email = g.Key
                })
                .ToList();

        /// <summary>Un CFDI se descarga una sola vez aunque aparezca en varias filas del cliente.</summary>
        private async Task<List<DocumentoFactura>> ObtenerDocumentos(
            IEnumerable<Factura> filas,
            CancellationToken cancelacion)
        {
            var documentos = new List<DocumentoFactura>();

            foreach (var grupo in filas.GroupBy(f => f.MovID))
            {
                var factura = grupo.First();
                var archivos = _omitirDescargaArchivos
                    ? new List<ArchivoFactura>()
                    : await DescargarArchivos(grupo.Key, cancelacion);

                // El desglose de IVA solo existe en el XML; la base únicamente guarda el subtotal.
                var importes = archivos.FirstOrDefault(a => a.Tipo == TipoArchivo.Xml) is { } xml
                    ? _lectorCfdi.Leer(xml)
                    : null;

                documentos.Add(new DocumentoFactura
                {
                    MovID = grupo.Key,
                    SubTotal = importes?.SubTotal ?? factura.Importe,
                    Iva = importes?.Iva ?? 0m,
                    Total = importes?.Total ?? factura.Importe,
                    Moneda = importes?.Moneda ?? "MXN",
                    ImportesDelXml = importes is not null,
                    Periodo = factura.Periodo,
                    Ejercicio = factura.Ejercicio,
                    Archivos = archivos
                });
            }

            return documentos;
        }

        private async Task<List<ArchivoFactura>> DescargarArchivos(string movID, CancellationToken cancelacion)
        {
            var archivos = new List<ArchivoFactura>();

            foreach (var tipo in new[] { TipoArchivo.Xml, TipoArchivo.Pdf })
            {
                var archivo = await _descargaService.Descargar(movID, tipo, cancelacion);
                if (archivo is not null)
                    archivos.Add(archivo);
            }

            return archivos;
        }
    }
}
