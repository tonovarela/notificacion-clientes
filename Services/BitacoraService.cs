using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using notificacion_clientes.Configuracion;
using notificacion_clientes.Entity;

namespace notificacion_clientes.Services
{
    /// <summary>
    /// Deja en disco la evidencia de cada corrida: un archivo por ejecución, con la fecha y hora
    /// en el nombre para que no se pisen entre sí y queden ordenados cronológicamente.
    /// </summary>
    public class BitacoraService
    {
        /// <summary>Fecha y hora del nombre del archivo: envios-2026-08-11_09-30-45.log</summary>
        private const string FormatoNombreArchivo = "yyyy-MM-dd_HH-mm-ss";

        private const string FormatoFechaHora = "yyyy-MM-dd HH:mm:ss";

        /// <summary>Los importes se escriben siempre en es-MX para que la evidencia no dependa del equipo.</summary>
        private static readonly CultureInfo Cultura = CultureInfo.GetCultureInfo("es-MX");

        private static readonly string Separador = new('=', 79);

        private static readonly string SeparadorTenue = new('-', 79);

        private readonly string _directorio;
        private readonly SmtpSettings _smtp;

        public BitacoraService(string directorio, SmtpSettings smtp)
        {
            _directorio = directorio;
            _smtp = smtp;
        }

        /// <summary>Escribe la bitácora y devuelve la ruta del archivo generado.</summary>
        public async Task<string> Escribir(
            IReadOnlyList<NotificacionCliente> notificaciones,
            IReadOnlyList<ResultadoEnvio> resultados,
            DateTime inicio,
            string? errorFatal = null)
        {
            Directory.CreateDirectory(_directorio);

            var ruta = Path.Combine(_directorio, $"envios-{inicio.ToString(FormatoNombreArchivo, Cultura)}.log");
            var contenido = new StringBuilder();

            EscribirEncabezado(contenido, inicio, notificaciones, resultados, errorFatal);
            EscribirDetalle(contenido, notificaciones, resultados);

            // Sin BOM: el archivo se abre igual en Windows y en Linux.
            await File.WriteAllTextAsync(ruta, contenido.ToString(), new UTF8Encoding(false));

            return ruta;
        }

        /// <summary>
        /// Bitácora de la corrida de cartera por vendedor. Va a un archivo aparte
        /// (revision-vendedores-*.log) para que no se mezcle con la de facturas a clientes.
        /// </summary>
        public async Task<string> EscribirVendedores(
            IReadOnlyList<NotificacionVendedor> notificaciones,
            IReadOnlyList<ResultadoEnvio> resultados,
            DateTime inicio,
            string? errorFatal = null)
        {
            Directory.CreateDirectory(_directorio);

            var ruta = Path.Combine(_directorio, $"revision-vendedores-{inicio.ToString(FormatoNombreArchivo, Cultura)}.log");
            var contenido = new StringBuilder();
            var enviados = resultados.Count(r => r.Enviado);

            contenido.AppendLine(Separador);
            contenido.AppendLine(" BITACORA DE FACTURAS SIN INGRESAR A REVISION - AVISO A VENDEDORES");
            contenido.AppendLine(Separador);
            contenido.AppendLine($" Inicio      : {inicio.ToString(FormatoFechaHora, Cultura)}");
            contenido.AppendLine($" Fin         : {DateTime.Now.ToString(FormatoFechaHora, Cultura)}");
            contenido.AppendLine($" Equipo      : {Environment.MachineName}");
            contenido.AppendLine($" Remitente   : {_smtp.RemitenteNombre} <{_smtp.RemitenteEmail}>");
            contenido.AppendLine($" Servidor    : {_smtp.Host}:{_smtp.Puerto} ({(_smtp.UsarSsl ? "cifrado" : "sin cifrado")})");
            contenido.AppendLine(_smtp.ModoPruebaVendedores
                ? $" Modo prueba : SI - ningun correo llego al vendedor, se redirigio a {_smtp.CorreoPruebaVendedores}"
                : " Modo prueba : NO - los correos se enviaron a los vendedores reales");
            contenido.AppendLine($" Copia oculta: {DescribirCopiaOculta()}");
            contenido.AppendLine(SeparadorTenue);
            contenido.AppendLine(" RESUMEN");
            contenido.AppendLine($"   Vendedores por avisar  : {notificaciones.Count}");
            contenido.AppendLine($"   Correos enviados       : {enviados}");
            contenido.AppendLine($"   Correos fallidos       : {resultados.Count - enviados}");
            contenido.AppendLine($"   Direcciones con error  : {ContarDireccionesConProblema(resultados)}");
            contenido.AppendLine($"   Facturas pendientes    : {notificaciones.Sum(n => n.TotalFacturas)}");
            contenido.AppendLine($"   Saldo total            : {notificaciones.Sum(n => n.Saldo).ToString("C", Cultura)}");

            if (errorFatal is not null)
            {
                contenido.AppendLine(SeparadorTenue);
                contenido.AppendLine($" LA EJECUCION SE INTERRUMPIO: {errorFatal}");
            }

            contenido.AppendLine(Separador);
            EscribirDetalleVendedores(contenido, notificaciones, resultados);

            await File.WriteAllTextAsync(ruta, contenido.ToString(), new UTF8Encoding(false));

            return ruta;
        }

