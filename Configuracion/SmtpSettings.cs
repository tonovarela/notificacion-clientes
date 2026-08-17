using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace notificacion_clientes.Configuracion
{
    public class SmtpSettings
    {
        public required string Host { get; init; }

        public required int Puerto { get; init; }

        public required string Usuario { get; init; }

        public required string Password { get; init; }

        public required bool UsarSsl { get; init; }

        public required string RemitenteNombre { get; init; }

        public required string RemitenteEmail { get; init; }

        /// <summary>
        /// Cuando es true, ningún correo llega al cliente: todo se redirige a CorreoPrueba.
        /// Viene activado por omisión para no notificar clientes reales por accidente.
        /// </summary>
        public required bool ModoPrueba { get; init; }

        public string? CorreoPrueba { get; init; }

        /// <summary>
        /// Lo mismo que ModoPrueba pero solo para el aviso a vendedores. Existe por separado porque
        /// los dos procesos se programan aparte: se puede dejar el de clientes en producción mientras
        /// el de vendedores todavía se está probando. Si no se configura, hereda ModoPrueba.
        /// </summary>
        public required bool ModoPruebaVendedores { get; init; }

        /// <summary>Buzón que recibe los avisos a vendedores en modo prueba. Si no se configura, usa CorreoPrueba.</summary>
        public string? CorreoPruebaVendedores { get; init; }

        /// <summary>
        /// Direcciones que reciben copia oculta de cada correo, normalmente el buzón que archiva
        /// la facturación. El cliente no las ve. Reciben copia también en modo prueba.
        /// Vacío si no se configuró ninguna.
        /// </summary>
        public IReadOnlyList<string> CopiaOculta { get; init; } = Array.Empty<string>();

        public static SmtpSettings Cargar(IConfiguration configuracion)
        {
            var seccion = configuracion.GetSection("Smtp");

            var modoPrueba = !bool.TryParse(seccion["ModoPrueba"], out var prueba) || prueba;
            var correoPrueba = seccion["CorreoPrueba"];

            // Las llaves de vendedores son opcionales: sin ellas el proceso se comporta igual que antes,
            // siguiendo la bandera general. Ponerlas solo sirve para separar los dos procesos.
            var modoPruebaVendedores = bool.TryParse(seccion["ModoPruebaVendedores"], out var pruebaVendedores)
                ? pruebaVendedores
                : modoPrueba;

            var correoPruebaVendedores = string.IsNullOrWhiteSpace(seccion["CorreoPruebaVendedores"])
                ? correoPrueba
                : seccion["CorreoPruebaVendedores"];

            if (modoPrueba && string.IsNullOrWhiteSpace(correoPrueba))
                throw new InvalidOperationException("Con 'Smtp:ModoPrueba' activo hay que definir 'Smtp:CorreoPrueba' (variable Smtp__CorreoPrueba)");

            if (modoPruebaVendedores && string.IsNullOrWhiteSpace(correoPruebaVendedores))
                throw new InvalidOperationException("Con 'Smtp:ModoPruebaVendedores' activo hay que definir 'Smtp:CorreoPruebaVendedores' o 'Smtp:CorreoPrueba' (variable Smtp__CorreoPruebaVendedores)");

            return new SmtpSettings
            {
                Host = seccion["Host"]
                    ?? throw new InvalidOperationException("Falta 'Smtp:Host' (variable Smtp__Host)"),

                Puerto = int.TryParse(seccion["Puerto"], out var puerto) ? puerto : 587,

                Usuario = seccion["Usuario"] ?? string.Empty,

                Password = seccion["Password"] ?? string.Empty,

                UsarSsl = !bool.TryParse(seccion["UsarSsl"], out var ssl) || ssl,

                RemitenteNombre = seccion["RemitenteNombre"] ?? "Facturación",

                RemitenteEmail = seccion["RemitenteEmail"]
                    ?? throw new InvalidOperationException("Falta 'Smtp:RemitenteEmail' (variable Smtp__RemitenteEmail)"),

                ModoPrueba = modoPrueba,

                CorreoPrueba = correoPrueba,

                ModoPruebaVendedores = modoPruebaVendedores,

                CorreoPruebaVendedores = correoPruebaVendedores,

                CopiaOculta = LeerCopiaOculta(seccion)
            };
        }

        /// <summary>
        /// Admite las dos formas de escribir la lista:
        /// arreglo — "CopiaOculta": ["a@x.com", "b@x.com"] en el JSON, o Smtp__CopiaOculta__0 y
        /// Smtp__CopiaOculta__1 como variables de entorno;
        /// y una sola línea — "a@x.com, b@x.com", que es lo que cabe en una variable suelta.
        /// </summary>
        private static IReadOnlyList<string> LeerCopiaOculta(IConfiguration seccion)
        {
            var copiaOculta = seccion.GetSection("CopiaOculta");

            var elementos = copiaOculta.GetChildren()
                .Select(elemento => elemento.Value)
                .Where(valor => !string.IsNullOrWhiteSpace(valor))
                .ToArray();

            // Cada elemento del arreglo se vuelve a separar por si trae varias direcciones juntas.
            return elementos.Length > 0
                ? elementos.SelectMany(SepararDirecciones).ToArray()
                : SepararDirecciones(copiaOculta.Value).ToArray();
        }

        private static IEnumerable<string> SepararDirecciones(string? valor)
        {
            if (string.IsNullOrWhiteSpace(valor))
                return Enumerable.Empty<string>();

            return valor.Split(
                new[] { ',', ';' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }
}
