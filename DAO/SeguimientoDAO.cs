using Dapper;
using Microsoft.Data.SqlClient;
using notificacion_clientes.Entity;

namespace notificacion_clientes.DAO
{
    /// <summary>
    /// Escribe en CorreosCXC.notif: deja constancia de cada correo que sale y de en qué acabó.
    /// Requiere permiso de escritura sobre esa base (ver deploy/sql/001-seguimiento.sql).
    ///
    /// Casi no lee. Saber a quién le toca el recordatorio de cobranza dejó de resolverse aquí y
    /// pasó a la consulta de facturas, que cruza la antigüedad de saldos contra estas mismas
    /// tablas dentro del SELECT (ver <see cref="FacturaDAO.ObtenerFacturasCobranzaVencidaSinContestar"/>).
    /// La única consulta que queda es la de envíos sin respuesta, que alimenta la lectura del buzón.
    /// </summary>
    public class SeguimientoDAO : ISeguimientoDAO
    {
        /// <summary>
        /// Las tablas viven en su propia base, CorreosCXC, y no dentro de Lito: son datos de esta
        /// aplicación, no del ERP. La conexión sigue apuntando a Lito —de ahí salen las facturas—
        /// y se llega aquí por nombre de tres partes, que es la misma convención que ya usa
        /// FacturaDAO para LITOCRM y etl_mstr.
        ///
        /// El nombre está en una constante y no repetido en cada consulta: mover la base a otro
        /// nombre es cambiar esta línea.
        /// </summary>
        private const string EsquemaSeguimiento = "CorreosCXC.notif";

        private const string TablaEnvio = EsquemaSeguimiento + ".Envio";

        private const string TablaEnvioFactura = EsquemaSeguimiento + ".EnvioFactura";

        private const string TablaEnvioRecordatorio = EsquemaSeguimiento + ".EnvioRecordatorio";

        /// <summary>
        /// El estado viaja como VARCHAR para que la tabla se pueda leer desde el ERP sin decodificar
        /// nada. Dapper no convierte string a enum por nombre, así que la traducción es explícita
        /// y vive sólo aquí.
        /// </summary>
        private static readonly Dictionary<EstadoEnvio, string> NombreEstado = new()
        {
            [EstadoEnvio.Enviado] = "ENVIADO",
            [EstadoEnvio.Fallido] = "FALLIDO",
            [EstadoEnvio.Contestado] = "CONTESTADO",
            [EstadoEnvio.Recordado] = "RECORDADO",
            [EstadoEnvio.SinRespuesta] = "SIN_RESPUESTA"
        };