        /// <summary>
        /// Bitacora de la lectura del buzon: que envios se revisaron y cuales cambiaron de estado.
        /// Archivo aparte (respuestas-*.log) por el mismo motivo que los demas: son corridas
        /// distintas y mezclarlas complica la lectura.
        /// </summary>
        public async Task<string> EscribirRespuestas(
            ResultadoRespuestas resultado,
            DateTime inicio,
            string? errorFatal = null)
        {
            Directory.CreateDirectory(_directorio);

            var ruta = Path.Combine(_directorio, $"respuestas-{inicio.ToString(FormatoNombreArchivo, Cultura)}.log");
            var contenido = new StringBuilder();

            contenido.AppendLine(Separador);
            contenido.AppendLine(" BITACORA DE RESPUESTAS - LECTURA DEL BUZON");
            contenido.AppendLine(Separador);
            contenido.AppendLine($" Inicio      : {inicio.ToString(FormatoFechaHora, Cultura)}");
            contenido.AppendLine($" Fin         : {DateTime.Now.ToString(FormatoFechaHora, Cultura)}");
            contenido.AppendLine($" Equipo      : {Environment.MachineName}");
            contenido.AppendLine($" Buzon       : {_smtp.RemitenteNombre} <{_smtp.RemitenteEmail}>");
            contenido.AppendLine(" Correos     : NO - esta corrida solo lee el buzon y actualiza estados");
            contenido.AppendLine(SeparadorTenue);
            contenido.AppendLine(" RESUMEN");
            contenido.AppendLine($"   Envios revisados       : {resultado.Revisados}");
            contenido.AppendLine($"   Marcados CONTESTADO    : {resultado.Respuestas.Count}");
            contenido.AppendLine($"   Marcados FALLIDO       : {resultado.Rebotes.Count}");
            contenido.AppendLine($"   Entregas retrasadas    : {resultado.RebotesTemporales.Count} (no cambian de estado)");

            if (errorFatal is not null)
            {
                contenido.AppendLine(SeparadorTenue);
                contenido.AppendLine($" LA EJECUCION SE INTERRUMPIO: {errorFatal}");
            }

            contenido.AppendLine(Separador);

            EscribirAcuses(contenido, resultado);

            await File.WriteAllTextAsync(ruta, contenido.ToString(), new UTF8Encoding(false));

            return ruta;
        }

