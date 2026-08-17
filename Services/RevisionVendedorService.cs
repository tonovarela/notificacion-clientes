using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using notificacion_clientes.DAO;
using notificacion_clientes.Entity;

namespace notificacion_clientes.Services
{
    /// <summary>
    /// Consulta las facturas sin ingresar a revisión y las agrupa por vendedor y, dentro de cada
    /// vendedor, por cliente. No envía ni imprime nada: solo devuelve la cartera lista.
    /// </summary>
    public class RevisionVendedorService
    {
        private readonly FacturaDAO _facturaDAO;

        public RevisionVendedorService(FacturaDAO facturaDAO)
        {
            _facturaDAO = facturaDAO;
        }

        public async Task<IReadOnlyList<NotificacionVendedor>> Preparar()
        {
            var facturas = await _facturaDAO.ObtenerFacturasRevisionVendedores();

            // Se agrupa por correo y no por nombre: es la llave del envío, y dos capturas distintas
            // del mismo agente ("JUAN PEREZ" / "Juan Pérez") no deben generar dos correos.
            return facturas
                .Where(f => !string.IsNullOrWhiteSpace(f.Email))
                .GroupBy(f => f.Email.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(grupo => new NotificacionVendedor
                {
                    Vendedor = ObtenerVendedor(grupo),
                    Email = grupo.Key,
                    Clientes = AgruparPorCliente(grupo)
                })
                .OrderByDescending(v => v.Saldo)
                .ToList();
        }

        private static string ObtenerVendedor(IEnumerable<FacturaRevisionVendedor> filas) =>
            filas
                .Select(f => f.Vendedor?.Trim())
                .FirstOrDefault(nombre => !string.IsNullOrWhiteSpace(nombre))
                ?? "AGENTE NO VALIDO";

        /// <summary>Dentro del correo la cartera se lee por cliente, del más atrasado al menos.</summary>
        private static List<ClienteCartera> AgruparPorCliente(IEnumerable<FacturaRevisionVendedor> filas) =>
            filas
                .GroupBy(f => f.Cliente.Trim())
                .Select(grupo => new ClienteCartera
                {
                    Cliente = grupo.Key,
                    RazonSocial = ObtenerRazonSocial(grupo),
                    Facturas = grupo.OrderBy(f => f.Vencimiento).ToList()
                })
                .OrderByDescending(c => c.DiasVencidoMaximo)
                .ThenByDescending(c => c.Saldo)
                .ToList();

        /// <summary>Si la vista no trae nombre del cliente se usa su número, que siempre viene.</summary>
        private static string ObtenerRazonSocial(IGrouping<string, FacturaRevisionVendedor> grupo) =>
            grupo
                .Select(f => f.RazonSocial?.Trim())
                .FirstOrDefault(razon => !string.IsNullOrWhiteSpace(razon))
                ?? grupo.Key;
    }
}
