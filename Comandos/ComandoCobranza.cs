using notificacion_clientes.Entity;
using notificacion_clientes.Services;

namespace notificacion_clientes.Comandos
{
    /// <summary>
    /// El estado de cuenta vencido a cada cliente. Sale martes y viernes a las 09:00.
    ///
    /// El viernes se excluye a quien ya contestó el correo del martes: insistirle a alguien que
    /// respondió hace tres días es la forma más rápida de que el correo deje de leerse.
    /// </summary>
    public class ComandoCobranza
    {
        private readonly Dependencias _dep;

        public ComandoCobranza(Dependencias dep)
        {
            _dep = dep;
        }

        public async Task Ejecutar(DateTime inicio, bool previsualizar, bool? forzarExclusion)
        {
            // La regla es del viernes, pero se decide aquí y no dentro del servicio para que una
            // corrida manual sea predecible: --con-exclusion y --sin-exclusion la fuerzan.
            var aplicarExclusion = forzarExclusion ?? (inicio.DayOfWeek == DayOfWeek.Friday);

            var cobranza = new ResultadoCobranza
            {
                Notificaciones = Array.Empty<NotificacionCobranza>(),
                ExcluidosPorRespuesta = Array.Empty<NotificacionCobranza>()
            };
            IReadOnlyList<ResultadoEnvio> resultados = Array.Empty<ResultadoEnvio>();
            string? errorFatal = null;

            try
            {
                Console.WriteLine("Obteniendo cobranza vencida...");

                if (aplicarExclusion && !_dep.Settings.Seguimiento.Habilitado)
                {
                    // Sin seguimiento no hay registro de quién contestó, así que la exclusión no
                    // puede aplicarse. Se avisa fuerte: la alternativa silenciosa es insistirle a
                    // todos, incluidos los que ya respondieron el martes.
                    Console.WriteLine("AVISO: toca aplicar la exclusión semanal, pero el seguimiento está");
                    Console.WriteLine("       deshabilitado (Seguimiento:Habilitado = false). No hay registro de");
                    Console.WriteLine("       quién contestó, así que se notificará a TODOS los clientes vencidos.");
                    aplicarExclusion = false;
                }

                cobranza = await _dep.CobranzaVencida.Preparar(aplicarExclusion, _dep.Settings.Smtp.ModoPrueba);
                _dep.Reporte.ImprimirCobranza(cobranza);

                if (previsualizar)
                {
                    foreach (var notificacion in cobranza.Notificaciones)
                        await Previsualizacion.Guardar(
                            $"previsualizacion-cobranza-{notificacion.Cliente}.html",
                            _dep.PlantillaCobranza.Renderizar(notificacion));

                    return;
                }

                Console.WriteLine();
                Console.WriteLine("Enviando estados de cuenta...");
                resultados = await _dep.Correo.EnviarCobranza(cobranza.Notificaciones);
                _dep.Reporte.ImprimirEnvios(resultados, _dep.Settings.Smtp.ModoPrueba);

                if (_dep.Settings.Seguimiento.Habilitado)
                    await Registrar(cobranza, resultados);
            }
            catch (Exception ex)
            {
                errorFatal = ex.Message;
                Console.WriteLine($"ERROR: la ejecución no se completó: {ex.Message}");
                Environment.ExitCode = 1;
            }

            var rutaBitacora = await _dep.Bitacora.EscribirCobranza(
                cobranza, resultados, inicio, aplicarExclusion, errorFatal);

            Console.WriteLine();
            Console.WriteLine($"Bitácora: {rutaBitacora}");
        }

        /// <summary>
        /// Sin este registro la regla del viernes no existe: es lo que permite saber quién
        /// contestó el correo del martes.
        ///
        /// El correo del viernes se registra como Intento = 2 ligado al del martes, y marca aquél
        /// como RECORDADO. Ese orden importa: si el registro del segundo fallara después, el peor
        /// caso es un correo sin renglón propio —que la bitácora sí reporta— y no un cliente al
        /// que se le vuelve a escribir el mismo día.
        ///
        /// A diferencia de las facturas del día, aquí no se guardan las facturas del envío: la
        /// cobranza no reenvía adjuntos y el detalle cambia cada corrida conforme se paga.
        /// </summary>
        private async Task Registrar(ResultadoCobranza cobranza, IReadOnlyList<ResultadoEnvio> resultados)
        {
            var porCliente = cobranza.Notificaciones.ToDictionary(n => n.Cliente, StringComparer.OrdinalIgnoreCase);
            var registrados = 0;

            foreach (var resultado in resultados)
            {
                if (!porCliente.TryGetValue(resultado.Cliente, out var notificacion))
                    continue;

                if (string.IsNullOrWhiteSpace(resultado.MessageId))
                    continue;

                try
                {
                    if (notificacion.EnvioOriginal is { } original)
                        await _dep.SeguimientoDAO.MarcarRecordado(original.IdEnvio);

                    await _dep.SeguimientoDAO.Registrar(new EnvioNotificacion
                    {
                        Cliente = notificacion.Cliente,
                        RazonSocial = notificacion.RazonSocial,
                        Proceso = ProcesoEnvio.Cobranza,
                        MessageId = resultado.MessageId,
                        Token = resultado.Token ?? Guid.Empty,
                        IdEnvioOriginal = notificacion.EnvioOriginal?.IdEnvio,
                        Intento = (byte)(notificacion.EsRecordatorio ? 2 : 1),
                        Asunto = resultado.Asunto ?? string.Empty,
                        Destinatarios = string.Join(", ", resultado.Destinatarios),
                        ModoPrueba = _dep.Settings.Smtp.ModoPrueba,
                        FechaEnvio = DateTime.Now,
                        Estado = resultado.Enviado ? EstadoEnvio.Enviado : EstadoEnvio.Fallido,
                        Error = resultado.Error
                    });

                    registrados++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  AVISO: no se registró el envío a {resultado.Cliente} — {ex.Message}");
                    Environment.ExitCode = 1;
                }
            }

            Console.WriteLine();
            Console.WriteLine($"Seguimiento: {registrados} envíos de cobranza registrados.");
        }
    }
}
