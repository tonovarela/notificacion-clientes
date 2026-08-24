using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using notificacion_clientes.Entity;
using notificacion_clientes.Services;

namespace notificacion_clientes.Comandos
{
    /// <summary>
    /// Lee el buzón y le pone estado a los envíos: CONTESTADO al que el cliente respondió,
    /// FALLIDO al que rebotó de forma definitiva. No manda ningún correo y no cierra nada por
    /// vigencia. Los avisos de entrega retrasada se reportan pero no cambian ningún estado.
    ///
    /// Es la pieza que hace útil al recordatorio de cobranza. La consulta del viernes descarta
    /// los envíos en CONTESTADO, pero ese estado no se pone solo: sin esta corrida, a un cliente
    /// que ya respondió se le sigue insistiendo hasta que la factura se pague.
    /// </summary>
    public class ComandoRespuestas
    {
        private readonly Dependencias _dep;

        public ComandoRespuestas(Dependencias dep)
        {
            _dep = dep;
        }

        public async Task Ejecutar(DateTime inicio)
        {
            if (!_dep.Settings.Seguimiento.Habilitado)
            {
                Console.WriteLine("El seguimiento está deshabilitado (Seguimiento:Habilitado = false). No se hizo nada.");
                return;
            }

            var resultado = new ResultadoRespuestas();
            string? errorFatal = null;

            try
            {
                resultado = await Conciliar();

                // El piso sólo avanza cuando la corrida completa terminó bien. Si el buzón no se
                // pudo leer, se queda donde estaba y la siguiente corrida cubre este hueco.
                await _dep.SeguimientoDAO.RegistrarConciliacion(inicio);
            }
            catch (Exception ex)
            {
                errorFatal = ex.Message;
                Console.WriteLine($"ERROR: la ejecución no se completó: {ex.Message}");
                Environment.ExitCode = 1;
            }

            _dep.Reporte.ImprimirRespuestas(resultado);

            var rutaBitacora = await _dep.Bitacora.EscribirRespuestas(resultado, inicio, errorFatal);
            Console.WriteLine();
            Console.WriteLine($"Bitácora: {rutaBitacora}");
        }

        /// <summary>
        /// Cruza el buzón contra los envíos abiertos y aplica lo encontrado.
        ///
        /// El estado se escribe envío por envío y no en bloque: si uno falla, los ya marcados
        /// quedan marcados. Volver a correr no hace daño —el correo ya conciliado deja de
        /// aparecer porque su envío salió de la lista de abiertos—, así que reintentar es seguro.
        /// </summary>
        private async Task<ResultadoRespuestas> Conciliar()
        {
            // El mismo tope que usa la búsqueda IMAP: no tiene caso traer envíos cuya respuesta
            // ya no se va a buscar en el buzón. El DAO lo compara contra el último recordatorio,
            // no contra el primer aviso, para no perder al moroso al que se le sigue insistiendo.
            var ventana = DateTime.Today.AddDays(-_dep.Settings.Seguimiento.DiasVentanaMaxima);

            var abiertos = await _dep.SeguimientoDAO.ObtenerEnviosSinRespuesta(ventana);

            if (abiertos.Count == 0)
            {
                // Sin nada pendiente no vale la pena ni abrir la conexión al buzón.
                Console.WriteLine("No hay envíos esperando respuesta.");
                return new ResultadoRespuestas();
            }

            Console.WriteLine($"Revisando el buzón desde {ventana:dd/MM/yyyy} contra {abiertos.Count} envíos abiertos...");

            var ultimaConciliacion = await _dep.SeguimientoDAO.ObtenerUltimaConciliacion();

            var detectadas = await _dep.Respuesta.Conciliar(
                abiertos,
                _dep.Settings.Seguimiento.DiasVentanaMaxima,
                ultimaConciliacion);

            var respuestas = detectadas.Where(d => !d.EsRebote).ToList();

            // Un rebote sólo cierra el envío si el servidor ya se rindió. Mientras siga
            // reintentando el correo puede entregarse solo, y marcarlo FALLIDO sacaría al cliente
            // del recordatorio por una dirección que en realidad sí sirve.
            var rebotes = detectadas.Where(d => d.CierraElEnvio).ToList();
            var temporales = detectadas.Where(d => d.EsRebote && !d.CierraElEnvio).ToList();

            foreach (var respuesta in respuestas)
                await _dep.SeguimientoDAO.MarcarContestado(respuesta);

            foreach (var rebote in rebotes)
                await _dep.SeguimientoDAO.MarcarFallidoPorRebote(rebote);

            return new ResultadoRespuestas
            {
                Revisados = abiertos.Count,
                Respuestas = respuestas,
                Rebotes = rebotes,
                RebotesTemporales = temporales
            };
        }
    }
}
