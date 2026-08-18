using System;
using System.Collections.Generic;
using System.Linq;

namespace notificacion_clientes.Entity
{
    /// <summary>
    /// El estado de cuenta vencido de un cliente, ya agrupado: a quién se le manda y qué facturas
    /// se le están reclamando.
    /// </summary>
    public class NotificacionCobranza
    {
        public required string Cliente { get; init; }

        public required string RazonSocial { get; init; }

        /// <summary>Agente que lleva la cuenta; el correo lo menciona como contacto para aclaraciones.</summary>
        public string? Agente { get; init; }

        public required IReadOnlyList<ContactoCobranza> Contactos { get; init; }

        public required IReadOnlyList<FacturaCobranzaVencida> Facturas { get; init; }

        /// <summary>
        /// El envío de esta misma semana al que hay que colgarse, si existe.
        ///
        /// Null en el correo del martes —es el primer intento— y en el de un cliente que apenas
        /// cayó en vencido a media semana. Cuando trae valor, el correo sale como recordatorio
        /// dentro de ese hilo y no como un mensaje suelto.
        /// </summary>
        public EnvioNotificacion? EnvioOriginal { get; init; }

        /// <summary>True cuando este correo es el segundo intento de la semana.</summary>
        public bool EsRecordatorio => EnvioOriginal is not null;

        /// <summary>Días desde el primer correo de la semana. Cero si éste es el primero.</summary>
        public int DiasDesdeEnvioOriginal =>
            EnvioOriginal is null ? 0 : Math.Max(0, (DateTime.Today - EnvioOriginal.FechaEnvio.Date).Days);

        public int TotalFacturas => Facturas.Count;

        /// <summary>Días de la factura más atrasada: es el número que duele y va en el asunto.</summary>
        public int DiasVencidoMaximo => Facturas.Count == 0 ? 0 : Facturas.Max(f => f.DiasVencido);

        /// <summary>
        /// Saldo vencido separado por moneda.
        ///
        /// No se suma todo en un solo número a propósito. Hoy ningún cliente tiene facturas en
        /// pesos y en dólares a la vez, pero eso es un hecho de los datos y no una garantía: el
        /// día que ocurra, un total único diría una cifra falsa sin que nadie lo note.
        /// </summary>
        public IReadOnlyList<SaldoPorMoneda> Saldos =>
            Facturas
                .GroupBy(f => f.Moneda, StringComparer.OrdinalIgnoreCase)
                .Select(g => new SaldoPorMoneda { Moneda = g.Key, Total = g.Sum(f => f.TotalVencido) })
                .OrderByDescending(s => s.Total)
                .ToList();
    }

    /// <summary>Un contacto de cuentas por pagar del cliente.</summary>
    public class ContactoCobranza
    {
        /// <summary>"LIC.", "C.P.", "SR"… Viene vacío en la mayoría de los registros.</summary>
        public string? Tratamiento { get; init; }

        public required string Nombre { get; init; }

        public string? Cargo { get; init; }

        public required string Email { get; init; }

        /// <summary>El tratamiento sólo se antepone si existe; si no, el nombre va solo.</summary>
        public string NombreConTratamiento =>
            string.IsNullOrWhiteSpace(Tratamiento) ? Nombre : $"{Tratamiento.Trim()} {Nombre}";
    }

    public class SaldoPorMoneda
    {
        public required string Moneda { get; init; }

        public required decimal Total { get; init; }
    }
}