        private static void EscribirAcuses(StringBuilder contenido, ResultadoRespuestas resultado)
        {
            if (resultado.Respuestas.Count == 0 && resultado.Rebotes.Count == 0
                && resultado.RebotesTemporales.Count == 0)
            {
                contenido.AppendLine();
                contenido.AppendLine("No se detecto ninguna respuesta nueva en esta corrida.");
                return;
            }

            var consecutivo = 0;

            foreach (var respuesta in resultado.Respuestas)
            {
                consecutivo++;
                contenido.AppendLine();
                contenido.AppendLine($"[{consecutivo:D3}] CONTESTADO | Cliente {respuesta.Envio.Cliente} - {respuesta.Envio.RazonSocial}");
                contenido.AppendLine($"      Envio original : {respuesta.Envio.FechaEnvio.ToString(FormatoFechaHora, Cultura)} (intento {respuesta.Envio.Intento})");
                contenido.AppendLine($"      Respondio      : {respuesta.DeEmail} el {respuesta.Fecha.ToString(FormatoFechaHora, Cultura)}");
                contenido.AppendLine($"      Asunto         : {respuesta.Asunto}");
                contenido.AppendLine($"      Cruce          : {DescribirCriterio(respuesta.Criterio)}");
            }

            foreach (var rebote in resultado.Rebotes)
            {
                consecutivo++;
                contenido.AppendLine();
                contenido.AppendLine($"[{consecutivo:D3}] REBOTE | Cliente {rebote.Envio.Cliente} - {rebote.Envio.RazonSocial}");
                contenido.AppendLine($"      Envio original : {rebote.Envio.FechaEnvio.ToString(FormatoFechaHora, Cultura)}");
                contenido.AppendLine($"      Destinatarios  : {rebote.Envio.Destinatarios}");
                EscribirReporteDeRebote(contenido, rebote);
                contenido.AppendLine($"      Cruce          : {DescribirCriterio(rebote.Criterio)}");
                contenido.AppendLine(rebote.Rebote?.CulpaDeLaDireccion ?? true
                    ? "      AVISO          : la direccion no acepto el correo; hay que corregirla en el CRM"
                    : "      AVISO          : el servidor agoto los reintentos; la direccion puede estar bien");
            }

            // Van al final y sin marcar nada: son avisos de que el correo sigue en camino.
            foreach (var retraso in resultado.RebotesTemporales)
            {
                consecutivo++;
                contenido.AppendLine();
                contenido.AppendLine($"[{consecutivo:D3}] RETRASADO | Cliente {retraso.Envio.Cliente} - {retraso.Envio.RazonSocial}");
                contenido.AppendLine($"      Envio original : {retraso.Envio.FechaEnvio.ToString(FormatoFechaHora, Cultura)}");
                contenido.AppendLine($"      Destinatarios  : {retraso.Envio.Destinatarios}");
                EscribirReporteDeRebote(contenido, retraso);
                contenido.AppendLine($"      Cruce          : {DescribirCriterio(retraso.Criterio)}");
                contenido.AppendLine("      AVISO          : el servidor sigue intentando; el envio NO se cerro");
            }
        }

        /// <summary>
        /// Lo que dijo el servidor, cuando el aviso trajo el reporte del RFC 3464. Sin reporte
        /// solo queda el asunto del correo, que es lo unico que habia antes de leerlo.
        /// </summary>
        private static void EscribirReporteDeRebote(StringBuilder contenido, RespuestaDetectada rebote)
        {
            if (rebote.Rebote is not { } informe)
            {
                contenido.AppendLine($"      Motivo         : {rebote.Asunto}");
                contenido.AppendLine("      Reporte        : el aviso no traia delivery-status; se dio por definitivo");
                return;
            }

            contenido.AppendLine($"      Direccion      : {informe.Destinatario ?? "no identificada en el reporte"}");
            contenido.AppendLine($"      Codigo         : {informe.Estado ?? "sin codigo"}");

            if (!string.IsNullOrWhiteSpace(informe.Diagnostico))
                contenido.AppendLine($"      Diagnostico    : {informe.Diagnostico}");

            if (!string.IsNullOrWhiteSpace(informe.ServidorQueReporta))
                contenido.AppendLine($"      Reporta        : {informe.ServidorQueReporta}");
        }

        private static string DescribirCriterio(CriterioCruce criterio) => criterio switch
        {
            CriterioCruce.InReplyTo => "In-Reply-To",
            CriterioCruce.References => "References (cadena del hilo)",
            CriterioCruce.EnvelopeId => "Original-Envelope-Id (exacto)",
            CriterioCruce.DestinatarioDelRebote => "direccion del reporte (aproximado)",
            _ => "remitente + asunto (aproximado)"
        };

