using System;
using System.Globalization;
using System.IO;
using System.Linq;
using notificacion_clientes.Entity;

namespace notificacion_clientes.Services
{
    /// <summary>
    /// Convierte una NotificacionCliente en el HTML del correo usando la plantilla Scriban.
    /// La plantilla se lee y compila una sola vez.
    /// </summary>
    public class PlantillaService
    {
        private readonly PlantillaCompilada _plantilla;
        private static readonly CultureInfo Cultura = new("es-MX");

        public PlantillaService(string rutaPlantilla)
        {
            _plantilla = new PlantillaCompilada(rutaPlantilla);
        }

        public string Renderizar(NotificacionCliente notificacion)
        {
            var modelo = new
            {
                cliente = notificacion.Cliente,
                razon_social = notificacion.RazonSocial,
                saludo = ArmarSaludo(notificacion),
                total_documentos = notificacion.Documentos.Count,
                total_archivos = notificacion.Documentos.Sum(d => d.Archivos.Count),
                subtotal_general = notificacion.SubTotal.ToString("C", Cultura),
                iva_general = notificacion.Iva.ToString("C", Cultura),
                total_general = notificacion.Total.ToString("C", Cultura),
                documentos = notificacion.Documentos.Select(d => new
                {
                    mov_id = d.MovID,
                    periodo = d.Periodo,
                    ejercicio = d.Ejercicio,
                    subtotal = d.SubTotal.ToString("C", Cultura),
                    iva = d.Iva.ToString("C", Cultura),
                    total = d.Total.ToString("C", Cultura)
                }).ToList()
            };

            return _plantilla.Renderizar(modelo);
        }

        /// <summary>Un solo correo va a varios contactos, así que el saludo es genérico si hay más de uno.</summary>
        private static string ArmarSaludo(NotificacionCliente notificacion) =>
            notificacion.Contactos.Count == 1
                ? notificacion.Contactos[0].Nombre
                : "cliente";
    }
}
