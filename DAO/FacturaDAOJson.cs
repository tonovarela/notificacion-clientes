using System.Text.Json;
using System.Text.Json.Serialization;
using notificacion_clientes.Entity;

namespace notificacion_clientes.DAO
{
    /// <summary>
    /// Fuente de datos alterna a <see cref="FacturaDAO"/>: lee de archivos JSON en vez de SQL
    /// Server. Pensada para desarrollar/probar por VPN, donde el servidor de base de datos es
    /// lento o no está disponible.
    ///
    /// Espera tres archivos dentro de <paramref name="rutaCarpeta"/>, uno por vista del ERP:
    /// facturas.json, cobranza-vencida.json y revision-vendedores.json. Cada uno es un arreglo
    /// JSON con las mismas columnas que devuelve la consulta SQL correspondiente.
    ///
    /// cobranza-vencida.json es la vista de antigüedad completa —TODO lo vencido—, no la
    /// población de un día. Las dos poblaciones de cobranza salen de cruzarla contra envios.json,
    /// que es lo que aquí hace las veces de CorreosCXC.notif. Ese cruce vive aquí y no en un
    /// archivo aparte a propósito: si la población del recordatorio se declarara a mano, se
    /// desincronizaría del registro en cuanto alguien mandara un correo, y el archivo mentiría
    /// justo sobre lo que se quería probar.
    /// </summary>
    public class FacturaDAOJson : IFacturaDAO
    {
        private static readonly JsonSerializerOptions OpcionesJson = new()
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly string _rutaCarpeta;

        public FacturaDAOJson(string rutaCarpeta)
        {
            _rutaCarpeta = rutaCarpeta;
        }

        public async Task<IEnumerable<Factura>> Obtener(int diasAtras = 0) =>
            await Leer<Factura>("facturas.json");

        public async Task<IEnumerable<FacturaRevisionVendedor>> ObtenerFacturasRevisionVendedores() =>
            await Leer<FacturaRevisionVendedor>("revision-vendedores.json");

        /// <summary>
        /// Primer aviso: lo vencido que no aparece en ningún envío registrado.
        ///
        /// Reproduce el CTE FacturasNotificaficadas, que no filtra por estado: basta con que la
        /// factura haya viajado en un correo —aunque aquél fallara— para que deje de ser primer
        /// aviso. Es lo que hace que volver a correr el martes no re-notifique a nadie.
        /// </summary>
        public async Task<IEnumerable<FacturaCobranzaVencida>> ObtenerFacturasCobranzaVencida()
        {
            var vencidas = await Leer<FacturaCobranzaVencida>("cobranza-vencida.json");
            var notificadas = await MovIdsNotificados(soloSinContestar: false);

            return vencidas.Where(f => !notificadas.Contains(f.MovID)).ToList();
        }

        /// <summary>
        /// Recordatorio: lo vencido que YA se notificó y cuyo envío no está contestado.
        ///
        /// Reproduce el CTE EnviosNoContestados, con su mismo criterio —"cualquier estado menos
        /// CONTESTADO"—, así que un envío FALLIDO también cuenta como pendiente de respuesta.
        /// </summary>
        public async Task<IEnumerable<FacturaCobranzaVencida>> ObtenerFacturasCobranzaVencidaSinContestar()
        {
            var vencidas = await Leer<FacturaCobranzaVencida>("cobranza-vencida.json");
            var sinContestar = await MovIdsNotificados(soloSinContestar: true);

            return vencidas.Where(f => sinContestar.Contains(f.MovID)).ToList();
        }

        /// <summary>
        /// Los MovID que aparecen en envios.json, que es el equivalente de unir notif.EnvioFactura
        /// con notif.Envio. Sin archivo se devuelve el conjunto vacío: es una instalación donde
        /// todavía no se ha mandado nada, y entonces todo lo vencido es primer aviso.
        /// </summary>
        private async Task<HashSet<string>> MovIdsNotificados(bool soloSinContestar)
        {
            var ruta = Path.Combine(_rutaCarpeta, "envios.json");

            if (!File.Exists(ruta))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await using var flujo = File.OpenRead(ruta);
            var envios = await JsonSerializer.DeserializeAsync<List<EnvioNotificacion>>(flujo, OpcionesJson)
                         ?? new List<EnvioNotificacion>();

            return envios
                .Where(e => !soloSinContestar || e.Estado != EstadoEnvio.Contestado)
                .SelectMany(e => e.Facturas)
                .Select(f => f.MovID)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private async Task<List<T>> Leer<T>(string archivo)
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
