using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using notificacion_clientes.DAO;
using notificacion_clientes.Entity;

namespace notificacion_clientes.Services
{
    /// <summary>
    /// Arma el estado de cuenta vencido de cada cliente y decide a quién le toca recibirlo.
    ///
    /// El correo sale martes y viernes. El viernes se excluye a quien ya contestó el del martes:
    /// insistirle a alguien que respondió hace tres días es la forma más rápida de que el correo
    /// deje de leerse.
    /// </summary>
    public class CobranzaVencidaService
    {
        private readonly FacturaDAO _facturaDAO;
        private readonly SeguimientoDAO _seguimientoDAO;

        public CobranzaVencidaService(FacturaDAO facturaDAO, SeguimientoDAO seguimientoDAO)
        {
            _facturaDAO = facturaDAO;
            _seguimientoDAO = seguimientoDAO;
        }

        /// <summary>
        /// El estado de cuenta de cada cliente con saldo vencido.
        ///
        /// <paramref name="aplicarExclusionSemanal"/> enciende la regla del viernes. Se recibe como
        /// parámetro en vez de mirar el día de la semana aquí dentro: así una corrida manual un
        /// miércoles hace lo que uno le pide, y no algo distinto según el calendario.
        /// </summary>
        public async Task<ResultadoCobranza> Preparar(
            bool aplicarExclusionSemanal,
            bool modoPrueba,
            CancellationToken cancelacion = default)
        {
            var filas = (await _facturaDAO.ObtenerFacturasCobranzaVencida()).ToList();

            var notificaciones = filas
                .Where(f => !string.IsNullOrWhiteSpace(f.Cliente))
                .GroupBy(f => f.Cliente.Trim())
                .Select(Agrupar)
                .OrderByDescending(n => n.DiasVencidoMaximo)
                .ToList();

            // La consulta ya descarta a los clientes sin contacto de cuentas por pagar, así que
            // aquí todo lo que llega es notificable.
            var notificables = notificaciones;

            IReadOnlyList<NotificacionCobranza> excluidos = Array.Empty<NotificacionCobranza>();

            if (aplicarExclusionSemanal)
            {
                var inicioDeSemana = InicioDeSemana();

                var contestaron = await _seguimientoDAO.ObtenerClientesQueContestaronCobranza(inicioDeSemana, modoPrueba);

                if (contestaron.Count > 0)
                {
                    excluidos = notificables.Where(n => contestaron.Contains(n.Cliente)).ToList();
                    notificables = notificables.Where(n => !contestaron.Contains(n.Cliente)).ToList();
                }

                // A quien ya recibió el correo del martes y no contestó, el de hoy le llega como
                // recordatorio dentro de ese mismo hilo.
                var abiertos = await _seguimientoDAO.ObtenerCobranzaAbiertaDeLaSemana(inicioDeSemana, modoPrueba);

                if (abiertos.Count > 0)
                {
                    notificables = notificables
                        .Select(n => abiertos.TryGetValue(n.Cliente, out var original) ? Colgar(n, original) : n)
                        .ToList();
                }
            }

            return new ResultadoCobranza
            {
                Notificaciones = notificables,
                ExcluidosPorRespuesta = excluidos
            };
        }

        /// <summary>Rearma la notificación atándola al envío del que es continuación.</summary>
        private static NotificacionCobranza Colgar(NotificacionCobranza notificacion, EnvioNotificacion original) =>
            new()
            {
                Cliente = notificacion.Cliente,
                RazonSocial = notificacion.RazonSocial,
                Agente = notificacion.Agente,
                Contactos = notificacion.Contactos,
                Facturas = notificacion.Facturas,
                EnvioOriginal = original
            };

        /// <summary>
        /// El lunes de la semana en curso. Es la ventana de la regla del viernes: se busca la
        /// respuesta al correo del martes, no a uno de hace un mes.
        /// </summary>
        private static DateTime InicioDeSemana()
        {
            var hoy = DateTime.Today;
            var desdeElLunes = ((int)hoy.DayOfWeek + 6) % 7;   // domingo = 0 en .NET
            return hoy.AddDays(-desdeElLunes);
        }

        private static NotificacionCobranza Agrupar(IGrouping<string, FacturaCobranzaVencida> grupo) =>
            new()
            {
                Cliente = grupo.Key,
                RazonSocial = grupo
                    .Select(f => f.RazonSocial?.Trim())
                    .FirstOrDefault(razon => !string.IsNullOrWhiteSpace(razon)) ?? grupo.Key,
                Agente = grupo
                    .Select(f => f.NombreAgente?.Trim())
                    .FirstOrDefault(agente => !string.IsNullOrWhiteSpace(agente)),
                Contactos = ObtenerContactos(grupo),
                // La consulta repite cada factura una vez por contacto: sin esto, un cliente con
                // tres contactos vería su saldo multiplicado por tres.
                Facturas = grupo
                    .GroupBy(f => f.Factura, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .OrderBy(f => f.Vencimiento)
                    .ToList()
            };

        private static List<ContactoCobranza> ObtenerContactos(IEnumerable<FacturaCobranzaVencida> filas) =>
            filas
                .Where(f => !string.IsNullOrWhiteSpace(f.Email))
                .GroupBy(f => f.Email!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => new ContactoCobranza
                {
                    Tratamiento = g.First().Tratamiento,
                    // El nombre puede venir vacío aunque el correo exista; el saludo cae a genérico.
                    Nombre = string.IsNullOrWhiteSpace(g.First().Nombre) ? "cliente" : g.First().Nombre!.Trim(),
                    Cargo = g.First().Cargo,
                    Email = g.Key
                })
                .ToList();
    }

    /// <summary>Lo que hay que notificar y lo que se saltó a propósito.</summary>
    public class ResultadoCobranza
    {
        public required IReadOnlyList<NotificacionCobranza> Notificaciones { get; init; }

        /// <summary>Clientes que ya contestaron esta semana y por eso no reciben el correo del viernes.</summary>
        public required IReadOnlyList<NotificacionCobranza> ExcluidosPorRespuesta { get; init; }
    }
}
