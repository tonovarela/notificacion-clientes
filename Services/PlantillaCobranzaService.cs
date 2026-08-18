using System;
using System.Globalization;
using System.Linq;
using notificacion_clientes.Entity;

namespace notificacion_clientes.Services
{
    /// <summary>
    /// Convierte el estado de cuenta vencido de un cliente en el HTML de su correo.
    /// Todo el formato —moneda, fechas, plurales— se resuelve aquí para que la plantilla
    /// sólo acomode texto ya listo.
    /// </summary>
    public class PlantillaCobranzaService
    {
        private static readonly CultureInfo Cultura = new("es-MX");

        /// <summary>
        /// La vista devuelve el nombre de la moneda, no su código. Para formatear un importe en
        /// dólares con "$" a secas se confundiría con pesos, que es un error caro en un correo
        /// de cobranza.
        /// </summary>
        private static readonly CultureInfo CulturaDolares = new("en-US");

        private readonly PlantillaCompilada _primerAviso;
        private readonly PlantillaCompilada _recordatorio;

        /// <summary>
        /// Dos plantillas y un solo modelo. El correo del viernes no es el del martes con una
        /// frase distinta: cambia el encabezado, el orden de lo que se dice y el cierre. Tenerlas
        /// separadas permite ajustar el tono de la insistencia sin arriesgar el primer aviso.
        /// </summary>
        public PlantillaCobranzaService(string rutaPrimerAviso, string rutaRecordatorio)
        {
            _primerAviso = new PlantillaCompilada(rutaPrimerAviso);
            _recordatorio = new PlantillaCompilada(rutaRecordatorio);
        }

        public string Renderizar(NotificacionCobranza notificacion)
        {
            var modelo = new
            {
                cliente = notificacion.Cliente,
                razon_social = notificacion.RazonSocial,
                saludo = ArmarSaludo(notificacion),
                agente = notificacion.Agente,
                fecha_corte = DateTime.Today.ToString("dd 'de' MMMM 'de' yyyy", Cultura),
                dias_desde_envio_original = notificacion.DiasDesdeEnvioOriginal,
                fecha_envio_original = notificacion.EnvioOriginal?.FechaEnvio
                    .ToString("dd 'de' MMMM 'de' yyyy", Cultura) ?? string.Empty,
                total_facturas = notificacion.TotalFacturas,
                dias_vencido_maximo = notificacion.DiasVencidoMaximo,
                saldos = notificacion.Saldos.Select(s => new
                {
                    moneda = DescribirMoneda(s.Moneda),
                    total = Formatear(s.Total, s.Moneda)
                }).ToList(),
                facturas = notificacion.Facturas.Select(f => new
                {
                    factura = f.Factura,
                    condicion = string.IsNullOrWhiteSpace(f.Condicion) ? string.Empty : f.Condicion.Trim(),
                    fecha_emision = f.FechaEmision.ToString("dd/MM/yyyy", Cultura),
                    vencimiento = f.Vencimiento.ToString("dd/MM/yyyy", Cultura),
                    dias_vencido = f.DiasVencido,
                    total_vencido = Formatear(f.TotalVencido, f.Moneda)
                }).ToList()
            };

            return notificacion.EsRecordatorio
                ? _recordatorio.Renderizar(modelo)
                : _primerAviso.Renderizar(modelo);
        }

        /// <summary>Un correo va a varios contactos, así que el saludo es genérico si hay más de uno.</summary>
        private static string ArmarSaludo(NotificacionCobranza notificacion) =>
            notificacion.Contactos.Count == 1
                ? notificacion.Contactos[0].NombreConTratamiento
                : "cliente";

        /// <summary>El importe lleva el símbolo de su moneda y, si es dólar, además las siglas.</summary>
        private static string Formatear(decimal importe, string moneda) =>
            EsDolares(moneda)
                ? $"{importe.ToString("C", CulturaDolares)} USD"
                : importe.ToString("C", Cultura);

        private static string DescribirMoneda(string moneda) =>
            EsDolares(moneda) ? "USD" : "MXN";

        private static bool EsDolares(string moneda) =>
            moneda.StartsWith("Dol", StringComparison.OrdinalIgnoreCase)
            || moneda.Equals("USD", StringComparison.OrdinalIgnoreCase);
    }
}
