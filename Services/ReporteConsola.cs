using System;
using System.Collections.Generic;
using notificacion_clientes.Entity;

namespace notificacion_clientes.Services
{
    /// <summary>Única responsable de cómo se ve el resultado en pantalla.</summary>
    public class ReporteConsola
    {
        public void Imprimir(IReadOnlyList<NotificacionCliente> notificaciones)
        {
            Console.WriteLine($"Se encontraron {notificaciones.Count} clientes por notificar.");

            foreach (var notificacion in notificaciones)
            {
                Console.WriteLine();
                Console.WriteLine($"Cliente: {notificacion.Cliente} - {notificacion.RazonSocial} | Facturas: {notificacion.Documentos.Count}");
                Console.WriteLine($"  Subtotal: {notificacion.SubTotal:C} | IVA: {notificacion.Iva:C} | Total: {notificacion.Total:C}");

                if (notificacion.TieneImportesEstimados)
                    Console.WriteLine("  AVISO: alguna factura no pudo leerse del XML; su IVA quedó en cero.");

                foreach (var contacto in notificacion.Contactos)
                    Console.WriteLine($"  Contacto: {contacto.Nombre} ({contacto.Cargo}) <{contacto.Email}>");

                foreach (var documento in notificacion.Documentos)
                {
                    Console.WriteLine($"  {documento.MovID} | Periodo {documento.Periodo}/{documento.Ejercicio} | Subtotal {documento.SubTotal:C} + IVA {documento.Iva:C} = {documento.Total:C}");
                    Console.WriteLine($"    XML: {Describir(documento.Xml)}");
                    Console.WriteLine($"    PDF: {Describir(documento.Pdf)}");
                }
            }
        }

        public void ImprimirVendedores(IReadOnlyList<NotificacionVendedor> notificaciones)
        {
            Console.WriteLine($"Se encontraron {notificaciones.Count} vendedores con facturas sin ingresar a revisión.");

            foreach (var notificacion in notificaciones)
            {
                Console.WriteLine();
                Console.WriteLine($"Vendedor: {notificacion.Vendedor} <{notificacion.Email}>");
                Console.WriteLine($"  Clientes: {notificacion.Clientes.Count} | Facturas: {notificacion.TotalFacturas}" +
                                  $" | Saldo: {notificacion.Saldo:C} | Máx. vencida: {notificacion.DiasVencidoMaximo} días");

                if (notificacion.SinAgenteValido)
                    Console.WriteLine("  AVISO: son facturas sin agente asignado en el CRM; van al buzón de cobranza.");

                foreach (var cliente in notificacion.Clientes)
                {
                    Console.WriteLine($"  {cliente.Cliente} - {cliente.RazonSocial} | {cliente.Facturas.Count} facturas | {cliente.Saldo:C}");

                    foreach (var factura in cliente.Facturas)
                        Console.WriteLine($"    {factura.Factura} | Vence {factura.Vencimiento:dd/MM/yyyy}" +
                                          $" | {ClienteCartera.CalcularDiasVencido(factura)} días | {factura.Saldo:C}");
                }
            }
        }

        public void ImprimirEnvios(IReadOnlyList<ResultadoEnvio> resultados, bool modoPrueba)
        {
            Console.WriteLine();
            if (modoPrueba)
                Console.WriteLine("MODO PRUEBA: los correos se enviaron al buzón de pruebas, no a los destinatarios reales.");

            foreach (var resultado in resultados)
            {
                Console.WriteLine(resultado.Enviado
                    ? $"Enviado a {resultado.Cliente}: {string.Join(", ", resultado.Destinatarios)}"
                    : $"FALLÓ {resultado.Cliente}: {resultado.Error}");

                if (resultado.CopiaOculta.Count > 0)
                    Console.WriteLine($"  CCO: {string.Join(", ", resultado.CopiaOculta)}");
            }
        }

        public void ImprimirCobranza(ResultadoCobranza cobranza)
        {
            Console.WriteLine($"Se encontraron {cobranza.Notificaciones.Count} clientes con saldo vencido por notificar.");

            if (cobranza.ExcluidosPorRespuesta.Count > 0)
                Console.WriteLine($"Se excluyeron {cobranza.ExcluidosPorRespuesta.Count} que ya contestaron esta semana: " +
                                  string.Join(", ", cobranza.ExcluidosPorRespuesta.Select(n => n.Cliente)));

            foreach (var notificacion in cobranza.Notificaciones)
            {
                Console.WriteLine();
                Console.WriteLine($"Cliente: {notificacion.Cliente} - {notificacion.RazonSocial}" +
                                  $" | Facturas: {notificacion.TotalFacturas}" +
                                  $" | Máx. vencida: {notificacion.DiasVencidoMaximo} días");
                Console.WriteLine($"  Saldo vencido: {string.Join(" + ", notificacion.Saldos.Select(sa => $"{sa.Total:N2} {sa.Moneda}"))}");

                foreach (var contacto in notificacion.Contactos)
                    Console.WriteLine($"  Contacto: {contacto.NombreConTratamiento} ({contacto.Cargo}) <{contacto.Email}>");
            }

        }

        /// <summary>Lo que se ve en pantalla al correr el seguimiento.</summary>
        public void ImprimirSeguimiento(ResultadoSeguimiento resultado)
        {
            Console.WriteLine();
            Console.WriteLine($"Envíos revisados contra el buzón: {resultado.Conciliados}");
            Console.WriteLine($"Acuses detectados: {resultado.Respuestas.Count} | Rebotes: {resultado.Rebotes.Count}");

            foreach (var respuesta in resultado.Respuestas)
                Console.WriteLine($"  CONTESTADO {respuesta.Envio.Cliente} - {respuesta.Envio.RazonSocial}" +
                                  $" | {respuesta.DeEmail} el {respuesta.Fecha:dd/MM/yyyy HH:mm}");

            foreach (var rebote in resultado.Rebotes)
                Console.WriteLine($"  REBOTE {rebote.Envio.Cliente}: {rebote.Envio.Destinatarios} — corregir en el CRM");

            if (resultado.Cerrados.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"Cobranza cerrada sin respuesta ({resultado.Cerrados.Count}):");

                foreach (var envio in resultado.Cerrados)
                    Console.WriteLine($"  {envio.Cliente} - {envio.RazonSocial} | enviado el {envio.FechaEnvio:dd/MM/yyyy}");
            }
        }

        private static string Describir(ArchivoFactura? archivo) =>
            archivo is null ? "no disponible" : $"{archivo.NombreArchivo} ({archivo.Tamanio} bytes)";
    }
}
