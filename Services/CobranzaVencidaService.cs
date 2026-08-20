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
    /// El correo sale martes y viernes, y son dos poblaciones distintas que no se traslapan:
    ///
    ///   martes  → facturas vencidas que nunca se han notificado          (primer aviso)
    ///   viernes → facturas ya notificadas cuyo envío sigue sin contestar (recordatorio)
    ///
    /// Las dos las resuelve la consulta, cruzando la antigüedad de saldos contra CorreosCXC.notif
    /// dentro del mismo SELECT. Este servicio ya no consulta el seguimiento: sólo elige cuál de
    /// las dos consultas corre y agrupa el resultado por cliente.
    /// </summary>
    public class CobranzaVencidaService
    {
        private readonly IFacturaDAO _facturaDAO;

        public CobranzaVencidaService(IFacturaDAO facturaDAO)
        {
            _facturaDAO = facturaDAO;
        }

        /// <summary>
        /// El estado de cuenta de cada cliente al que le toca correo hoy.
        ///
        /// <paramref name="esRecordatorio"/> elige la población: false trae lo que nunca se ha
        /// notificado, true lo que ya se notificó y nadie contestó. Se recibe como parámetro en
        /// vez de mirar el día de la semana aquí dentro: así una corrida manual un miércoles hace
        /// lo que uno le pide, y no algo distinto según el calendario.
        /// </summary>
        public async Task<ResultadoCobranza> Preparar(
            bool esRecordatorio,
            CancellationToken cancelacion = default)
        {
            var filas = esRecordatorio
                ? (await _facturaDAO.ObtenerFacturasCobranzaVencidaSinContestar()).ToList()
                : (await _facturaDAO.ObtenerFacturasCobranzaVencida()).ToList();

            var notificaciones = filas
                .Where(f => !string.IsNullOrWhiteSpace(f.Cliente))
                .GroupBy(f => f.Cliente.Trim())
                .Select(grupo => Agrupar(grupo, esRecordatorio))
                .OrderByDescending(n => n.DiasVencidoMaximo)
                .ToList();

            // La consulta ya descarta a los clientes sin contacto de cuentas por pagar y, en el
            // recordatorio, a quien ya contestó. Aquí todo lo que llega es notificable.
            return new ResultadoCobranza { Notificaciones = notificaciones };
        }

        private static NotificacionCobranza Agrupar(
            IGrouping<string, FacturaCobranzaVencida> grupo,
            bool esRecordatorio) =>
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
                //
                // Se agrupa por MovID y no por Factura. Factura es texto de presentación —"Factura
                // Electronica CFDI123"— y agruparlo confunde dos documentos distintos en cuanto ese
                // texto se repite, que es justo lo que pasa si la vista lo arma mal. MovID es la
                // identidad del documento: la misma llave que usan EnvioFactura y las dos consultas
                // de cobranza, así que agrupar por otra cosa las desalinea.
                Facturas = grupo
                    .GroupBy(f => f.MovID, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .OrderBy(f => f.Vencimiento)
                    .ToList(),
                EsRecordatorio = esRecordatorio
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

    /// <summary>Lo que hay que notificar en esta corrida.</summary>
    public class ResultadoCobranza
    {
        public required IReadOnlyList<NotificacionCobranza> Notificaciones { get; init; }
    }
}
