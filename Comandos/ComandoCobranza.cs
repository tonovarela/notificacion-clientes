using notificacion_clientes.Entity;
using notificacion_clientes.Services;

namespace notificacion_clientes.Comandos
{
    /// <summary>
    /// El estado de cuenta vencido a cada cliente. Sale martes y viernes a las 09:00.
    ///
    /// Los dos días mandan poblaciones distintas: el martes, facturas que nunca se han notificado;
    /// el viernes, las ya notificadas que siguen sin respuesta. La consulta hace ese corte, así que
    /// insistirle a alguien que ya contestó no depende de ninguna regla de este comando.
    /// </summary>
    public class ComandoCobranza
    {
        private readonly Dependencias _dep;

        public ComandoCobranza(Dependencias dep)
        {
            _dep = dep;
        }

        public async Task Ejecutar(DateTime inicio, bool previsualizar, bool? forzarRecordatorio)
        {
            // El día manda, pero se decide aquí y no dentro del servicio para que una corrida
            // manual sea predecible: --recordatorio y --primer-aviso la fuerzan.
            var esRecordatorio = forzarRecordatorio ?? (inicio.DayOfWeek == DayOfWeek.Friday);

            var cobranza = new ResultadoCobranza { Notificaciones = Array.Empty<NotificacionCobranza>() };
            IReadOnlyList<ResultadoEnvio> resultados = Array.Empty<ResultadoEnvio>();
            string? errorFatal = null;

            try
            {
                Console.WriteLine(esRecordatorio
                    ? "Obteniendo cobranza vencida ya notificada y sin contestar..."
                    : "Obteniendo cobranza vencida no notificada...");

                if (esRecordatorio && !_dep.Settings.Seguimiento.Habilitado)
                {
                    // La consulta del recordatorio se apoya en lo que Registrar dejó escrito. Con el
                    // seguimiento apagado no se registra nada, así que esa población queda vacía y
                    // el correo del viernes no le llegaría a nadie sin que se note por qué.
                    Console.WriteLine("AVISO: el recordatorio se arma con los envíos ya registrados, pero el");
                    Console.WriteLine("       seguimiento está deshabilitado (Seguimiento:Habilitado = false).");
                    Console.WriteLine("       No hay registro de qué se notificó, así que no saldrá ningún correo.");
                }

                cobranza = await _dep.CobranzaVencida.Preparar(esRecordatorio);
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

                // El recordatorio no deja registro. Su población sale de las facturas que el
                // primer aviso ya escribió, así que anotarlo otra vez sólo duplicaría renglones
                // de EnvioFactura por el mismo MovID —y partiría el estado en dos envíos, de los
                // cuales marcar uno como CONTESTADO no bastaría para dejar de insistir—.
                if (_dep.Settings.Seguimiento.Habilitado && !esRecordatorio)
                    await Registrar(cobranza, resultados);
                else if (_dep.Settings.Seguimiento.Habilitado)
                    await SellarRecordatorio(cobranza, resultados);
            }
            catch (Exception ex)
            {
                errorFatal = ex.Message;
                Console.WriteLine($"ERROR: la ejecución no se completó: {ex.Message}");
                Environment.ExitCode = 1;
            }

            var rutaBitacora = await _dep.Bitacora.EscribirCobranza(
                cobranza, resultados, inicio, esRecordatorio, errorFatal,
                _dep.Correo.ServidorAvisaNoEntrega);

            Console.WriteLine();
            Console.WriteLine($"Bitácora: {rutaBitacora}");
        }

