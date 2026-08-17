using System;
using System.Collections.Generic;
using System.Linq;

namespace notificacion_clientes.Entity
{
    /// <summary>
    /// La cartera de un vendedor ya agrupada: a quién se le manda y qué facturas trae pendientes
    /// de ingresar a revisión, repartidas por cliente.
    /// </summary>
    public class NotificacionVendedor
    {
        public required string Vendedor { get; init; }

        public required string Email { get; init; }

        public required IReadOnlyList<ClienteCartera> Clientes { get; init; }

        public IEnumerable<FacturaRevisionVendedor> Facturas => Clientes.SelectMany(c => c.Facturas);

        public int TotalFacturas => Clientes.Sum(c => c.Facturas.Count);

        public decimal Saldo => Clientes.Sum(c => c.Saldo);

        /// <summary>Días vencidos de la factura más atrasada: es el número que duele y va en el asunto.</summary>
        public int DiasVencidoMaximo => Clientes.Count == 0 ? 0 : Clientes.Max(c => c.DiasVencidoMaximo);

        /// <summary>True cuando el CRM no tenía agente y la cartera cayó en el buzón de cobranza.</summary>
        public bool SinAgenteValido => Vendedor.Equals("AGENTE NO VALIDO", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Las facturas de un mismo cliente dentro de la cartera de un vendedor.</summary>
    public class ClienteCartera
    {
        public required string Cliente { get; init; }

        public required string RazonSocial { get; init; }

        public required IReadOnlyList<FacturaRevisionVendedor> Facturas { get; init; }

        public decimal Saldo => Facturas.Sum(f => f.Saldo);

        public int DiasVencidoMaximo => Facturas.Count == 0 ? 0 : Facturas.Max(CalcularDiasVencido);

        /// <summary>
        /// Días transcurridos desde el vencimiento. Nunca negativo: una factura que aún no vence
        /// cuenta como cero para que la plantilla no muestre "-3 días vencida".
        /// </summary>
        public static int CalcularDiasVencido(FacturaRevisionVendedor factura) =>
            Math.Max(0, (DateTime.Today - factura.Vencimiento.Date).Days);
    }
}
