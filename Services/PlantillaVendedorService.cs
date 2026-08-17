using System;
using System.Globalization;
using System.Linq;
using notificacion_clientes.Entity;

namespace notificacion_clientes.Services
{
    /// <summary>
    /// Convierte la cartera de un vendedor en el HTML de su correo.
    /// Todo el formato (moneda, fechas, plurales) se resuelve aquí para que la plantilla
    /// solo acomode texto ya listo.
    /// </summary>
    public class PlantillaVendedorService
    {
        private readonly PlantillaCompilada _plantilla;
        private static readonly CultureInfo Cultura = new("es-MX");

        public PlantillaVendedorService(string rutaPlantilla)
        {
            _plantilla = new PlantillaCompilada(rutaPlantilla);
        }

        public string Renderizar(NotificacionVendedor notificacion)
        {
            var modelo = new
            {
                vendedor = notificacion.Vendedor,
                sin_agente_valido = notificacion.SinAgenteValido,
                total_clientes = notificacion.Clientes.Count,
                total_facturas = notificacion.TotalFacturas,
                saldo_total = notificacion.Saldo.ToString("C", Cultura),
                dias_vencido_maximo = notificacion.DiasVencidoMaximo,
                fecha_corte = DateTime.Today.ToString("dd 'de' MMMM 'de' yyyy", Cultura),
                clientes = notificacion.Clientes.Select(c => new
                {
                    cliente = c.Cliente,
                    razon_social = c.RazonSocial,
                    total_facturas = c.Facturas.Count,
                    saldo = c.Saldo.ToString("C", Cultura),
                    dias_vencido_maximo = c.DiasVencidoMaximo,
                    facturas = c.Facturas.Select(f => new
                    {
                        factura = f.Factura,
                        mov_id = f.MovID,
                        fecha_emision = f.FechaEmision.ToString("dd/MM/yyyy", Cultura),
                        vencimiento = f.Vencimiento.ToString("dd/MM/yyyy", Cultura),
                        dias_vencido = ClienteCartera.CalcularDiasVencido(f),
                        antiguedad = DescribirAntiguedad(f.EstatusCxC),
                        saldo = f.Saldo.ToString("C", Cultura)
                    }).ToList()
                }).ToList()
            };

            return _plantilla.Renderizar(modelo);
        }

        /// <summary>
        /// La vista devuelve el rango con un prefijo de orden ("2.31-60"); en el correo solo
        /// tiene sentido la parte legible.
        /// </summary>
        private static string DescribirAntiguedad(string? estatusCxC)
        {
            if (string.IsNullOrWhiteSpace(estatusCxC))
                return "-";

            var punto = estatusCxC.IndexOf('.');

            return punto >= 0 && punto < estatusCxC.Length - 1
                ? estatusCxC[(punto + 1)..].Trim()
                : estatusCxC.Trim();
        }
    }
}
