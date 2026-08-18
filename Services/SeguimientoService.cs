using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using notificacion_clientes.DAO;
using notificacion_clientes.Entity;

namespace notificacion_clientes.Services
{
    /// <summary>
    /// El puente entre la corrida de envíos y el registro en base: decide qué facturas ya no hay
    /// que volver a mandar, y deja constancia de cada correo que salió.
    ///
    /// No conoce IMAP ni recordatorios; sólo escribe lo que ocurrió en la corrida del día.
    /// </summary>
    public class SeguimientoService
    {
        private readonly SeguimientoDAO _seguimientoDAO;

        public SeguimientoService(SeguimientoDAO seguimientoDAO)
        {
            _seguimientoDAO = seguimientoDAO;
        }

        /// <summary>
        /// Quita las facturas que ya salieron en un envío anterior y descarta a los clientes que
        /// se quedan sin ninguna.
        ///
        /// Es la red de seguridad contra una doble corrida o un rango de fechas mal puesto: sin
        /// esto, la misma factura abriría un envío nuevo cada día y a los N días dispararía un
        /// recordatorio por cada uno.
        /// </summary>
        public async Task<ResultadoFiltrado> FiltrarYaNotificadas(
            IReadOnlyList<NotificacionCliente> notificaciones)
        {
            var movIds = notificaciones
                .SelectMany(n => n.Documentos.Select(d => d.MovID))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var yaNotificadas = await _seguimientoDAO.ObtenerFacturasYaNotificadas(movIds);

            if (yaNotificadas.Count == 0)
                return new ResultadoFiltrado { Notificaciones = notificaciones, Omitidas = Array.Empty<string>() };

            var filtradas = new List<NotificacionCliente>();

            foreach (var notificacion in notificaciones)
            {
                var pendientes = notificacion.Documentos
                    .Where(d => !yaNotificadas.Contains(d.MovID))
                    .ToList();

                if (pendientes.Count == 0)
                    continue;

                // Se rearma en vez de mutar: NotificacionCliente es de sólo lectura y sus totales
                // se calculan de los documentos, así que quitar uno tiene que recalcularlos.
                filtradas.Add(new NotificacionCliente
                {
                    Cliente = notificacion.Cliente,
                    RazonSocial = notificacion.RazonSocial,
                    Contactos = notificacion.Contactos,
                    Documentos = pendientes
                });
            }

            return new ResultadoFiltrado
            {
                Notificaciones = filtradas,
                Omitidas = movIds.Where(yaNotificadas.Contains).ToList()
            };
        }

        /// <summary>
        /// Deja un renglón por cada correo de la corrida, enviado o fallido.
        ///
        /// Un fallo al guardar no puede tumbar la corrida: los correos ya salieron y volver a
        /// intentarlo los duplicaría. Se registra el problema y se sigue con el siguiente.
        /// </summary>
        public async Task<ResultadoRegistro> RegistrarEnvios(
            IReadOnlyList<NotificacionCliente> notificaciones,
            IReadOnlyList<ResultadoEnvio> resultados,
            bool modoPrueba)
        {
            var porCliente = notificaciones.ToDictionary(n => n.Cliente, StringComparer.OrdinalIgnoreCase);
            var registrados = 0;
            var errores = new List<string>();

            foreach (var resultado in resultados)
            {
                if (!porCliente.TryGetValue(resultado.Cliente, out var notificacion))
                    continue;

                // Sin Message-Id no hay con qué cruzar una respuesta y la columna es NOT NULL.
                // Pasa cuando el mensaje ni siquiera se pudo armar; la bitácora ya lo reporta.
                if (string.IsNullOrWhiteSpace(resultado.MessageId))
                {
                    errores.Add($"Cliente {resultado.Cliente}: el correo no llegó a armarse, no se registró");
                    continue;
                }

                try
                {
                    await _seguimientoDAO.Registrar(new EnvioNotificacion
                    {
                        Cliente = notificacion.Cliente,
                        RazonSocial = notificacion.RazonSocial,
                        MessageId = resultado.MessageId,
                        Token = resultado.Token ?? Guid.Empty,
                        Intento = 1,
                        Asunto = resultado.Asunto ?? string.Empty,
                        Destinatarios = string.Join(", ", resultado.Destinatarios),
                        ModoPrueba = modoPrueba,
                        FechaEnvio = DateTime.Now,
                        Estado = resultado.Enviado ? EstadoEnvio.Enviado : EstadoEnvio.Fallido,
                        Error = resultado.Error,
                        Facturas = notificacion.Documentos.Select(d => new FacturaEnviada
                        {
                            MovID = d.MovID,
                            Total = d.Total,
                            Moneda = d.Moneda
                        }).ToList()
                    });

                    registrados++;
                }
                catch (Exception ex)
                {
                    errores.Add($"Cliente {resultado.Cliente}: {ex.Message}");
                }
            }

            return new ResultadoRegistro { Registrados = registrados, Errores = errores };
        }
    }

    /// <summary>Lo que quedó por notificar y lo que se saltó por estar ya notificado.</summary>
    public class ResultadoFiltrado
    {
        public required IReadOnlyList<NotificacionCliente> Notificaciones { get; init; }

        /// <summary>MovID de las facturas que ya habían salido en un envío anterior.</summary>
        public required IReadOnlyList<string> Omitidas { get; init; }
    }

    public class ResultadoRegistro
    {
        public required int Registrados { get; init; }

        /// <summary>Envíos que salieron pero no se pudieron registrar. Van a la bitácora.</summary>
        public required IReadOnlyList<string> Errores { get; init; }
    }
}