        /// <summary>
        /// Bitacora de la corrida de cobranza vencida. Archivo aparte (cobranza-*.log) por el
        /// mismo motivo que los demas: son corridas distintas y mezclarlas complica la lectura.
        /// </summary>
        public async Task<string> EscribirCobranza(
            ResultadoCobranza cobranza,
            IReadOnlyList<ResultadoEnvio> resultados,
            DateTime inicio,
            bool esRecordatorio,
            string? errorFatal = null,
            bool? avisoDeNoEntrega = null)
        {
            Directory.CreateDirectory(_directorio);

            var ruta = Path.Combine(_directorio, $"cobranza-{inicio.ToString(FormatoNombreArchivo, Cultura)}.log");
            var contenido = new StringBuilder();
            var enviados = resultados.Count(r => r.Enviado);

            contenido.AppendLine(Separador);
            contenido.AppendLine(" BITACORA DE COBRANZA VENCIDA - ESTADO DE CUENTA A CLIENTES");
            contenido.AppendLine(Separador);
            contenido.AppendLine($" Inicio      : {inicio.ToString(FormatoFechaHora, Cultura)}");
            contenido.AppendLine($" Fin         : {DateTime.Now.ToString(FormatoFechaHora, Cultura)}");
            contenido.AppendLine($" Equipo      : {Environment.MachineName}");
            contenido.AppendLine($" Remitente   : {_smtp.RemitenteNombre} <{_smtp.RemitenteEmail}>");
            contenido.AppendLine(_smtp.ModoPrueba
                ? $" Modo prueba : SI - ningun correo llego al cliente, se redirigio a {_smtp.CorreoPrueba}"
                : " Modo prueba : NO - los correos se enviaron a los contactos reales del cliente");
            contenido.AppendLine(esRecordatorio
                ? " Poblacion   : RECORDATORIO - facturas ya notificadas que siguen sin contestar"
                : " Poblacion   : PRIMER AVISO - facturas vencidas que no se habian notificado");
            contenido.AppendLine($" Copia oculta: {DescribirCopiaOculta()}");
            contenido.AppendLine($" Aviso rebote: {DescribirAvisoDeNoEntrega(avisoDeNoEntrega)}");
            contenido.AppendLine(SeparadorTenue);
            contenido.AppendLine(" RESUMEN");
            contenido.AppendLine($"   Clientes por notificar : {cobranza.Notificaciones.Count}");
            contenido.AppendLine($"   Correos enviados       : {enviados}");
            contenido.AppendLine($"   Correos fallidos       : {resultados.Count - enviados}");
            contenido.AppendLine($"   Direcciones con error  : {ContarDireccionesConProblema(resultados)}");

            foreach (var saldo in TotalizarPorMoneda(cobranza.Notificaciones))
                contenido.AppendLine($"   Saldo vencido {saldo.Moneda,-10}: {FormatearImporte(saldo.Total, saldo.Moneda)}");

            if (errorFatal is not null)
            {
                contenido.AppendLine(SeparadorTenue);
                contenido.AppendLine($" LA EJECUCION SE INTERRUMPIO: {errorFatal}");
            }

            contenido.AppendLine(Separador);

            EscribirDetalleCobranza(contenido, cobranza, resultados);

            await File.WriteAllTextAsync(ruta, contenido.ToString(), new UTF8Encoding(false));

            return ruta;
        }

        private static void EscribirDetalleCobranza(
            StringBuilder contenido,
            ResultadoCobranza cobranza,
            IReadOnlyList<ResultadoEnvio> resultados)
        {
            if (cobranza.Notificaciones.Count == 0)
            {
                contenido.AppendLine();
                contenido.AppendLine("No habia clientes por notificar en esta corrida.");
                return;
            }

            var porCliente = resultados.ToDictionary(r => r.Cliente, StringComparer.OrdinalIgnoreCase);
            var consecutivo = 0;

            foreach (var notificacion in cobranza.Notificaciones)
            {
                consecutivo++;
                porCliente.TryGetValue(notificacion.Cliente, out var resultado);

                var estado = resultado is null ? "SIN ENVIAR" : resultado.Enviado ? "ENVIADO" : "FALLO";

                contenido.AppendLine();
                contenido.AppendLine($"[{consecutivo:D3}] {estado} | Cliente {notificacion.Cliente} - {notificacion.RazonSocial}");

                if (resultado is null)
                    contenido.AppendLine("      Motivo        : el proceso no llego a enviar este correo");
                else if (resultado.Enviado)
                    contenido.AppendLine($"      Destinatarios : {string.Join(", ", resultado.Destinatarios)}");
                else
                    contenido.AppendLine($"      Error         : {resultado.Error}");

                if (resultado is not null && resultado.CopiaOculta.Count > 0)
                    contenido.AppendLine($"      Copia oculta  : {string.Join(", ", resultado.CopiaOculta)}");

                EscribirDireccionesConProblema(contenido, resultado);

                contenido.AppendLine($"      Agente        : {notificacion.Agente ?? "sin agente en el CRM"}");
                contenido.AppendLine($"      Saldo vencido : {DescribirSaldos(notificacion)}" +
                                     $" | {notificacion.TotalFacturas} facturas" +
                                     $" | Maxima {notificacion.DiasVencidoMaximo} dias vencida");

                foreach (var factura in notificacion.Facturas)
                    contenido.AppendLine($"        - {factura.Factura} | Vence {factura.Vencimiento:dd/MM/yyyy}" +
                                         $" | {factura.DiasVencido} dias" +
                                         $" | {FormatearImporte(factura.TotalVencido, factura.Moneda)}");
            }
        }

