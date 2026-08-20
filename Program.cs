
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
                // Las banderas fuerzan la población en una corrida manual; sin ellas manda el día.
                bool? recordatorio = args.Contains("--recordatorio") ? true
                                   : args.Contains("--primer-aviso") ? false
                                   : null;

                await new ComandoCobranza(dep).Ejecutar(inicio, previsualizar, recordatorio);
                ejecutado = true;
            }

            // Lunes a viernes, 10:00. Lee el buzón y marca quién contestó; no manda ningún correo.
            // Es lo que hace que el recordatorio de cobranza deje de insistirle a quien ya respondió.
            if (args.Contains("--respuestas"))
            {
                await new ComandoRespuestas(dep).Ejecutar(inicio);
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
            Console.WriteLine("  --respuestas           Lee el buzón y marca en notif.Envio quién contestó");
            Console.WriteLine();
            Console.WriteLine("  --previsualizar        Genera el HTML en disco sin enviar correos");
            Console.WriteLine();
            Console.WriteLine("  Sólo para --cobranza, la población se puede forzar:");
            Console.WriteLine("  --recordatorio         Insiste sobre lo ya notificado sin respuesta");
            Console.WriteLine("  --primer-aviso         Sólo facturas que nunca se han notificado");
        }
    }
}
