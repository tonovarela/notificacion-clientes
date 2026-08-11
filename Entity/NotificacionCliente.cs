using System;
using System.Collections.Generic;
using System.Linq;

namespace notificacion_clientes.Entity
{
    /// <summary>Todo lo que hace falta para notificar a un cliente: a quién se le manda y qué se le manda.</summary>
    public class NotificacionCliente
    {
        public required string Cliente { get; init; }

        /// <summary>Razón social del cliente; si el CRM no la tiene, cae al número de cliente.</summary>
        public required string RazonSocial { get; init; }

        public required IReadOnlyList<Contacto> Contactos { get; init; }

        public required IReadOnlyList<DocumentoFactura> Documentos { get; init; }

        public decimal SubTotal => Documentos.Sum(d => d.SubTotal);

        public decimal Iva => Documentos.Sum(d => d.Iva);

        public decimal Total => Documentos.Sum(d => d.Total);

        /// <summary>True si a alguna factura no se le pudo leer el XML y sus importes vienen de la base.</summary>
        public bool TieneImportesEstimados => Documentos.Any(d => !d.ImportesDelXml);
    }

    public class Contacto
    {
        public required string Nombre { get; init; }

        public string? Cargo { get; init; }

        public required string Email { get; init; }
    }

    /// <summary>Una factura del cliente junto con sus archivos ya descargados (XML y PDF).</summary>
    public class DocumentoFactura
    {
        public required string MovID { get; init; }

        public required decimal SubTotal { get; init; }

        public required decimal Iva { get; init; }

        public required decimal Total { get; init; }

        public string Moneda { get; init; } = "MXN";

        /// <summary>
        /// False cuando no se pudo leer el XML y los importes se tomaron de la base,
        /// donde no hay desglose de IVA.
        /// </summary>
        public required bool ImportesDelXml { get; init; }

        public required string Periodo { get; init; }

        public required string Ejercicio { get; init; }

        public required IReadOnlyList<ArchivoFactura> Archivos { get; init; }

        public ArchivoFactura? Xml => Archivos.FirstOrDefault(a => a.Tipo == TipoArchivo.Xml);

        public ArchivoFactura? Pdf => Archivos.FirstOrDefault(a => a.Tipo == TipoArchivo.Pdf);
    }
}
