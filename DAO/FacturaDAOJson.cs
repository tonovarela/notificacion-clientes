using System.Text.Json;
using notificacion_clientes.Entity;

namespace notificacion_clientes.DAO
{
    /// <summary>
    /// Fuente de datos alterna a <see cref="FacturaDAO"/>: lee de archivos JSON en vez de SQL
    /// Server. Pensada para desarrollar/probar por VPN, donde el servidor de base de datos es
    /// lento o no está disponible.
    ///
    /// Espera tres archivos dentro de <paramref name="rutaCarpeta"/>, uno por tipo de envío:
    /// facturas.json, cobranza-vencida.json, revision-vendedores.json. Cada uno es un arreglo
    /// JSON con las mismas columnas que devuelve la consulta SQL correspondiente.
    /// </summary>
    public class FacturaDAOJson : IFacturaDAO
    {
        private static readonly JsonSerializerOptions OpcionesJson = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly string _rutaCarpeta;

        public FacturaDAOJson(string rutaCarpeta)
        {
            _rutaCarpeta = rutaCarpeta;
        }

        public Task<IEnumerable<Factura>> Obtener(int diasAtras = 0) =>
            Leer<Factura>("facturas.json");

        public Task<IEnumerable<FacturaRevisionVendedor>> ObtenerFacturasRevisionVendedores() =>
            Leer<FacturaRevisionVendedor>("revision-vendedores.json");

        public Task<IEnumerable<FacturaCobranzaVencida>> ObtenerFacturasCobranzaVencida() =>
            Leer<FacturaCobranzaVencida>("cobranza-vencida.json");

        private async Task<IEnumerable<T>> Leer<T>(string archivo)
        {
            var ruta = Path.Combine(_rutaCarpeta, archivo);

            if (!File.Exists(ruta))
                throw new FileNotFoundException(
                    $"No se encontró el archivo de datos de prueba '{archivo}' en '{_rutaCarpeta}'.", ruta);

            await using var flujo = File.OpenRead(ruta);
            var datos = await JsonSerializer.DeserializeAsync<List<T>>(flujo, OpcionesJson);
            return datos ?? new List<T>();
        }
    }
}
