namespace notificacion_clientes.Entity
{
    /// <summary>Importes leídos del XML del CFDI.</summary>
    public class ImportesCfdi
    {
        public required decimal SubTotal { get; init; }

        public required decimal Iva { get; init; }

        public required decimal Total { get; init; }

        public required string Moneda { get; init; }
    }
}
