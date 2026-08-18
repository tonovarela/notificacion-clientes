using System;
using Microsoft.Extensions.Configuration;

namespace notificacion_clientes.Configuracion
{
    /// <summary>Política del seguimiento: cuánto se espera antes de insistir, y si se insiste.</summary>
    public class SeguimientoSettings
    {
        /// <summary>
        /// Interruptor general. En false la aplicación se comporta como antes del módulo: manda
        /// los correos y no registra nada. Existe para poder desactivar el seguimiento sin
        /// desplegar una imagen distinta si algo sale mal en producción.
        /// </summary>
        public required bool Habilitado { get; init; }

        /// <summary>
        /// Días que un envío de cobranza sigue esperando respuesta antes de cerrarse como
        /// SIN_RESPUESTA. Siete cubre el ciclo semanal: cuando sale el correo del martes
        /// siguiente, el de la semana pasada ya no espera nada.
        ///
        /// No es sólo higiene de datos: la conciliación busca en el buzón desde el pendiente más
        /// viejo, así que lo que nunca se cierra hace crecer esa ventana sin límite.
        /// </summary>
        public required int DiasVigencia { get; init; }

        /// <summary>
        /// Tope duro de cuántos días hacia atrás se lee el buzón, pase lo que pase.
        ///
        /// Es una red independiente de DiasVigencia: si por un error quedaran envíos abiertos de
        /// hace meses, sin este tope cada corrida descargaría decenas de miles de correos. Vale
        /// más perder una respuesta muy vieja que degradar la corrida diaria.
        /// </summary>
        public required int DiasVentanaMaxima { get; init; }

        public static SeguimientoSettings Cargar(IConfiguration configuracion)
        {
            var seccion = configuracion.GetSection("Seguimiento");

            var diasVigencia = int.TryParse(seccion["DiasVigencia"], out var vigencia) ? vigencia : 7;

            if (diasVigencia < 1)
                throw new InvalidOperationException(
                    "'Seguimiento:DiasVigencia' tiene que ser al menos 1: con 0 los envíos se cerrarían el mismo día que salen");

            var diasVentana = int.TryParse(seccion["DiasVentanaMaxima"], out var ventana) ? ventana : 30;

            if (diasVentana < diasVigencia)
                throw new InvalidOperationException(
                    $"'Seguimiento:DiasVentanaMaxima' ({diasVentana}) no puede ser menor que 'Seguimiento:DiasVigencia' ({diasVigencia}): " +
                    "se dejarían de leer respuestas a envíos que todavía las esperan");

            return new SeguimientoSettings
            {
                // Apagado por omisión: encenderlo exige haber corrido el script SQL y haberle dado
                // permiso de escritura al usuario de la base. Sin eso, la corrida truena.
                Habilitado = bool.TryParse(seccion["Habilitado"], out var habilitado) && habilitado,

                DiasVigencia = diasVigencia,

                DiasVentanaMaxima = diasVentana
            };
        }
    }
}
