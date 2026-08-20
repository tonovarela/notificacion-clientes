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

        Task<IEnumerable<FacturaCobranzaVencida>> ObtenerFacturasCobranzaVencida();
    }
}