        /// <summary>
        /// Sin este registro el recordatorio no existe: la consulta del viernes se arma cruzando
        /// la antigüedad de saldos contra estas dos tablas, así que una factura que no quede
        /// escrita aquí se seguirá viendo como nunca notificada.
        ///
        /// Aquí sólo llega el primer aviso: el recordatorio no se registra. Por eso todo lo que
        /// se escribe lleva Intento = 1 e IdEnvioOriginal nulo, y por eso una factura tiene a lo
        /// más un renglón en EnvioFactura por más veces que se le insista.
        ///
        /// Junto con el envío se guardan las facturas que se le estaban reclamando. Eso es lo que
        /// convierte el registro en la fuente del recordatorio: la consulta del viernes cruza la
        /// antigüedad de saldos contra estos renglones, así que una factura que no quede escrita
        /// aquí se seguirá viendo como nunca notificada.
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
                    await _dep.SeguimientoDAO.Registrar(new EnvioNotificacion
                    {
                        Cliente = notificacion.Cliente,
                        RazonSocial = notificacion.RazonSocial,
                        Proceso = ProcesoEnvio.Cobranza,
                        MessageId = resultado.MessageId,
                        Token = resultado.Token ?? Guid.Empty,
                        IdEnvioOriginal = null,
                        // Siempre 1: aquí sólo llega el primer aviso.
                        Intento = 1,
                        Asunto = resultado.Asunto ?? string.Empty,
                        Destinatarios = string.Join(", ", resultado.Destinatarios),
                        ModoPrueba = _dep.Settings.Smtp.ModoPrueba,
                        FechaEnvio = DateTime.Now,
                        Estado = resultado.Enviado ? EstadoEnvio.Enviado : EstadoEnvio.Fallido,
                        // No es sólo el error del envío: un correo que salió bien puede traer
                        // contactos que el servidor rechazó o que ni siquiera son direcciones.
                        // El renglón queda ENVIADO con la nota, que es lo que revisa cobranza
                        // para saber a quién hay que corregirle el dato en el CRM.
                        Error = resultado.Incidencias,
                        Facturas = Detallar(notificacion)
                    });

                    registrados++;

                    if (resultado.Enviado && resultado.TieneDireccionesConProblema)
                        Console.WriteLine($"  AVISO: a {resultado.Cliente} le salió el correo, pero no a todos sus contactos — {resultado.Incidencias}");
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

        /// <summary>
        /// El recordatorio no genera renglón —duplicaría MovIDs en EnvioFactura—, pero sí deja su
        /// Message-Id estampado sobre los envíos que cubrió.
        ///
        /// Sin eso, la respuesta del cliente a ese correo no casa con nada: su In-Reply-To apunta a
        /// un id que no está en ninguna parte, y el asunto tampoco sirve porque lleva el prefijo
        /// "Recordatorio:". El cliente contestaría y se le seguiría insistiendo cada viernes.
        ///
        /// Se estampa por MovID y no por cliente: un recordatorio puede juntar facturas de varias
        /// semanas —y por tanto de varios envíos—, y todos tienen que quedar con el mismo id para
        /// que una sola respuesta los cierre de golpe.
        /// </summary>
        private async Task SellarRecordatorio(ResultadoCobranza cobranza, IReadOnlyList<ResultadoEnvio> resultados)
        {
            var porCliente = cobranza.Notificaciones.ToDictionary(n => n.Cliente, StringComparer.OrdinalIgnoreCase);
            var sellados = 0;

            foreach (var resultado in resultados)
            {
                if (!resultado.Enviado || string.IsNullOrWhiteSpace(resultado.MessageId))
                    continue;

                if (!porCliente.TryGetValue(resultado.Cliente, out var notificacion))
                    continue;

                var movIds = notificacion.Facturas.Select(f => f.MovID).ToList();

                try
                {
                    sellados += await _dep.SeguimientoDAO.MarcarRecordatorioEnviado(
                        movIds, resultado.MessageId, DateTime.Now);
                }
                catch (Exception ex)
                {
                    // Se avisa fuerte: el correo ya salió, y sin el sello su respuesta será invisible.
                    Console.WriteLine($"  AVISO: no se selló el recordatorio de {resultado.Cliente} — {ex.Message}");
                    Console.WriteLine( "         Si el cliente contesta ese correo, no se detectará.");
                    Environment.ExitCode = 1;
                }
            }

            Console.WriteLine();
            Console.WriteLine($"Seguimiento: sin registro nuevo; {sellados} envíos quedaron ligados al recordatorio.");
        }

        /// <summary>
        /// Las facturas del correo, como renglones de EnvioFactura.
        ///
        /// El importe que se guarda es el vencido, no el total del documento: es la cifra que el
        /// cliente leyó. Un abono parcial baja el vencido sin cambiar el total, y guardar el total
        /// haría que la tabla contradijera al correo que sí se mandó.
        ///
        /// El servicio ya dejó una factura por MovID —la consulta las repite una vez por
        /// contacto—, que es justo lo que exige la llave primaria (IdEnvio, MovID). Y como el
        /// recordatorio no registra, tampoco hay un segundo envío que repita el mismo MovID.
        /// </summary>
        private static IReadOnlyList<FacturaEnviada> Detallar(NotificacionCobranza notificacion) =>
            notificacion.Facturas
                .Select(f => new FacturaEnviada
                {
                    MovID = f.MovID,
                    Total = f.TotalVencido,
                    Moneda = Monedas.Codigo(f.Moneda)
                })
                .ToList();
    }
}
