using System;

namespace notificacion_clientes.Entity
{
    /// <summary>
    /// Un renglón de cobranza vencida, tal como lo devuelve la vista de antigüedad de saldos.
    ///
    /// La consulta trae una fila por factura y por contacto de cuentas por pagar, así que un
    /// cliente con tres contactos y cuatro facturas produce doce filas: agrupar es trabajo del
    /// servicio, no de la vista.
    /// </summary>
    public class FacturaCobranzaVencida
    {
        public required string Cliente { get; set; }

        /// <summary>Razón social del cliente (columna Nombre de la vista de antigüedad).</summary>
        public required string RazonSocial { get; set; }

        /// <summary>Documento completo como se ve en el ERP: "Factura Electronica CFDI12345".</summary>
        public required string Factura { get; set; }

        /// <summary>
        /// El folio a secas: "CFDI12345". Es lo que se guarda en EnvioFactura, y va sin el prefijo
        /// del tipo de documento para que case con el MovID que usan las facturas del día: las dos
        /// tablas se cruzan por esta columna para saber si una factura ya se notificó.
        /// </summary>
        public required string MovID { get; set; }

        public required DateTime FechaEmision { get; set; }

        /// <summary>Condición de pago pactada: "30 Dias", "50% Anticipo Resto 8 días", etc.</summary>
        public string? Condicion { get; set; }

        public required DateTime Vencimiento { get; set; }

        /// <summary>
        /// La vista devuelve el nombre y no el código: "Pesos" o "Dolares". Llega como CHAR, con
        /// espacios de relleno, y se agrupa por este valor: sin recortarlo, "Pesos" y "Pesos   "
        /// serían dos monedas distintas y el total saldría partido en dos renglones.
        /// </summary>
        public required string Moneda
        {
            get => _moneda;
            set => _moneda = value?.Trim() ?? string.Empty;
        }

        private string _moneda = string.Empty;

        public required decimal TotalVencido { get; set; }

        /// <summary>Agente que lleva la cuenta. Va en el correo para que el cliente sepa a quién buscar.</summary>
        public string? NombreAgente { get; set; }

        /// <summary>Tratamiento del contacto: "LIC.", "C.P.", "SR". Viene vacío la mayoría de las veces.</summary>
        public string? Tratamiento { get; set; }

        /// <summary>Nombre del contacto de cuentas por pagar. Null si el CRM no tiene ninguno.</summary>
        public string? Nombre { get; set; }

        public string? Cargo { get; set; }

        /// <summary>Correo del contacto CXP. Null cuando el cliente no tiene contacto marcado.</summary>
        public string? Email { get; set; }

        /// <summary>
        /// Días transcurridos desde el vencimiento. Nunca negativo: la vista sólo trae vencidas,
        /// pero una fecha capturada a futuro no debe mostrarse como "-3 días vencida".
        /// </summary>
        public int DiasVencido => Math.Max(0, (DateTime.Today - Vencimiento.Date).Days);
    }
}
