using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using notificacion_clientes.Entity;

namespace notificacion_clientes.Services
{
    /// <summary>
    /// Descarga el XML o el PDF de un CFDI desde el API de facturas.
    /// Endpoint: {UrlBase}/{cfdi}?tipo=xml|pdf
    /// </summary>
    public class FacturaDescargaService
    {
        private readonly HttpClient _cliente;
        private readonly string _urlBase;

        public FacturaDescargaService(HttpClient cliente, string urlBase)
        {
            _cliente = cliente;
            _urlBase = urlBase.TrimEnd('/');
        }

        /// <summary>
        /// Descarga el archivo del CFDI en memoria. Devuelve null si el API responde 404.
        /// </summary>
        public async Task<ArchivoFactura?> Descargar(
            string cfdi,
            TipoArchivo tipo = TipoArchivo.Xml,
            CancellationToken cancelacion = default)
        {
            if (string.IsNullOrWhiteSpace(cfdi))
                throw new ArgumentException("El CFDI es obligatorio", nameof(cfdi));

            var url = $"{_urlBase}/{Uri.EscapeDataString(cfdi)}?tipo={tipo.ToString().ToLowerInvariant()}";

            using var respuesta = await _cliente.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancelacion);

            if (respuesta.StatusCode == HttpStatusCode.NotFound)
                return null;

            respuesta.EnsureSuccessStatusCode();

            var contenido = await respuesta.Content.ReadAsByteArrayAsync(cancelacion);

            return new ArchivoFactura
            {
                Cfdi = cfdi,
                Tipo = tipo,
                NombreArchivo = ObtenerNombreArchivo(respuesta, cfdi, tipo),
                ContentType = respuesta.Content.Headers.ContentType?.MediaType ?? "application/octet-stream",
                Contenido = contenido
            };
        }

        /// <summary>
        /// Descarga el archivo y lo guarda en la carpeta indicada. Devuelve la ruta final, o null si no existe el CFDI.
        /// </summary>
        public async Task<string?> DescargarYGuardar(
            string cfdi,
            string carpetaDestino,
            TipoArchivo tipo = TipoArchivo.Xml,
            CancellationToken cancelacion = default)
        {
            var archivo = await Descargar(cfdi, tipo, cancelacion);
            if (archivo is null)
                return null;

            Directory.CreateDirectory(carpetaDestino);
            var ruta = Path.Combine(carpetaDestino, archivo.NombreArchivo);
            await File.WriteAllBytesAsync(ruta, archivo.Contenido, cancelacion);

            return ruta;
        }

        /// <summary>
        /// Toma el nombre de Content-Disposition; si no viene o es inseguro, arma uno con el CFDI.
        /// </summary>
        private static string ObtenerNombreArchivo(HttpResponseMessage respuesta, string cfdi, TipoArchivo tipo)
        {
            var extension = tipo == TipoArchivo.Pdf ? "pdf" : "xml";
            var propuesto = respuesta.Content.Headers.ContentDisposition?.FileNameStar
                ?? respuesta.Content.Headers.ContentDisposition?.FileName;

            if (string.IsNullOrWhiteSpace(propuesto))
                return $"{cfdi}.{extension}";

            // El nombre lo manda el servidor: nos quedamos solo con el archivo y quitamos caracteres inválidos
            // para que no pueda escribir fuera de la carpeta destino.
            propuesto = Path.GetFileName(propuesto.Trim().Trim('"'));
            propuesto = string.Concat(propuesto.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));

            return string.IsNullOrWhiteSpace(propuesto) ? $"{cfdi}.{extension}" : propuesto;
        }
    }
}
