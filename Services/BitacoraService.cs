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
