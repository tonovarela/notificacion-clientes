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

        /// <summary>Rebotes: la dirección no existe. No son respuestas y dejan el envío FALLIDO.</summary>
        public IReadOnlyList<RespuestaDetectada> Rebotes { get; init; } = Array.Empty<RespuestaDetectada>();
    }
}
