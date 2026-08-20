using System.Text.Json;
using System.Text.Json.Serialization;
using notificacion_clientes.Entity;

namespace notificacion_clientes.DAO
{
    /// <summary>
    /// Fuente de seguimiento alterna a <see cref="SeguimientoDAO"/>: guarda los envíos en un
    /// archivo JSON en vez de CorreosCXC.notif. Pensada para desarrollar/probar por VPN, donde
    /// el servidor de base de datos es lento o no está disponible.
    ///
    /// Igual que la de SQL, casi sólo escribe: a quién le toca el recordatorio de cobranza lo
    /// decide la consulta de facturas, no este archivo. Lo único que se lee de aquí son los envíos
    /// que siguen esperando respuesta, para cruzarlos contra el buzón.
    ///
    /// Lee y reescribe <paramref name="rutaArchivo"/> completo en cada operación: para el volumen
    /// de datos de prueba (decenas de envíos) es más simple que llevar un índice, y deja el
    /// archivo abierto para inspeccionarlo a mano entre corridas.
    /// </summary>
    public class SeguimientoDAOJson : ISeguimientoDAO
    {
        private static readonly JsonSerializerOptions OpcionesJson = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly string _rutaArchivo;

        public SeguimientoDAOJson(string rutaArchivo)
        {
            _rutaArchivo = rutaArchivo;
        }

        public async Task<int> Registrar(EnvioNotificacion envio)
        {
            var envios = await Leer();

            var idEnvio = envios.Count == 0 ? 1 : envios.Max(e => e.IdEnvio) + 1;

            envios.Add(new EnvioNotificacion
            {
                IdEnvio = idEnvio,
                Cliente = envio.Cliente,
                RazonSocial = envio.RazonSocial,
                Proceso = envio.Proceso,
                MessageId = envio.MessageId,
                Token = envio.Token,
                IdEnvioOriginal = envio.IdEnvioOriginal,
                Intento = envio.Intento,
                Asunto = envio.Asunto,
                Destinatarios = envio.Destinatarios,
                ModoPrueba = envio.ModoPrueba,
                FechaEnvio = envio.FechaEnvio,
                Estado = envio.Estado,
                Error = envio.Error,
                FechaRespuesta = envio.FechaRespuesta,
                RespondioEmail = envio.RespondioEmail,
                RespuestaMessageId = envio.RespuestaMessageId,
                RespuestaAsunto = envio.RespuestaAsunto,
                RecordatorioMessageIds = envio.RecordatorioMessageIds,
                Facturas = envio.Facturas
            });

            await Guardar(envios);

            return idEnvio;
        }

        public async Task<IReadOnlyList<EnvioNotificacion>> ObtenerEnviosSinRespuesta(DateTime desde)
        {
            var envios = await Leer();

            return envios
                .Where(e => (e.Estado == EstadoEnvio.Enviado || e.Estado == EstadoEnvio.Recordado)
                            && e.FechaEnvio >= desde)
                .OrderBy(e => e.FechaEnvio)
                .ToList();
        }

        /// <summary>
        /// Estampa el Message-Id del recordatorio sobre los envíos abiertos que llevaban esas
        /// facturas. Se acota por MovID y no por cliente: si una factura ya se pagó, su envío no
        /// entró en el recordatorio y no debe cerrarse con la respuesta a éste.
        /// </summary>
        public async Task<int> MarcarRecordatorioEnviado(
            IReadOnlyList<string> movIds,
            string messageId,
            DateTime fechaEnvio)
        {
            if (movIds.Count == 0)
                return 0;

            var buscados = movIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var envios = await Leer();
            var marcados = 0;

            envios = envios
                .Select(e =>
                {
                    if (e.Estado == EstadoEnvio.Contestado || !e.Facturas.Any(f => buscados.Contains(f.MovID)))
                        return e;

                    // Se acumula, no se pisa: el cliente puede contestar un recordatorio viejo.
                    // Repetir la corrida del mismo día no debe duplicar el mismo id.
                    if (e.RecordatorioMessageIds.Contains(messageId, StringComparer.OrdinalIgnoreCase))
                        return e;

                    marcados++;
                    return Clonar(e, recordatorioMessageIds: e.RecordatorioMessageIds.Append(messageId).ToList());
                })
                .ToList();

            await Guardar(envios);

            return marcados;
        }

