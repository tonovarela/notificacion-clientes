using System;
using Microsoft.Extensions.Configuration;

namespace notificacion_clientes.Configuracion
{
    /// <summary>Si los envíos se registran, y hasta dónde se lee el buzón al buscar respuestas.</summary>
    public class SeguimientoSettings
    {
        /// <summary>
        /// Interruptor general. En false la aplicación se comporta como antes del módulo: manda
        /// los correos y no registra nada. Existe para poder desactivar el seguimiento sin
        /// desplegar una imagen distinta si algo sale mal en producción.
        ///
        /// Apagarlo tiene ahora una consecuencia extra: el recordatorio de cobranza se arma con lo
        /// que quedó registrado, así que sin registro esa población sale vacía.
        /// </summary>
        public required bool Habilitado { get; init; }

        /// <summary>
        /// Tope duro de cuántos días hacia atrás se lee el buzón, pase lo que pase.
        ///
        /// Sin él la búsqueda IMAP crece sin límite: los envíos de cobranza ya no se cierran solos
        /// —eso se fue con el cierre por vigencia—, así que un renglón que se quede en ENVIADO
        /// anclaría la ventana para siempre. Vale más perder una respuesta muy vieja que degradar
        /// la corrida diaria.
        /// </summary>
        public required int DiasVentanaMaxima { get; init; }

        public static SeguimientoSettings Cargar(IConfiguration configuracion)
        {
            var seccion = configuracion.GetSection("Seguimiento");

            var diasVentana = int.TryParse(seccion["DiasVentanaMaxima"], out var ventana) ? ventana : 30;

            if (diasVentana < 1)
                throw new InvalidOperationException(
                    "'Seguimiento:DiasVentanaMaxima' tiene que ser al menos 1: con 0 no se leería ningún correo");

            return new SeguimientoSettings
            {
                // Apagado por omisión: encenderlo exige haber corrido el script SQL y haberle dado
                // permiso de escritura al usuario de la base. Sin eso, la corrida truena.
                Habilitado = bool.TryParse(seccion["Habilitado"], out var habilitado) && habilitado,

                DiasVentanaMaxima = diasVentana
            };
        }
    }
}
