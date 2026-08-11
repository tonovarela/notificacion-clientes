using System;

namespace notificacion_clientes.Entity
{
    public enum TipoArchivo
    {
        Xml,
        Pdf
    }

    public class ArchivoFactura
    {
        public required string Cfdi { get; set; }

        public required TipoArchivo Tipo { get; set; }

        /// <summary>Nombre que reporta el servidor en Content-Disposition, ya saneado.</summary>
        public required string NombreArchivo { get; set; }

        public required string ContentType { get; set; }

        public required byte[] Contenido { get; set; }

        public int Tamanio => Contenido.Length;
    }
}
