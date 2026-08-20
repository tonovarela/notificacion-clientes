using notificacion_clientes.Entity;

namespace notificacion_clientes.DAO
{
    /// <summary>
    /// Seguimiento de envíos: qué se mandó, quién contestó y a quién toca insistirle.
    /// Implementada por <see cref="SeguimientoDAO"/> (SQL Server) y por
    /// <see cref="SeguimientoDAOJson"/> (archivo en Datos/, para probar sin depender de la VPN/DB).
    /// </summary>
    public interface ISeguimientoDAO
    {
        Task<int> Registrar(EnvioNotificacion envio);

        Task<IReadOnlyList<EnvioNotificacion>> ObtenerParaConciliar(DateTime desde);

        Task<DateTime?> ObtenerFechaPendienteMasAntiguo();

        Task<Dictionary<string, EnvioNotificacion>> ObtenerCobranzaAbiertaDeLaSemana(DateTime desde, bool modoPrueba);

        Task<IReadOnlyList<EnvioNotificacion>> ObtenerPendientesDeCierre(DateTime fechaCorte);

        Task<HashSet<string>> ObtenerClientesQueContestaronCobranza(DateTime desde, bool modoPrueba);

        Task MarcarContestado(RespuestaDetectada respuesta);

        Task MarcarFallidoPorRebote(RespuestaDetectada rebote);

        Task MarcarRecordado(int idEnvio);

        Task MarcarSinRespuesta(int idEnvio);
    }
}