        private static string DescribirSaldos(NotificacionCobranza notificacion) =>
            string.Join(" + ", notificacion.Saldos.Select(s => FormatearImporte(s.Total, s.Moneda)));

        /// <summary>Los saldos no se suman entre monedas: cada una lleva su propio total.</summary>
        private static IEnumerable<SaldoPorMoneda> TotalizarPorMoneda(IEnumerable<NotificacionCobranza> notificaciones) =>
            notificaciones
                .SelectMany(n => n.Facturas)
                .GroupBy(f => f.Moneda, StringComparer.OrdinalIgnoreCase)
                .Select(g => new SaldoPorMoneda { Moneda = g.Key, Total = g.Sum(f => f.TotalVencido) })
                .OrderByDescending(s => s.Total);

        private static string FormatearImporte(decimal importe, string moneda) =>
            moneda.StartsWith("Dol", StringComparison.OrdinalIgnoreCase) || moneda.Equals("USD", StringComparison.OrdinalIgnoreCase)
                ? $"{importe.ToString("C", CultureInfo.GetCultureInfo("en-US"))} USD"
                : importe.ToString("C", Cultura);


        private static void EscribirDetalleVendedores(
            StringBuilder contenido,
            IReadOnlyList<NotificacionVendedor> notificaciones,
            IReadOnlyList<ResultadoEnvio> resultados)
        {
            if (notificaciones.Count == 0)
            {
                contenido.AppendLine();
                contenido.AppendLine("No habia facturas sin ingresar a revision en esta corrida.");
                return;
            }

            // La llave del resultado es el correo del vendedor: es lo unico unico por grupo.
            var porVendedor = resultados.ToDictionary(r => r.Cliente, StringComparer.OrdinalIgnoreCase);
            var consecutivo = 0;

            foreach (var notificacion in notificaciones)
            {
                consecutivo++;
                porVendedor.TryGetValue(notificacion.Email, out var resultado);

                var estado = resultado is null ? "SIN ENVIAR" : resultado.Enviado ? "ENVIADO" : "FALLO";

                contenido.AppendLine();
                contenido.AppendLine($"[{consecutivo:D3}] {estado} | {notificacion.Vendedor} <{notificacion.Email}>");

                if (resultado is null)
                    contenido.AppendLine("      Motivo        : el proceso no llego a enviar este correo");
                else if (resultado.Enviado)
                    contenido.AppendLine($"      Destinatarios : {string.Join(", ", resultado.Destinatarios)}");
                else
                    contenido.AppendLine($"      Error         : {resultado.Error}");

                if (resultado is not null && resultado.CopiaOculta.Count > 0)
                    contenido.AppendLine($"      Copia oculta  : {string.Join(", ", resultado.CopiaOculta)}");

                EscribirDireccionesConProblema(contenido, resultado);

                contenido.AppendLine($"      Cartera       : {notificacion.Clientes.Count} clientes" +
                                     $" | {notificacion.TotalFacturas} facturas" +
                                     $" | Saldo {notificacion.Saldo.ToString("C", Cultura)}" +
                                     $" | Maxima {notificacion.DiasVencidoMaximo} dias vencida");

                if (notificacion.SinAgenteValido)
                    contenido.AppendLine("      AVISO         : facturas sin agente asignado en el CRM, se enviaron a cobranza");

                foreach (var cliente in notificacion.Clientes)
                {
                    contenido.AppendLine($"        Cliente {cliente.Cliente} - {cliente.RazonSocial}" +
                                         $" | {cliente.Facturas.Count} facturas | {cliente.Saldo.ToString("C", Cultura)}");

                    foreach (var factura in cliente.Facturas)
                        contenido.AppendLine($"          - {factura.Factura} | Emitida {factura.FechaEmision:dd/MM/yyyy}" +
                                             $" | Vence {factura.Vencimiento:dd/MM/yyyy}" +
                                             $" | {ClienteCartera.CalcularDiasVencido(factura)} dias" +
                                             $" | Saldo {factura.Saldo.ToString("C", Cultura)}");
                }
            }
        }

