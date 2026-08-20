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
        /// True cuando el correo insiste sobre facturas ya notificadas que nadie contestó.
        ///
        /// Lo fija el servicio según la consulta que corrió, y sólo decide qué plantilla se usa.
        /// Antes se deducía del envío del martes que se recuperaba del seguimiento, lo que además
        /// permitía colgar el recordatorio de ese hilo de correo; ahora la población sale de la
        /// consulta y ese envío ya no se busca, así que el recordatorio va como mensaje suelto.
        /// </summary>
        public bool EsRecordatorio { get; init; }

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
