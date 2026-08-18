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
    /// Las facturas del día a cada cliente. Es el proceso original; lo que agrega el módulo de
    /// seguimiento es saltarse las facturas ya notificadas y dejar registro de cada correo.
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

                if (_dep.Settings.Seguimiento.Habilitado)
                    notificaciones = await DescartarYaNotificadas(notificaciones);

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

                if (_dep.Settings.Seguimiento.Habilitado)
                    await Registrar(notificaciones, resultados);
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

        /// <summary>
        /// Quita lo que ya salió en un envío anterior. Es lo que impide que una factura abra un
        /// envío nuevo cada día y dispare varios recordatorios por lo mismo.
        /// </summary>
        private async Task<IReadOnlyList<NotificacionCliente>> DescartarYaNotificadas(
            IReadOnlyList<NotificacionCliente> notificaciones)
        {
            var filtrado = await _dep.Seguimiento.FiltrarYaNotificadas(notificaciones);

            if (filtrado.Omitidas.Count > 0)
                Console.WriteLine($"Se omitieron {filtrado.Omitidas.Count} facturas ya notificadas: " +
                                  string.Join(", ", filtrado.Omitidas.Take(10)) +
                                  (filtrado.Omitidas.Count > 10 ? "..." : string.Empty));

            return filtrado.Notificaciones;
        }

        /// <summary>
        /// El registro va después de enviar y nunca tumba la corrida: los correos ya salieron y
        /// reintentarlos los duplicaría. Lo que no se pudo guardar se reporta y se sigue.
        /// </summary>
        private async Task Registrar(
            IReadOnlyList<NotificacionCliente> notificaciones,
            IReadOnlyList<ResultadoEnvio> resultados)
        {
            var registro = await _dep.Seguimiento.RegistrarEnvios(
                notificaciones, resultados, _dep.Settings.Smtp.ModoPrueba);

            Console.WriteLine();
            Console.WriteLine($"Seguimiento: {registro.Registrados} envíos registrados.");

            foreach (var error in registro.Errores)
            {
                Console.WriteLine($"  AVISO: no se registró — {error}");
                Environment.ExitCode = 1;
            }
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