        private void EscribirEncabezado(
            StringBuilder contenido,
            DateTime inicio,
            IReadOnlyList<NotificacionCliente> notificaciones,
            IReadOnlyList<ResultadoEnvio> resultados,
            string? errorFatal)
        {
            var enviados = resultados.Count(r => r.Enviado);
            var fallidos = resultados.Count - enviados;

            contenido.AppendLine(Separador);
            contenido.AppendLine(" BITACORA DE NOTIFICACION DE FACTURAS A CLIENTES");
            contenido.AppendLine(Separador);
            contenido.AppendLine($" Inicio      : {inicio.ToString(FormatoFechaHora, Cultura)}");
            contenido.AppendLine($" Fin         : {DateTime.Now.ToString(FormatoFechaHora, Cultura)}");
            contenido.AppendLine($" Equipo      : {Environment.MachineName}");
            contenido.AppendLine($" Remitente   : {_smtp.RemitenteNombre} <{_smtp.RemitenteEmail}>");
            contenido.AppendLine($" Servidor    : {_smtp.Host}:{_smtp.Puerto} ({(_smtp.UsarSsl ? "cifrado" : "sin cifrado")})");
            contenido.AppendLine(_smtp.ModoPrueba
                ? $" Modo prueba : SI - ningun correo llego al cliente, se redirigio a {_smtp.CorreoPrueba}"
                : " Modo prueba : NO - los correos se enviaron a los contactos reales del cliente");
            contenido.AppendLine($" Copia oculta: {DescribirCopiaOculta()}");
            contenido.AppendLine(SeparadorTenue);
            contenido.AppendLine(" RESUMEN");
            contenido.AppendLine($"   Clientes por notificar : {notificaciones.Count}");
            contenido.AppendLine($"   Correos enviados       : {enviados}");
            contenido.AppendLine($"   Correos fallidos       : {fallidos}");
            contenido.AppendLine($"   Direcciones con error  : {ContarDireccionesConProblema(resultados)}");
            contenido.AppendLine($"   Facturas incluidas     : {notificaciones.Sum(n => n.Documentos.Count)}");
            contenido.AppendLine($"   Importe total          : {notificaciones.Sum(n => n.Total).ToString("C", Cultura)}");

            if (errorFatal is not null)
            {
                contenido.AppendLine(SeparadorTenue);
                contenido.AppendLine($" LA EJECUCION SE INTERRUMPIO: {errorFatal}");
            }

            contenido.AppendLine(Separador);
        }

