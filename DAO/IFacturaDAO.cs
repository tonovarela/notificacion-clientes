using notificacion_clientes.Entity;

namespace notificacion_clientes.DAO
{
    /// <summary>
    /// Fuente de datos de facturas. Implementada por <see cref="FacturaDAO"/> (SQL Server) y por
    /// <see cref="FacturaDAOJson"/> (archivos en Datos/, para probar sin depender de la VPN/DB).
    /// </summary>
    public interface IFacturaDAO
    {
        Task<IEnumerable<Factura>> Obtener(int diasAtras = 0);

        Task<IEnumerable<FacturaRevisionVendedor>> ObtenerFacturasRevisionVendedores();

        /// <summary>Primer aviso: facturas vencidas que todavía no se le han notificado a nadie.</summary>
        Task<IEnumerable<FacturaCobranzaVencida>> ObtenerFacturasCobranzaVencida();

        /// <summary>
        /// Recordatorio: facturas vencidas que ya se notificaron y cuyo envío sigue sin contestar.
        /// La exclusión de quien ya respondió se hace dentro del SELECT, cruzando contra
        /// CorreosCXC.notif; por eso el servicio ya no necesita consultar el seguimiento aparte.
        /// </summary>
        Task<IEnumerable<FacturaCobranzaVencida>> ObtenerFacturasCobranzaVencidaSinContestar();
    }
}
