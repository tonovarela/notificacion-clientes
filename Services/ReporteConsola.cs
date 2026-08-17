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

        private static string Describir(ArchivoFactura? archivo) =>
            archivo is null ? "no disponible" : $"{archivo.NombreArchivo} ({archivo.Tamanio} bytes)";
    }
}
