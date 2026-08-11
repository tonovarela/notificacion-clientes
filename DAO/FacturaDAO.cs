using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using notificacion_clientes.Entity;

namespace notificacion_clientes.DAO
{
    public class FacturaDAO
    {
        private readonly string _cadena;

        public FacturaDAO(string cadena)
        {
            _cadena = cadena;
        }

        public async Task<IEnumerable<Factura>> Obtener()
        {
            using var conexion = new SqlConnection(_cadena);
            return await conexion.QueryAsync<Factura>(
                @"
                select
        v.Cliente,
   x.razonsocial RazonSocial,
      v.importe Importe ,
     v.periodo Periodo, 
     v.Ejercicio,
  upper(x.nombre) Nombre,
  x.cargo Cargo,
   x.email Email,
   v.movid MovID
from lito.dbo.venta v
left join ( 
          select c.razonsocial, c.nombreagente, ctos.Tratamiento, ctos.Nombre,  ctos.cargo, ctos.email, ctos.Telefonos, c.ClienteINT
			from LITOCRM.dbo.v_catclientes c
					left join (select Tratamiento, nombre, cargo, Departamento, Telefonos, email, idClienteINT
								FROM [LitoCRM].[dbo].[v_catContactos] where cfd_enviar = 1 and activo = 1) ctos on ctos.idClienteINT =  c.ClienteINT) x on x.ClienteINT = v.Cliente
where mov = 'Factura Electronica' and estatus = 'concluido'
and cliente in (select clienteint from litocrm.dbo.v_catClientes where idMetodoRevision = 4 and estatus = 'alta' and diasrevision is not null )
and fechaEmision = convert(date,getdate()-21)

                ");
        }
    }
}
