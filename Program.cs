
using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using notificacion_clientes.Comandos;
using notificacion_clientes.Configuracion;

namespace notificacion_clientes
{
    public class Program
    {
        /// <summary>
        /// Los importes y las fechas se formatean en español de México en todo el programa.
        /// Se fija aquí porque dentro del contenedor no hay LANG y .NET cae en la cultura
        /// invariante: los montos saldrían como '¤931,872.08' en vez de '$931,872.08'.
        /// </summary>
        private static readonly CultureInfo Cultura = new("es-MX");

        public static async Task Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = Cultura;
            CultureInfo.DefaultThreadCurrentUICulture = Cultura;

            var inicio = DateTime.Now;
            var previsualizar = args.Contains("--previsualizar");

            using var dep = new Dependencias(AppSettings.Cargar());

            // Procesos distintos comparten el mismo ejecutable porque comparten configuración,
            // SMTP y bitácora. Se eligen por argumento para poder programarlos en horarios
            // distintos, y el argumento es obligatorio: sin él no sale ningún correo y el
            // programa terminaría en 0, que se ve igual que una corrida exitosa.
            var ejecutado = false;

            // Lunes a viernes, 18:00  //Facturas a revision
            if (args.Contains("--clientes"))
            {
                await new ComandoClientes(dep).Ejecutar(inicio, previsualizar);
                ejecutado = true;
            }

            //  Viernes 09:00. Facturas no ingresadas a revisión, a cada vendedor
            if (args.Contains("--vendedores"))
            {
                await new ComandoVendedores(dep).Ejecutar(inicio, previsualizar);
                ejecutado = true;
            }

            // Martes y viernes, 09:00. El viernes excluye a quien contestó el correo del martes.
            if (args.Contains("--cobranza"))
            {
                // Las banderas fuerzan la regla en una corrida manual; sin ellas manda el día.
                bool? exclusion = args.Contains("--con-exclusion") ? true
                                : args.Contains("--sin-exclusion") ? false
                                : null;

                await new ComandoCobranza(dep).Ejecutar(inicio, previsualizar, exclusion);
                ejecutado = true;
            }

            // Lunes a viernes, 10:00. Detecta quién contestó y cierra lo que agotó su vigencia.
            // Es lo que alimenta la regla del viernes de cobranza; no manda ningún correo.
            if (args.Contains("--seguimiento"))
            {
                await new ComandoSeguimiento(dep).Ejecutar(inicio, cerrarVencidos: true);
                ejecutado = true;
            }

            // Sólo la conciliación, sin cerrar nada. Para correr a mano y ver qué detecta.
            if (args.Contains("--revisar-respuestas"))
            {
                await new ComandoSeguimiento(dep).Ejecutar(inicio, cerrarVencidos: false);
                ejecutado = true;
            }

            if (!ejecutado)
            {
                ImprimirUso();
                Environment.ExitCode = 64;   // EX_USAGE
                return;
            }

            Console.WriteLine("Termino de ejecutar el programa.");
        }

        /// <summary>
        /// Sin argumento válido no se hace nada y se sale con 64. Terminar en 0 sin mandar un
        /// solo correo pasaría por corrida exitosa, que es la falla más difícil de notar.
        /// </summary>
        private static void ImprimirUso()
        {
            Console.WriteLine("Uso: notificacion-clientes <comando> [--previsualizar]");
            Console.WriteLine();
            Console.WriteLine("  --clientes             Facturas del día a cada cliente");
            Console.WriteLine("  --vendedores           Cartera sin ingresar a revisión, a cada vendedor");
            Console.WriteLine("  --cobranza             Estado de cuenta vencido a cada cliente");
            Console.WriteLine("  --seguimiento          Detecta acuses y cierra lo que agotó su vigencia");
            Console.WriteLine("  --revisar-respuestas   Sólo la detección, sin cerrar nada");
            Console.WriteLine();
            Console.WriteLine("  --previsualizar        Genera el HTML en disco sin enviar correos");
            Console.WriteLine();
            Console.WriteLine("  Sólo para --cobranza, la regla del viernes se puede forzar:");
            Console.WriteLine("  --con-exclusion        Omite a quien ya contestó esta semana");
            Console.WriteLine("  --sin-exclusion        Notifica a todos, hayan contestado o no");
        }
    }
}
