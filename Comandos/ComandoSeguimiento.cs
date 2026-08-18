using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using notificacion_clientes.Entity;
using notificacion_clientes.Services;

namespace notificacion_clientes.Comandos
{
    /// <summary>
    /// La corrida de seguimiento: revisa el buzón, cierra los envíos que ya tienen acuse, y da
    /// de baja los de cobranza que agotaron su vigencia sin respuesta.
    ///
    /// Ese cierre no es cosmético: la conciliación busca en el buzón desde el pendiente más
    /// viejo, así que un envío que nunca se cierra ancla esa ventana para siempre y la búsqueda
    /// IMAP crece sin límite.
    ///
    /// No manda recordatorios. El recordatorio de cobranza es el correo del viernes, que sale
    /// por --cobranza dentro del hilo del martes.
    /// </summary>
    public class ComandoSeguimiento
    {
        private readonly Dependencias _dep;

        public ComandoSeguimiento(Dependencias dep)
        {
            _dep = dep;
        }

        public async Task Ejecutar(DateTime inicio, bool cerrarVencidos)
        {
            if (!_dep.Settings.Seguimiento.Habilitado)
            {
                Console.WriteLine("El seguimiento está deshabilitado (Seguimiento:Habilitado = false). No se hizo nada.");
                return;
            }

            var respuestas = new List<RespuestaDetectada>();
            var rebotes = new List<RespuestaDetectada>();
            var cerrados = (IReadOnlyList<EnvioNotificacion>)Array.Empty<EnvioNotificacion>();
            var conciliados = 0;
            string? errorFatal = null;

            try
            {
                var detectadas = await Conciliar();
                conciliados = detectadas.Revisados;
                respuestas.AddRange(detectadas.Respuestas);
                rebotes.AddRange(detectadas.Rebotes);

                // El cierre va después de conciliar: un envío que hoy recibe su respuesta no
                // debe cerrarse como si nadie hubiera contestado.
                if (cerrarVencidos)
                    cerrados = await CerrarVencidos();
            }
            catch (Exception ex)
            {
                errorFatal = ex.Message;
                Console.WriteLine($"ERROR: la ejecución no se completó: {ex.Message}");
                Environment.ExitCode = 1;
            }

            var resultado = new ResultadoSeguimiento
            {
                Respuestas = respuestas,
                Rebotes = rebotes,
                Cerrados = cerrados,
                Conciliados = conciliados
            };

            _dep.Reporte.ImprimirSeguimiento(resultado);

            var rutaBitacora = await _dep.Bitacora.EscribirSeguimiento(resultado, inicio, errorFatal);
            Console.WriteLine();
            Console.WriteLine($"Bitácora: {rutaBitacora}");
        }

        /// <summary>
        /// Da de baja los envíos de cobranza que ya agotaron su vigencia. Un correo de cobranza
        /// deja de esperar respuesta cuando ya salió el siguiente.
        /// </summary>
        private async Task<IReadOnlyList<EnvioNotificacion>> CerrarVencidos()
        {
            var fechaCorte = DateTime.Today.AddDays(-_dep.Settings.Seguimiento.DiasVigencia);
            var porCerrar = await _dep.SeguimientoDAO.ObtenerPendientesDeCierre(fechaCorte);

            foreach (var envio in porCerrar)
                await _dep.SeguimientoDAO.MarcarSinRespuesta(envio.IdEnvio);

            return porCerrar;
        }

        /// <summary>
        /// Cruza el buzón contra los envíos pendientes y aplica lo encontrado: acuse cierra el
        /// envío, rebote lo marca fallido para que cobranza corrija la dirección en el CRM.
        /// </summary>
        private async Task<Conciliacion> Conciliar()
        {
            // Sin nada pendiente no vale la pena ni abrir la conexión al buzón.
            var desde = await _dep.SeguimientoDAO.ObtenerFechaPendienteMasAntiguo();

            if (desde is null)
            {
                Console.WriteLine("No hay envíos pendientes de respuesta.");
                return new Conciliacion();
            }

            // El mismo tope se aplica a la consulta: no tiene caso traer pendientes cuya respuesta
            // ya no se va a buscar en el buzón.
            var tope = DateTime.Today.AddDays(-_dep.Settings.Seguimiento.DiasVentanaMaxima);
            var ventana = desde.Value < tope ? tope : desde.Value;

            if (ventana > desde.Value)
                Console.WriteLine($"AVISO: hay pendientes desde {desde:dd/MM/yyyy}, más viejos que la ventana de " +
                                  $"{_dep.Settings.Seguimiento.DiasVentanaMaxima} días. Se revisa sólo desde {ventana:dd/MM/yyyy}.");

            Console.WriteLine($"Revisando respuestas desde {ventana:dd/MM/yyyy}...");

            var pendientes = await _dep.SeguimientoDAO.ObtenerParaConciliar(ventana);
            var detectadas = await _dep.Respuesta.Conciliar(pendientes, _dep.Settings.Seguimiento.DiasVentanaMaxima);

            var respuestas = detectadas.Where(d => !d.EsRebote).ToList();
            var rebotes = detectadas.Where(d => d.EsRebote).ToList();

            foreach (var respuesta in respuestas)
                await _dep.SeguimientoDAO.MarcarContestado(respuesta);

            foreach (var rebote in rebotes)
                await _dep.SeguimientoDAO.MarcarFallidoPorRebote(rebote);

            return new Conciliacion
            {
                Revisados = pendientes.Count,
                Respuestas = respuestas,
                Rebotes = rebotes
            };
        }

        private class Conciliacion
        {
            public int Revisados { get; init; }

            public IReadOnlyList<RespuestaDetectada> Respuestas { get; init; } = Array.Empty<RespuestaDetectada>();

            public IReadOnlyList<RespuestaDetectada> Rebotes { get; init; } = Array.Empty<RespuestaDetectada>();
        }
    }
}
