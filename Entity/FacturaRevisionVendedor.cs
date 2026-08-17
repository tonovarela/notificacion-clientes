using System;

namespace notificacion_clientes.Entity
{
    /// <summary>
    /// Un renglón de la cartera: una factura que el cliente todavía no ingresa a revisión,
    /// tal como la devuelve la consulta de antigüedad de saldos.
    /// </summary>
    public class FacturaRevisionVendedor
    {
        public required string Cliente { get; set; }

        public required string MovID { get; set; }

        /// <summary>Razón social del cliente (NombreCte en la vista de antigüedad).</summary>
        public required string RazonSocial { get; set; }

        /// <summary>Documento completo tal como se ve en el ERP: "Factura Electronica CFDI12345".</summary>
        public required string Factura { get; set; }

        public required DateTime FechaEmision { get; set; }

        public required DateTime Vencimiento { get; set; }

        public required decimal Saldo { get; set; }

        /// <summary>Rango de antigüedad que ya calcula la vista: "2.31-60", "3.61-90", etc.</summary>
        public string? EstatusCxC { get; set; }

        public required string Vendedor { get; set; }

        public required string Email { get; set; }
    }
}
