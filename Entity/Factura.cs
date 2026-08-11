using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace notificacion_clientes.Entity
{
    public class Factura
    {
        public required string Cliente { get; set; }

        public string? RazonSocial { get; set; }

        public decimal Importe { get; set; }
        //public decimal Iva { get; set; }

        public required string Ejercicio { get; set; }

        public required string Periodo { get; set; }

        public required string Nombre { get; set; }

        public string? Cargo { get; set; }
        public string? Email { get; set; }

        public required string MovID { get; set; }

    }
}