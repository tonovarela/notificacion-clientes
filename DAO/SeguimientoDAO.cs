
using System.Data;

using Dapper;
using Microsoft.Data.SqlClient;
using notificacion_clientes.Entity;

namespace notificacion_clientes.DAO
{
    /// <summary>
    /// Lee y escribe CorreosCXC.notif: qué se envió, quién contestó y a quién toca insistirle.
    /// Requiere permiso de escritura sobre esa base (ver deploy/sql/001-seguimiento.sql).
    /// </summary>
    public class SeguimientoDAO
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
        /// Facturas que ya salieron en un envío que no está FALLIDO. Es la red de seguridad contra
        /// una doble corrida o un rango de fechas mal puesto.
        ///
        /// Se pregunta por el lote completo y no factura por factura: una corrida con cien clientes
        /// haría cien viajes a la base para averiguar lo mismo.
        /// </summary>
        public async Task<HashSet<string>> ObtenerFacturasYaNotificadas(IReadOnlyCollection<string> movIds)
        {
            if (movIds.Count == 0)
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            const string sql = $@"
                SELECT DISTINCT ef.MovID
                FROM {TablaEnvioFactura} ef
                    INNER JOIN {TablaEnvio} e ON e.IdEnvio = ef.IdEnvio
                WHERE ef.MovID IN @MovIds
                  AND e.Estado <> 'FALLIDO';";

            using var conexion = new SqlConnection(_sqlConexion);
            var notificadas = await conexion.QueryAsync<string>(sql, new { MovIds = movIds });

            return new HashSet<string>(notificadas, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Envíos cuya respuesta todavía no conocemos y que por tanto hay que buscar en el buzón.
        /// Incluye los RECORDADO: el cliente puede contestar al recordatorio.
        ///
        /// A diferencia de la consulta de recordatorios, aquí NO se excluyen los envíos de modo
        /// prueba. Es deliberado: en modo prueba el correo sí salió, sólo que al buzón de pruebas,
        /// y contestarlo desde ahí es la única forma de comprobar que el cruce funciona antes de
        /// que le llegue nada a un cliente. Marcarlos CONTESTADO no dispara nada, porque el
        /// recordatorio sí los excluye.
        /// </summary>
        public async Task<IReadOnlyList<EnvioNotificacion>> ObtenerParaConciliar(DateTime desde)
        {
            const string sql = $@"
                SELECT IdEnvio, Cliente, RazonSocial, Proceso, MessageId, Token, IdEnvioOriginal, Intento,
                       Asunto, Destinatarios, ModoPrueba, FechaEnvio, Estado, Error
                FROM {TablaEnvio}
                WHERE Estado IN ('ENVIADO','RECORDADO')
                  AND FechaEnvio >= @Desde
                ORDER BY FechaEnvio;";

            using var conexion = new SqlConnection(_sqlConexion);
            var filas = await conexion.QueryAsync(sql, new { Desde = desde });

            return filas.Select(Mapear).ToList();
        }

        /// <summary>
        /// Fecha del envío pendiente más viejo. Es el punto desde el cual vale la pena leer el
        /// buzón: más atrás sólo hay correos ya conciliados.
        /// Null si no hay nada pendiente, en cuyo caso no hace falta ni conectarse al IMAP.
        /// </summary>
        public async Task<DateTime?> ObtenerFechaPendienteMasAntiguo()
        {
            const string sql = $@"
                SELECT MIN(FechaEnvio)
                FROM {TablaEnvio}
                WHERE Estado IN ('ENVIADO','RECORDADO');";

            using var conexion = new SqlConnection(_sqlConexion);
            return await conexion.ExecuteScalarAsync<DateTime?>(sql);
        }

        /// <summary>
        /// Los envíos de cobranza abiertos de la semana, indexados por cliente.
        ///
        /// Es lo que convierte el correo del viernes en un recordatorio de verdad: si el cliente
        /// ya recibió el del martes y no contestó, el del viernes se cuelga de ese hilo en vez de
        /// llegar como un correo suelto. Un cliente que apenas cayó en vencido el miércoles no
        /// aparece aquí, y su correo del viernes sale como primer intento.
        ///
        /// Se excluyen los CONTESTADO —a ésos ni siquiera se les escribe— y los FALLIDO, que
        /// nunca llegaron y por tanto no forman hilo.
        /// </summary>
        public async Task<Dictionary<string, EnvioNotificacion>> ObtenerCobranzaAbiertaDeLaSemana(
            DateTime desde,
            bool modoPrueba)
        {
            const string sql = $@"
                SELECT IdEnvio, Cliente, RazonSocial, Proceso, MessageId, Token, IdEnvioOriginal, Intento,
                       Asunto, Destinatarios, ModoPrueba, FechaEnvio, Estado, Error
                FROM {TablaEnvio}
                WHERE Proceso    = 'COBRANZA'
                  AND Estado     = 'ENVIADO'
                  AND Intento    = 1
                  AND ModoPrueba = @ModoPrueba
                  AND FechaEnvio >= @Desde
                ORDER BY FechaEnvio;";

            using var conexion = new SqlConnection(_sqlConexion);
            var filas = await conexion.QueryAsync(sql, new { Desde = desde, ModoPrueba = modoPrueba });

            var porCliente = new Dictionary<string, EnvioNotificacion>(StringComparer.OrdinalIgnoreCase);

            // Si hubiera más de uno en la semana, gana el más reciente: es el hilo vivo.
            foreach (var fila in filas)
            {
                var envio = Mapear(fila);
                porCliente[envio.Cliente] = envio;
            }

            return porCliente;
        }

        /// <summary>
        /// Envíos de cobranza que ya agotaron su vigencia sin respuesta.
        ///
        /// Cerrarlos no es cosmético. La conciliación busca en el buzón desde el pendiente más
        /// viejo, así que un envío que nunca se cierra ancla esa ventana para siempre y la
        /// búsqueda IMAP crece sin límite. Un correo de cobranza deja de esperar respuesta cuando
        /// ya salió el siguiente.
        /// </summary>
        public async Task<IReadOnlyList<EnvioNotificacion>> ObtenerPendientesDeCierre(DateTime fechaCorte)
        {
            const string sql = $@"
                SELECT IdEnvio, Cliente, RazonSocial, Proceso, MessageId, Token, IdEnvioOriginal, Intento,
                       Asunto, Destinatarios, ModoPrueba, FechaEnvio, Estado, Error
                FROM {TablaEnvio}
                WHERE Proceso    = 'COBRANZA'
                  AND Estado     IN ('ENVIADO','RECORDADO')
                  AND FechaEnvio < @FechaCorte
                ORDER BY FechaEnvio;";

            using var conexion = new SqlConnection(_sqlConexion);
            var filas = await conexion.QueryAsync(sql, new { FechaCorte = fechaCorte });

            return filas.Select(Mapear).ToList();
        }

        /// <summary>
        /// Clientes que ya contestaron un correo de cobranza mandado desde <paramref name="desde"/>.
        ///
        /// Es la regla del viernes: a quien contestó el correo del martes no se le vuelve a
        /// insistir en la misma semana. Se pregunta por cliente y no por envío porque lo que
        /// importa es si la persona respondió, no a cuál de los correos.
        ///
        /// Se compara contra envíos del MISMO modo que la corrida actual, no siempre contra los
        /// reales. Eso preserva lo importante —una respuesta desde el buzón de pruebas jamás
        /// excluye a un cliente de un correo de verdad— y además deja la regla comprobable: en
        /// una corrida de prueba se contrasta contra los envíos de prueba. Con el filtro fijo en
        /// cero, un ensayo completo nunca excluía a nadie y la regla no se podía verificar.
        /// </summary>
        public async Task<HashSet<string>> ObtenerClientesQueContestaronCobranza(DateTime desde, bool modoPrueba)
        {
            const string sql = $@"
                SELECT DISTINCT Cliente
                FROM {TablaEnvio}
                WHERE Proceso    = 'COBRANZA'
                  AND Estado     = 'CONTESTADO'
                  AND ModoPrueba = @ModoPrueba
                  AND FechaEnvio >= @Desde;";

            using var conexion = new SqlConnection(_sqlConexion);
            var clientes = await conexion.QueryAsync<string>(sql, new { Desde = desde, ModoPrueba = modoPrueba });

            return new HashSet<string>(clientes, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Cierra un envío como contestado. Guardar el RespuestaMessageId es lo que hace que volver
        /// a correr la conciliación no reprocese lo ya visto.
        /// Marca también el envío original si esto era un recordatorio: el hilo es uno solo.
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
                   OR IdEnvio = @IdEnvioOriginal;";

            using var conexion = new SqlConnection(_sqlConexion);
            await conexion.ExecuteAsync(sql, new
            {
                respuesta.Envio.IdEnvio,
                respuesta.Envio.IdEnvioOriginal,
                respuesta.Fecha,
                respuesta.DeEmail,
                respuesta.MessageId,
                Asunto = Recortar(respuesta.Asunto, 500)
            });
        }

        /// <summary>
        /// Un rebote no es una respuesta: la dirección no existe o no acepta el correo. Se marca
        /// FALLIDO para que cobranza la corrija en el CRM, y sale del ciclo de recordatorios.
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

        /// <summary>Las facturas de varios envíos en un solo viaje, agrupadas por IdEnvio.</summary>
        private static async Task<Dictionary<int, List<FacturaEnviada>>> ObtenerFacturas(
            SqlConnection conexion,
            IReadOnlyCollection<int> idsEnvio)
        {
            const string sql = $@"
                SELECT IdEnvio, MovID, Total, Moneda
                FROM {TablaEnvioFactura}
                WHERE IdEnvio IN @IdsEnvio;";

            var filas = await conexion.QueryAsync(sql, new { IdsEnvio = idsEnvio });

            return filas
                .GroupBy(f => (int)f.IdEnvio)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(f => new FacturaEnviada
                    {
                        MovID = (string)f.MovID,
                        Total = (decimal)f.Total,
                        Moneda = (string)f.Moneda
                    }).ToList());
        }

        private static EnvioNotificacion Mapear(dynamic fila) =>
            Mapear(fila, Array.Empty<FacturaEnviada>());

        private static EnvioNotificacion Mapear(dynamic fila, IReadOnlyList<FacturaEnviada> facturas) =>
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
                Facturas = facturas
            };

        /// <summary>Las columnas tienen tope y un asunto de correo puede traer cualquier cosa.</summary>
        private static string Recortar(string valor, int maximo) =>
            valor.Length <= maximo ? valor : valor[..maximo];
    }
}