        public async Task MarcarContestado(RespuestaDetectada respuesta)
        {
            var envios = await Leer();

            // Si contestó un recordatorio, se cierran todos los envíos que aquel correo cubría:
            // el cliente respondió a un mensaje que le reclamaba varias facturas, no una.
            bool Alcanza(EnvioNotificacion e) =>
                e.IdEnvio == respuesta.Envio.IdEnvio
                || e.IdEnvio == respuesta.Envio.IdEnvioOriginal
                || (respuesta.RespondioARecordatorio is { } id
                    && e.Estado != EstadoEnvio.Contestado
                    && e.RecordatorioMessageIds.Contains(id, StringComparer.OrdinalIgnoreCase));

            envios = envios
                .Select(e => Alcanza(e)
                    ? Clonar(e, estado: EstadoEnvio.Contestado, fechaRespuesta: respuesta.Fecha,
                        respondioEmail: respuesta.DeEmail, respuestaMessageId: respuesta.MessageId,
                        respuestaAsunto: Recortar(respuesta.Asunto, 500))
                    : e)
                .ToList();

            await Guardar(envios);
        }

        public async Task MarcarFallidoPorRebote(RespuestaDetectada rebote)
        {
            var envios = await Leer();

            envios = envios
                .Select(e => e.IdEnvio == rebote.Envio.IdEnvio
                    ? Clonar(e, estado: EstadoEnvio.Fallido,
                        error: Recortar($"Rebote de {rebote.DeEmail}: {rebote.Asunto}", 1000),
                        fechaRespuesta: rebote.Fecha, respuestaMessageId: rebote.MessageId)
                    : e)
                .ToList();

            await Guardar(envios);
        }

        public async Task MarcarRecordado(int idEnvio)
        {
            var envios = await Leer();

            envios = envios
                .Select(e => e.IdEnvio == idEnvio ? Clonar(e, estado: EstadoEnvio.Recordado) : e)
                .ToList();

            await Guardar(envios);
        }

        public async Task MarcarSinRespuesta(int idEnvio)
        {
            var envios = await Leer();

            envios = envios
                .Select(e => e.IdEnvio == idEnvio || e.IdEnvioOriginal == idEnvio
                    ? Clonar(e, estado: EstadoEnvio.SinRespuesta)
                    : e)
                .ToList();

            await Guardar(envios);
        }

        private static EnvioNotificacion Clonar(
            EnvioNotificacion origen,
            EstadoEnvio? estado = null,
            string? error = null,
            DateTime? fechaRespuesta = null,
            string? respondioEmail = null,
            string? respuestaMessageId = null,
            string? respuestaAsunto = null,
            IReadOnlyList<string>? recordatorioMessageIds = null) =>
            new()
            {
                IdEnvio = origen.IdEnvio,
                Cliente = origen.Cliente,
                RazonSocial = origen.RazonSocial,
                Proceso = origen.Proceso,
                MessageId = origen.MessageId,
                Token = origen.Token,
                IdEnvioOriginal = origen.IdEnvioOriginal,
                Intento = origen.Intento,
                Asunto = origen.Asunto,
                Destinatarios = origen.Destinatarios,
                ModoPrueba = origen.ModoPrueba,
                FechaEnvio = origen.FechaEnvio,
                Estado = estado ?? origen.Estado,
                Error = error ?? origen.Error,
                FechaRespuesta = fechaRespuesta ?? origen.FechaRespuesta,
                RespondioEmail = respondioEmail ?? origen.RespondioEmail,
                RespuestaMessageId = respuestaMessageId ?? origen.RespuestaMessageId,
                RespuestaAsunto = respuestaAsunto ?? origen.RespuestaAsunto,
                RecordatorioMessageIds = recordatorioMessageIds ?? origen.RecordatorioMessageIds,
                Facturas = origen.Facturas
            };

        private static string Recortar(string valor, int maximo) =>
            valor.Length <= maximo ? valor : valor[..maximo];

        private async Task<List<EnvioNotificacion>> Leer()
        {
            if (!File.Exists(_rutaArchivo))
                return new List<EnvioNotificacion>();

            await using var flujo = File.OpenRead(_rutaArchivo);
            var envios = await JsonSerializer.DeserializeAsync<List<EnvioNotificacion>>(flujo, OpcionesJson);
            return envios ?? new List<EnvioNotificacion>();
        }

        private async Task Guardar(List<EnvioNotificacion> envios)
        {
            var directorio = Path.GetDirectoryName(_rutaArchivo);
            if (!string.IsNullOrEmpty(directorio))
                Directory.CreateDirectory(directorio);

            await using var flujo = File.Create(_rutaArchivo);
            await JsonSerializer.SerializeAsync(flujo, envios, OpcionesJson);
        }
    }
}