        private static readonly Dictionary<string, EstadoEnvio> EstadoPorNombre =
            NombreEstado.ToDictionary(par => par.Value, par => par.Key, StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<ProcesoEnvio, string> NombreProceso = new()
        {
            [ProcesoEnvio.Clientes] = "CLIENTES",
            [ProcesoEnvio.Cobranza] = "COBRANZA"
        };

        private static readonly Dictionary<string, ProcesoEnvio> ProcesoPorNombre =
            NombreProceso.ToDictionary(par => par.Value, par => par.Key, StringComparer.OrdinalIgnoreCase);

        private readonly string _sqlConexion;

        public SeguimientoDAO(string sqlConexion)
        {
            _sqlConexion = sqlConexion;
        }

        /// <summary>
        /// Guarda un envío y las facturas que llevaba, en una sola transacción: un renglón de
        /// Envio sin sus facturas no sirve para reenviar nada, y uno de EnvioFactura sin su
        /// Envio no existe.
        ///
        /// Estas dos escrituras son ahora lo único que alimenta la consulta del recordatorio: si
        /// un envío no queda registrado, sus facturas se ven como nunca notificadas y volverán a
        /// salir como primer aviso en la siguiente corrida.
        ///
        /// La conexión apunta a Lito y las dos escrituras van a CorreosCXC. Mientras las dos bases
        /// vivan en la misma instancia eso es una transacción local normal; si algún día CorreosCXC
        /// se mudara a otro servidor, esto pasaría a necesitar transacciones distribuidas (MSDTC)
        /// y dejaría de funcionar sin avisar de otra forma que con un error al escribir.
        ///
        /// Devuelve el IdEnvio asignado.
        /// </summary>
        public async Task<int> Registrar(EnvioNotificacion envio)
        {
            const string sqlEnvio = $@"
                INSERT INTO {TablaEnvio}
                    (Cliente, RazonSocial, Proceso, MessageId, Token, IdEnvioOriginal, Intento, Asunto,
                     Destinatarios, ModoPrueba, FechaEnvio, Estado, Error)
                OUTPUT INSERTED.IdEnvio
                VALUES
                    (@Cliente, @RazonSocial, @Proceso, @MessageId, @Token, @IdEnvioOriginal, @Intento, @Asunto,
                     @Destinatarios, @ModoPrueba, @FechaEnvio, @Estado, @Error);";

            const string sqlFactura = $@"
                INSERT INTO {TablaEnvioFactura} (IdEnvio, MovID, Total, Moneda)
                VALUES (@IdEnvio, @MovID, @Total, @Moneda);";

            using var conexion = new SqlConnection(_sqlConexion);
            await conexion.OpenAsync();
            using var transaccion = conexion.BeginTransaction();

            var idEnvio = await conexion.ExecuteScalarAsync<int>(sqlEnvio, new
            {
                envio.Cliente,
                envio.RazonSocial,
                Proceso = NombreProceso[envio.Proceso],
                envio.MessageId,
                envio.Token,
                envio.IdEnvioOriginal,
                envio.Intento,
                envio.Asunto,
                envio.Destinatarios,
                envio.ModoPrueba,
                envio.FechaEnvio,
                Estado = NombreEstado[envio.Estado],
                envio.Error
            }, transaccion);

            if (envio.Facturas.Count > 0)
            {
                await conexion.ExecuteAsync(sqlFactura, envio.Facturas.Select(f => new
                {
                    IdEnvio = idEnvio,
                    f.MovID,
                    f.Total,
                    f.Moneda
                }), transaccion);
            }

            transaccion.Commit();

            return idEnvio;
        }

        /// <summary>
        /// Envíos cuya respuesta todavía no conocemos y que por tanto hay que buscar en el buzón.
        /// Incluye los RECORDADO: el cliente puede contestar al recordatorio.
        ///
        /// NO se excluyen los envíos de modo prueba. Es deliberado: en modo prueba el correo sí
        /// salió, sólo que al buzón de pruebas, y contestarlo desde ahí es la única forma de
        /// comprobar que el cruce funciona antes de que le llegue nada a un cliente.
        ///
        /// <paramref name="desde"/> es el tope duro de la ventana. Sin él la búsqueda crecería sin
        /// límite: nada cierra por vigencia los envíos que nadie contesta, así que un renglón
        /// atorado en ENVIADO ancla la ventana para siempre.
        /// </summary>
        public async Task<IReadOnlyList<EnvioNotificacion>> ObtenerEnviosSinRespuesta(DateTime desde)
        {
            const string sql = $@"
                SELECT IdEnvio, Cliente, RazonSocial, Proceso, MessageId, Token, IdEnvioOriginal, Intento,
                       Asunto, Destinatarios, ModoPrueba, FechaEnvio, Estado, Error
                FROM {TablaEnvio}
                WHERE Estado IN ('ENVIADO','RECORDADO')
                  AND FechaEnvio >= @Desde
                ORDER BY FechaEnvio;";

            using var conexion = new SqlConnection(_sqlConexion);
            var filas = (await conexion.QueryAsync(sql, new { Desde = desde })).ToList();

            if (filas.Count == 0)
                return Array.Empty<EnvioNotificacion>();

            var recordatorios = await ObtenerRecordatorios(conexion, filas.Select(f => (int)f.IdEnvio).ToList());

            return filas
                .Select(f => (EnvioNotificacion)Mapear(f, recordatorios.GetValueOrDefault((int)f.IdEnvio) ?? new List<string>()))
                .ToList();
        }

        /// <summary>
        /// Anota el recordatorio contra los envíos abiertos que llevaban esas facturas. Se acota
        /// por MovID y no por cliente: si una factura del cliente ya se pagó, su envío no entró en
        /// el recordatorio y no debe cerrarse con la respuesta a éste.
        ///
        /// Se agrega un renglón por recordatorio en vez de pisar el anterior: así el cliente que
        /// contesta un correo de hace tres semanas también se detecta. El NOT EXISTS hace que
        /// repetir la corrida del mismo día no truene contra la llave primaria.
        /// </summary>
        public async Task<int> MarcarRecordatorioEnviado(
            IReadOnlyList<string> movIds,
            string messageId,
            DateTime fechaEnvio)
        {
            if (movIds.Count == 0)
                return 0;

            const string sql = $@"
                INSERT INTO {TablaEnvioRecordatorio} (IdEnvio, MessageId, FechaEnvio)
                SELECT DISTINCT ef.IdEnvio, @MessageId, @FechaEnvio
                FROM {TablaEnvioFactura} ef
                    JOIN {TablaEnvio} e ON e.IdEnvio = ef.IdEnvio
                WHERE ef.MovID IN @MovIds
                  AND e.Estado <> 'CONTESTADO'
                  AND NOT EXISTS (SELECT 1 FROM {TablaEnvioRecordatorio} r
                                  WHERE r.IdEnvio = ef.IdEnvio AND r.MessageId = @MessageId);";

            using var conexion = new SqlConnection(_sqlConexion);
            return await conexion.ExecuteAsync(sql, new { MovIds = movIds, MessageId = messageId, FechaEnvio = fechaEnvio });
        }

        /// <summary>
        /// Cierra un envío como contestado. Es lo que saca a sus facturas del recordatorio: la
        /// consulta de cobranza sin contestar descarta los envíos en CONTESTADO, así que mientras
        /// nadie llame a este método el cliente seguirá recibiendo el mismo aviso.
        ///
        /// Si la respuesta fue a un recordatorio, cierra de una vez TODOS los envíos que aquel
        /// correo cubría. Es lo que pide el sentido común: el cliente contestó un mensaje que le
        /// reclamaba varias facturas, no una; cerrar sólo una lo dejaría recibiendo el mismo
        /// recordatorio por el resto.
        /// </summary>
        public async Task MarcarContestado(RespuestaDetectada respuesta)
        {
            const string sql = $@"
                UPDATE {TablaEnvio}
                SET Estado             = 'CONTESTADO',
                    FechaRespuesta     = @Fecha,
                    RespondioEmail     = @DeEmail,
                    RespuestaMessageId = @MessageId,
                    RespuestaAsunto    = @Asunto
                WHERE IdEnvio = @IdEnvio
                   OR IdEnvio = @IdEnvioOriginal
                   OR (@RecordatorioMessageId IS NOT NULL
                       AND Estado <> 'CONTESTADO'
                       AND IdEnvio IN (SELECT IdEnvio FROM {TablaEnvioRecordatorio}
                                       WHERE MessageId = @RecordatorioMessageId));";

            using var conexion = new SqlConnection(_sqlConexion);
            await conexion.ExecuteAsync(sql, new
            {
                respuesta.Envio.IdEnvio,
                respuesta.Envio.IdEnvioOriginal,
                RecordatorioMessageId = respuesta.RespondioARecordatorio,
                respuesta.Fecha,
                respuesta.DeEmail,
                respuesta.MessageId,
                Asunto = Recortar(respuesta.Asunto, 500)
            });
        }

        /// <summary>
        /// Un rebote no es una respuesta: la dirección no existe o no acepta el correo. Se marca
        /// FALLIDO para que cobranza la corrija en el CRM.
        /// </summary>
        public async Task MarcarFallidoPorRebote(RespuestaDetectada rebote)
        {
            const string sql = $@"
                UPDATE {TablaEnvio}
                SET Estado             = 'FALLIDO',
                    Error              = @Error,
                    FechaRespuesta     = @Fecha,
                    RespuestaMessageId = @MessageId
                WHERE IdEnvio = @IdEnvio;";

            using var conexion = new SqlConnection(_sqlConexion);
            await conexion.ExecuteAsync(sql, new
            {
                rebote.Envio.IdEnvio,
                Error = Recortar($"Rebote de {rebote.DeEmail}: {rebote.Asunto}", 1000),
                rebote.Fecha,
                rebote.MessageId
            });
        }

        /// <summary>El original pasa a RECORDADO; el recordatorio se registra aparte con Registrar.</summary>
        public async Task MarcarRecordado(int idEnvio)
        {
            const string sql = $"UPDATE {TablaEnvio} SET Estado = 'RECORDADO' WHERE IdEnvio = @IdEnvio;";

            using var conexion = new SqlConnection(_sqlConexion);
            await conexion.ExecuteAsync(sql, new { IdEnvio = idEnvio });
        }

        /// <summary>
        /// Cierra el ciclo. Marca el envío y su recordatorio de una vez: los dos renglones
        /// describen el mismo intento fallido de contactar al cliente.
        /// </summary>
        public async Task MarcarSinRespuesta(int idEnvio)
        {
            const string sql = $@"
                UPDATE {TablaEnvio}
                SET Estado = 'SIN_RESPUESTA'
                WHERE IdEnvio = @IdEnvio
                   OR IdEnvioOriginal = @IdEnvio;";

            using var conexion = new SqlConnection(_sqlConexion);
            await conexion.ExecuteAsync(sql, new { IdEnvio = idEnvio });
        }

        /// <summary>Los recordatorios de varios envíos en un solo viaje, agrupados por IdEnvio.</summary>
        private static async Task<Dictionary<int, List<string>>> ObtenerRecordatorios(
            SqlConnection conexion,
            IReadOnlyCollection<int> idsEnvio)
        {
            const string sql = $@"
                SELECT IdEnvio, MessageId
                FROM {TablaEnvioRecordatorio}
                WHERE IdEnvio IN @IdsEnvio
                ORDER BY FechaEnvio;";

            var filas = await conexion.QueryAsync(sql, new { IdsEnvio = idsEnvio });

            return filas
                .GroupBy(f => (int)f.IdEnvio)
                .ToDictionary(g => g.Key, g => g.Select(f => (string)f.MessageId).ToList());
        }

        private static EnvioNotificacion Mapear(dynamic fila, IReadOnlyList<string> recordatorios) =>
            new()
            {
                IdEnvio = (int)fila.IdEnvio,
                Cliente = (string)fila.Cliente,
                RazonSocial = (string?)fila.RazonSocial,
                Proceso = ProcesoPorNombre[(string)fila.Proceso],
                MessageId = (string)fila.MessageId,
                Token = (Guid)fila.Token,
                IdEnvioOriginal = (int?)fila.IdEnvioOriginal,
                Intento = (byte)fila.Intento,
                Asunto = (string)fila.Asunto,
                Destinatarios = (string)fila.Destinatarios,
                ModoPrueba = (bool)fila.ModoPrueba,
                FechaEnvio = (DateTime)fila.FechaEnvio,
                Estado = EstadoPorNombre[(string)fila.Estado],
                Error = (string?)fila.Error,
                RecordatorioMessageIds = recordatorios
            };

        /// <summary>Las columnas tienen tope y un asunto de correo puede traer cualquier cosa.</summary>
        private static string Recortar(string valor, int maximo) =>
            valor.Length <= maximo ? valor : valor[..maximo];
    }
}
