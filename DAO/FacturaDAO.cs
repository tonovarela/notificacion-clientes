using Dapper;
using Microsoft.Data.SqlClient;
using notificacion_clientes.Entity;

namespace notificacion_clientes.DAO
{
    public class FacturaDAO
    {

        private readonly string _sqlConexion;
        // private const string sqlAlejandro = @"select
        //                     v.Cliente,
        //                     x.razonsocial RazonSocial,
        //                     v.importe Importe ,
        //                     v.periodo Periodo, 
        //                     v.Ejercicio,
        //                     upper(x.nombre) Nombre,
        //                     x.cargo Cargo,
        //                     x.email Email,
        //                     v.movid MovID
        //                     from lito.dbo.venta v
        //                     left join ( 
        //                     select c.razonsocial, c.nombreagente, ctos.Tratamiento, ctos.Nombre,  ctos.cargo, ctos.email, ctos.Telefonos, c.ClienteINT
		// 	                from LITOCRM.dbo.v_catclientes c
		// 			            left join (select Tratamiento, nombre, cargo, Departamento, Telefonos, email, idClienteINT
		// 						FROM [LitoCRM].[dbo].[v_catContactos] where cfd_enviar = 1 and activo = 1) ctos on ctos.idClienteINT =  c.ClienteINT) x on x.ClienteINT = v.Cliente
        //                         where mov = 'Factura Electronica' and estatus = 'concluido'
        //                             and cliente in (select clienteint from litocrm.dbo.v_catClientes where idMetodoRevision = 4 and estatus = 'alta' and diasrevision is not null )
        //                     and fechaEmision = convert(date,getdate())";
      

        public FacturaDAO(string sqlConexion)
        {
            _sqlConexion = sqlConexion;
        }

        public async Task<IEnumerable<Factura>> Obtener()
        {
            string sql= @"SELECT
                                            v.Cliente,
                                            c.RazonSocial,
                                            v.Importe,
                                            v.Periodo,
                                            v.Ejercicio,
                                            UPPER(ctos.Nombre) AS Nombre,
                                            ctos.Cargo,
                                            ctos.Email,
                                            v.MovID,
                                            v.FechaEmision
                                        FROM lito.dbo.venta v
                                            INNER JOIN LITOCRM.dbo.v_catClientes c
                                                ON  c.ClienteINT       = v.Cliente
                                                AND c.idMetodoRevision = 4
                                                AND c.Estatus          = 'alta'
                                                AND c.diasRevision IS NOT NULL
                                            LEFT JOIN LITOCRM.dbo.v_catContactos ctos
                                                ON  ctos.idClienteINT = c.ClienteINT
                                                AND ctos.cfd_enviar   = 1
                                                AND ctos.activo       = 1
                                        WHERE v.Mov       = 'Factura Electronica'
                                          AND v.Estatus   = 'concluido'  
                                          --AND v.FechaEmision = CONVERT(DATE, GETDATE())
                                          AND v.FechaEmision >= DATEADD(DAY, -21, CAST(GETDATE() AS DATE));";
            using var conexion = new SqlConnection(_sqlConexion);
            return await conexion.QueryAsync<Factura>(sql);
        }


        /// <summary>
        /// Cartera por vendedor: facturas vencidas que el cliente todavía no ingresa a revisión.
        /// El agente sin correo en el CRM cae en el buzón de cobranza para que nadie se quede sin dueño.
        /// </summary>
        public async Task<IEnumerable<FacturaRevisionVendedor>> ObtenerFacturasRevisionVendedores()
        {
            string sql = @"SELECT
                                v.Cliente,
                                v.NombreCte                                     AS RazonSocial,
                                Factura = v.Mov + ' ' + v.MovID,
                                v.MovID,
                                v.FechaEmision,
                                v.Vencimiento,
                                v.Saldo,
                                v.EstatusCxC,
                                ISNULL(x.nombre, 'AGENTE NO VALIDO')            AS Vendedor,
                                ISNULL(x.email, 'gcasas@litoprocess.com')       AS Email
                            FROM etl_mstr.dbo.v_AntiguedadCxCST v
                                LEFT JOIN (SELECT agente, nombre, email FROM litocrm.dbo.v_catAgentes) x
                                    ON x.agente = v.Agente
                            WHERE v.Situacion  = 'NO INGRESADA'
                              AND v.EstatusCxC NOT IN ('1.0-30')
                            ORDER BY Vendedor, v.NombreCte, v.Vencimiento;";
            using var conexion = new SqlConnection(_sqlConexion);
            return await conexion.QueryAsync<FacturaRevisionVendedor>(sql);
        }
    }
}
