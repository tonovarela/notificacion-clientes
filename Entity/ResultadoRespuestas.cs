using System;
using System.Collections.Generic;

namespace notificacion_clientes.Entity
{
    /// <summary>
    /// Lo que dio la lectura del buzón: qué envíos se revisaron y cuáles cambiaron de estado.
    /// Es lo que se imprime y lo que va a la bitácora.
    /// </summary>
    public class ResultadoRespuestas
    {
        /// <summary>Cuántos envíos abiertos se cruzaron contra el buzón.</summary>
        public int Revisados { get; init; }

        /// <summary>Respuestas de clientes. Cada una deja su envío en CONTESTADO.</summary>
        public IReadOnlyList<RespuestaDetectada> Respuestas { get; init; } = Array.Empty<RespuestaDetectada>();

        /// <summary>Rebotes definitivos: el correo no llegó ni va a llegar. Dejan el envío FALLIDO.</summary>
        public IReadOnlyList<RespuestaDetectada> Rebotes { get; init; } = Array.Empty<RespuestaDetectada>();

        /// <summary>
        /// Avisos de que la entrega se está retrasando. No cambian el estado de nada —el correo
        /// sigue en cola y puede entregarse solo—, pero se reportan: un retraso que se repite
        /// cada corrida acaba siendo un fallo, y conviene verlo venir.
        /// </summary>
        public IReadOnlyList<RespuestaDetectada> RebotesTemporales { get; init; } = Array.Empty<RespuestaDetectada>();
    }
}
