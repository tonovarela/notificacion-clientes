using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using notificacion_clientes.Entity;

namespace notificacion_clientes.Services
{
    /// <summary>
    /// Lee los importes del XML del CFDI. Funciona con CFDI 3.3 y 4.0 porque toma
    /// el namespace del propio documento en lugar de fijarlo.
    /// </summary>
    public class LectorCfdi
    {
        /// <summary>Clave del IVA en el catálogo de impuestos del SAT.</summary>
        private const string ClaveIva = "002";

        /// <summary>Devuelve null si el XML no se puede leer; el llamador decide con qué respaldarse.</summary>
        public ImportesCfdi? Leer(ArchivoFactura archivo)
        {
            try
            {
                using var flujo = new MemoryStream(archivo.Contenido);
                var comprobante = XDocument.Load(flujo).Root;

                if (comprobante is null)
                    return null;

                var ns = comprobante.Name.Namespace;

                var subTotal = LeerDecimal(comprobante.Attribute("SubTotal"));
                var total = LeerDecimal(comprobante.Attribute("Total"));

                return new ImportesCfdi
                {
                    SubTotal = subTotal,
                    Iva = ObtenerIva(comprobante, ns),
                    Total = total,
                    Moneda = comprobante.Attribute("Moneda")?.Value ?? "MXN"
                };
            }
            catch (Exception)
            {
                // Un XML malformado no debe impedir el envío del correo.
                return null;
            }
        }

        /// <summary>
        /// Suma solo los traslados de IVA del nodo Impuestos raíz. Se filtra por clave 002
        /// para no incluir otros impuestos trasladados como el IEPS.
        /// </summary>
        private static decimal ObtenerIva(XElement comprobante, XNamespace ns)
        {
            var traslados = comprobante
                .Element(ns + "Impuestos")?
                .Element(ns + "Traslados")?
                .Elements(ns + "Traslado");

            if (traslados is null)
                return 0m;

            return traslados
                .Where(t => t.Attribute("Impuesto")?.Value == ClaveIva)
                .Sum(t => LeerDecimal(t.Attribute("Importe")));
        }

        /// <summary>El SAT siempre escribe los importes con punto decimal, sin importar la cultura local.</summary>
        private static decimal LeerDecimal(XAttribute? atributo) =>
            decimal.TryParse(atributo?.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var valor)
                ? valor
                : 0m;
    }
}
