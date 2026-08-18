using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using notificacion_clientes.Entity;
using notificacion_clientes.Services;

namespace notificacion_clientes.Comandos
{
    /// <summary>
    /// Avisa a cada vendedor de las facturas que sus clientes no han ingresado a revisión.
    /// No entra al seguimiento: no se espera acuse de un vendedor interno.
    /// </summary>
    public class ComandoVendedores
    {
        private readonly Dependencias _dep;

        public ComandoVendedores(Dependencias dep)
        {
            _dep = dep;
        }

        public async Task Ejecutar(DateTime inicio, bool previsualizar)
        {
            IReadOnlyList<NotificacionVendedor> notificaciones = Array.Empty<NotificacionVendedor>();
            IReadOnlyList<ResultadoEnvio> resultados = Array.Empty<ResultadoEnvio>();
            string? errorFatal = null;

            try
            {
                Console.WriteLine("Obteniendo facturas sin ingresar a revisión...");
                notificaciones = await _dep.RevisionVendedor.Preparar();
                _dep.Reporte.ImprimirVendedores(notificaciones);

                if (previsualizar)
                {
                    foreach (var notificacion in notificaciones)
                        await Previsualizacion.Guardar(
                            $"previsualizacion-vendedor-{Previsualizacion.Sanear(notificacion.Email)}.html",
                            _dep.PlantillaVendedor.Renderizar(notificacion));

                    return;
                }

                Console.WriteLine();
                Console.WriteLine("Enviando correos a vendedores...");
                resultados = await _dep.Correo.EnviarVendedores(notificaciones);
                _dep.Reporte.ImprimirEnvios(resultados, _dep.Settings.Smtp.ModoPruebaVendedores);
            }
            catch (Exception ex)
            {
                errorFatal = ex.Message;
                Console.WriteLine($"ERROR: la ejecución no se completó: {ex.Message}");
                Environment.ExitCode = 1;
            }

            var rutaBitacora = await _dep.Bitacora.EscribirVendedores(notificaciones, resultados, inicio, errorFatal);
            Console.WriteLine();
            Console.WriteLine($"Bitácora: {rutaBitacora}");
        }
    }
}
