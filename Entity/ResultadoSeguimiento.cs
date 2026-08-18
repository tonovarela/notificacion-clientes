using System;
using System.Collections.Generic;
using notificacion_clientes.Services;

namespace notificacion_clientes.Entity
{
    /// <summary>
    /// Todo lo que ocurrió en una corrida de seguimiento: quién contestó, a quién se le insistió
    /// y qué se cerró sin respuesta. Es lo que se imprime y lo que va a la bitácora.
    /// </summary>
    public class ResultadoSeguimiento
    {
        /// <summary>Respuestas de clientes detectadas en el buzón.</summary>
        public IReadOnlyList<RespuestaDetectada> Respuestas { get; init; } = Array.Empty<RespuestaDetectada>();

        /// <summary>Rebotes: la dirección no existe. No son respuestas y marcan el envío FALLIDO.</summary>
        public IReadOnlyList<RespuestaDetectada> Rebotes { get; init; } = Array.Empty<RespuestaDetectada>();

        /// <summary>Envíos de cobranza que agotaron su vigencia sin respuesta y se cerraron.</summary>
        public IReadOnlyList<EnvioNotificacion> Cerrados { get; init; } = Array.Empty<EnvioNotificacion>();

        /// <summary>Cuántos envíos se revisaron contra el buzón.</summary>
        public int Conciliados { get; init; }
    }
}
