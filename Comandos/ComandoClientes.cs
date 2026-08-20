using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using notificacion_clientes.Entity;
using notificacion_clientes.Services;

namespace notificacion_clientes.Comandos
{
    /// <summary>
    /// Las facturas del día a cada cliente. No lleva seguimiento: a diferencia de cobranza, aquí
    /// no hay recordatorio ni exclusión por respuesta, así que no hace falta el registro.
    /// </summary>
    public class ComandoClientes
    {
        private readonly Dependencias _dep;

        public ComandoClientes(Dependencias dep)
        {
            _dep = dep;
        }

        public async Task Ejecutar(DateTime inicio, bool previsualizar)
        {
            // Si algo truena a medio camino igual se deja la evidencia en disco con el motivo:
            // una bitácora que falta no sirve para aclarar nada después.
            IReadOnlyList<NotificacionCliente> notificaciones = Array.Empty<NotificacionCliente>();
            IReadOnlyList<ResultadoEnvio> resultados = Array.Empty<ResultadoEnvio>();
            string? errorFatal = null;

            try
            {
                Console.WriteLine("Obteniendo facturas...");
                notificaciones = await _dep.Notificacion.Preparar();

                _dep.Reporte.Imprimir(notificaciones);

                // Con --previsualizar se genera el HTML en disco y no se envía nada.
                if (previsualizar)
                {
                    foreach (var notificacion in notificaciones)
                        await Previsualizacion.Guardar(
                            $"previsualizacion-cliente-{notificacion.Cliente}.html",
                            _dep.Plantilla.Renderizar(notificacion));

                    return;
                }

                Console.WriteLine();
                Console.WriteLine("Enviando correos...");
                resultados = await _dep.Correo.Enviar(notificaciones);
                _dep.Reporte.ImprimirEnvios(resultados, _dep.Settings.Smtp.ModoPrueba);
            }
            catch (Exception ex)
            {
                errorFatal = ex.Message;
                Console.WriteLine($"ERROR: la ejecución no se completó: {ex.Message}");
                Environment.ExitCode = 1;
            }

            var rutaBitacora = await _dep.Bitacora.Escribir(notificaciones, resultados, inicio, errorFatal);
            Console.WriteLine();
            Console.WriteLine($"Bitácora: {rutaBitacora}");
        }

    }

    /// <summary>El HTML de previsualización se guarda junto al ejecutable, sin enviar nada.</summary>
    public static class Previsualizacion
    {
        public static async Task Guardar(string nombreArchivo, string html)
        {
            var ruta = Path.Combine(AppContext.BaseDirectory, nombreArchivo);
            await File.WriteAllTextAsync(ruta, html);
            Console.WriteLine($"Previsualización: {ruta}");
        }

        /// <summary>El correo se usa como nombre de archivo; la arroba y el punto estorban.</summary>
        public static string Sanear(string valor) =>
            string.Concat(valor.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
    }
}