        private static void EscribirDetalle(
            StringBuilder contenido,
            IReadOnlyList<NotificacionCliente> notificaciones,
            IReadOnlyList<ResultadoEnvio> resultados)
        {
            if (notificaciones.Count == 0)
            {
                contenido.AppendLine();
                contenido.AppendLine("No habia clientes por notificar en esta corrida.");
                return;
            }

            var porCliente = resultados.ToDictionary(r => r.Cliente);
            var consecutivo = 0;

            foreach (var notificacion in notificaciones)
            {
                consecutivo++;
                porCliente.TryGetValue(notificacion.Cliente, out var resultado);

                var estado = resultado is null ? "SIN ENVIAR" : resultado.Enviado ? "ENVIADO" : "FALLO";

                contenido.AppendLine();
                contenido.AppendLine($"[{consecutivo:D3}] {estado} | Cliente {notificacion.Cliente} - {notificacion.RazonSocial}");

                if (resultado is null)
                    contenido.AppendLine("      Motivo        : el proceso no llego a enviar este correo");
                else if (resultado.Enviado)
                    contenido.AppendLine($"      Destinatarios : {string.Join(", ", resultado.Destinatarios)}");
                else
                    contenido.AppendLine($"      Error         : {resultado.Error}");

                if (resultado is not null && resultado.CopiaOculta.Count > 0)
                    contenido.AppendLine($"      Copia oculta  : {string.Join(", ", resultado.CopiaOculta)}");

                EscribirDireccionesConProblema(contenido, resultado);

                contenido.AppendLine($"      Contactos CRM : {DescribirContactos(notificacion)}");
                contenido.AppendLine($"      Importes      : Subtotal {notificacion.SubTotal.ToString("C", Cultura)}" +
                                     $" | IVA {notificacion.Iva.ToString("C", Cultura)}" +
                                     $" | Total {notificacion.Total.ToString("C", Cultura)}");

                if (notificacion.TieneImportesEstimados)
                    contenido.AppendLine("      AVISO         : alguna factura no pudo leerse del XML; su IVA quedo en cero");

                contenido.AppendLine($"      Facturas ({notificacion.Documentos.Count}):");

                foreach (var documento in notificacion.Documentos)
                {
                    contenido.AppendLine($"        - {documento.MovID} | Periodo {documento.Periodo}/{documento.Ejercicio}" +
                                         $" | Total {documento.Total.ToString("C", Cultura)} {documento.Moneda}");
                    contenido.AppendLine($"            XML: {Describir(documento.Xml)}");
                    contenido.AppendLine($"            PDF: {Describir(documento.Pdf)}");
                }
            }
        }

        /// <summary>
        /// Las direcciones que no recibieron el correo, aunque el envio figure como ENVIADO.
        /// Es lo unico de la bitacora que exige que alguien haga algo: corregir el dato en el CRM.
        /// </summary>
        private static void EscribirDireccionesConProblema(StringBuilder contenido, ResultadoEnvio? resultado)
        {
            if (resultado is null)
                return;

            if (!string.IsNullOrWhiteSpace(resultado.AcuseServidor))
                contenido.AppendLine($"      Acuse servidor: {resultado.AcuseServidor}");

            foreach (var invalida in resultado.DireccionesInvalidas)
                contenido.AppendLine($"      INVALIDA      : '{invalida}' no es una direccion de correo; se omitio - corregir en el CRM");

            foreach (var rechazado in resultado.Rechazados)
                contenido.AppendLine($"      RECHAZADA     : {rechazado.Email} - el servidor respondio {rechazado.Codigo} {rechazado.Respuesta}"
                                     + (rechazado.EsDefinitivo ? " - corregir en el CRM" : " - rechazo temporal"));
        }

        /// <summary>Cuantas direcciones se quedaron sin recibir el correo en toda la corrida.</summary>
        private static int ContarDireccionesConProblema(IEnumerable<ResultadoEnvio> resultados) =>
            resultados.Sum(r => r.DireccionesInvalidas.Count + r.Rechazados.Count);

        /// <summary>
        /// Si al servidor se le pudo pedir que avise cuando un correo no se entregue. Queda en la
        /// bitacora porque explica de antemano por que un rebote podria no reconocerse despues.
        /// </summary>
        private static string DescribirAvisoDeNoEntrega(bool? soportado) => soportado switch
        {
            true => "SI - se pidio al servidor aviso de no entrega y se sello el sobre con el token",
            false => "NO - el servidor no soporta DSN; un rebote habra que casarlo por hilo o direccion",
            _ => "no aplica - no se llego a conectar con el servidor"
        };

        private string DescribirCopiaOculta() =>
            _smtp.CopiaOculta.Count == 0
                ? "ninguna configurada"
                : $"{string.Join(", ", _smtp.CopiaOculta)} (recibe copia en ambos modos)";

        private static string DescribirContactos(NotificacionCliente notificacion) =>
            notificacion.Contactos.Count == 0
                ? "ninguno"
                : string.Join(", ", notificacion.Contactos.Select(c => $"{c.Nombre} <{c.Email}>"));

        private static string Describir(ArchivoFactura? archivo) =>
            archivo is null ? "no disponible" : $"{archivo.NombreArchivo} ({archivo.Tamanio} bytes)";
    }
}
