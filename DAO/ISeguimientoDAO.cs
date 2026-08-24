using notificacion_clientes.Entity;

namespace notificacion_clientes.DAO
{
    /// <summary>
    /// Registro de envíos: qué se mandó y en qué acabó.
    /// Implementada por <see cref="SeguimientoDAO"/> (SQL Server) y por
    /// <see cref="SeguimientoDAOJson"/> (archivo en Datos/, para probar sin depender de la VPN/DB).
    ///
    /// Aquí ya no se decide a quién le toca el recordatorio de cobranza. Esa pregunta la contesta
    /// la consulta <see cref="IFacturaDAO.ObtenerFacturasCobranzaVencidaSinContestar"/>, que cruza
    /// la antigüedad de saldos contra lo ya notificado dentro del mismo SELECT.
    ///
    /// Queda una sola lectura, <see cref="ObtenerEnviosSinRespuesta"/>: para casar un correo del
    /// buzón con el envío que lo provocó hay que tener a la mano los Message-Id que mandamos, y
    /// eso no se puede resolver desde la consulta de facturas.
    /// </summary>
    public interface ISeguimientoDAO
    {
        Task<int> Registrar(EnvioNotificacion envio);

        /// <summary>
        /// Envíos que todavía esperan respuesta, para cruzarlos contra el buzón.
        /// <paramref name="desde"/> acota cuánto hacia atrás se mira, contado desde el mensaje más
        /// reciente del envío —su último recordatorio— y no desde el primer aviso.
        /// </summary>
        Task<IReadOnlyList<EnvioNotificacion>> ObtenerEnviosSinRespuesta(DateTime desde);

        /// <summary>
        /// Estampa el Message-Id de un recordatorio sobre los envíos abiertos que cubrían esas
        /// facturas. Es lo que permite reconocer después una respuesta a ese correo, que no tiene
        /// renglón propio. Devuelve cuántos envíos quedaron marcados.
        /// </summary>
        Task<int> MarcarRecordatorioEnviado(IReadOnlyList<string> movIds, string messageId, DateTime fechaEnvio);

        Task MarcarContestado(RespuestaDetectada respuesta);

        Task MarcarFallidoPorRebote(RespuestaDetectada rebote);

        Task MarcarRecordado(int idEnvio);

        Task MarcarSinRespuesta(int idEnvio);

        /// <summary>
        /// Cuándo arrancó la última corrida de --respuestas que terminó bien, o null si nunca ha
        /// terminado ninguna. Es el piso de la ventana de búsqueda en el buzón: sin él, un paro del
        /// cron deja un hueco que no se recupera solo.
        /// </summary>
        Task<DateTime?> ObtenerUltimaConciliacion();

        /// <summary>
        /// Mueve el piso. Se llama sólo cuando la corrida terminó sin error fatal: si el buzón no
        /// se pudo leer, el piso tiene que quedarse donde está para que la siguiente lo cubra.
        /// </summary>
        Task RegistrarConciliacion(DateTime inicio);
    }
}
