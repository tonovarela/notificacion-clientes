namespace notificacion_clientes.Entity
{
    /// <summary>
    /// Qué acabó pasando con una entrega, según el propio servidor de correo (campo Action del
    /// RFC 3464). La diferencia entre las dos primeras es la que decide si hay que hacer algo:
    /// un fracaso definitivo obliga a corregir el dato, un retraso sólo hay que dejarlo correr.
    /// </summary>
    public enum ResultadoEntrega
    {
        /// <summary>Ya no se va a reintentar. El correo no llegó y no va a llegar.</summary>
        Fallida,

        /// <summary>Sigue en cola. Todavía puede entregarse; no hay nada que corregir.</summary>
        Retrasada,

        /// <summary>Sí se entregó. El aviso es informativo y no cambia nada.</summary>
        Entregada
    }

    /// <summary>
    /// Lo que dice un aviso de no entrega (el "rebote") una vez leído su reporte formal.
    ///
    /// Se saca del adjunto message/delivery-status, no del texto del correo: ese texto lo escribe
    /// cada servidor a su manera y en su idioma, mientras que el reporte es el mismo en todos y
    /// trae lo único que importa —qué dirección, qué código y de qué envío era—.
    /// </summary>
    public class InformeRebote
    {
        public required ResultadoEntrega Resultado { get; init; }

        /// <summary>La dirección que no recibió el correo, tal como la reporta el servidor.</summary>
        public string? Destinatario { get; init; }

        /// <summary>
        /// El código del RFC 3463. La primera cifra es la que manda: 5 es definitivo —5.1.1 es
        /// buzón inexistente, que es el caso que interesa a cobranza— y 4 es temporal.
        /// </summary>
        public string? Estado { get; init; }

        /// <summary>La respuesta cruda del servidor que rechazó: 'smtp; 550 5.1.1 User unknown'.</summary>
        public string? Diagnostico { get; init; }

        /// <summary>
        /// El identificador de sobre con el que salió el correo, devuelto tal cual. Es nuestro
        /// token, y con él el rebote se casa con su envío sin depender de ningún header de hilo.
        /// </summary>
        public string? EnvelopeIdOriginal { get; init; }

        /// <summary>Qué servidor reporta. Sirve para saber si el rechazo fue nuestro o del cliente.</summary>
        public string? ServidorQueReporta { get; init; }

        /// <summary>El correo no llegó y no se va a reintentar: el envío se cierra como FALLIDO.</summary>
        public bool EsDefinitivo => Resultado == ResultadoEntrega.Fallida;

        /// <summary>
        /// Un 5.x.x señala a la dirección: no existe, no acepta correo o el dominio está mal.
        /// Es lo que distingue "corrijan el CRM" de "el servidor del cliente estaba caído".
        /// </summary>
        public bool CulpaDeLaDireccion => Estado?.StartsWith('5') == true;

        public override string ToString()
        {
            var destino = Destinatario ?? "destinatario no identificado";
            var codigo = string.IsNullOrWhiteSpace(Estado) ? string.Empty : $" [{Estado}]";
            var motivo = string.IsNullOrWhiteSpace(Diagnostico) ? string.Empty : $": {Diagnostico}";

            return $"{destino}{codigo}{motivo}";
        }
    }
}
