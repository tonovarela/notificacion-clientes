using System.Resources;
using Dapper;
using Microsoft.Data.SqlClient;
using notificacion_clientes.Entity;

namespace notificacion_clientes.DAO
{
    public class FacturaDAO : IFacturaDAO
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

        /// <summary>
        /// Facturas a notificar. <paramref name="diasAtras"/> es cuántos días hacia atrás se
        /// incluyen: 0 = sólo las de hoy, que es lo normal.
        ///
        /// El rango importa más de lo que parece. Con seguimiento encima, cada corrida abre un
        /// envío por cada factura que trae; un rango de tres semanas volvería a abrir envíos por
        /// facturas ya notificadas todos los días, y a los N días dispararía un recordatorio por
        /// cada uno. El tope superior deja fuera las facturas con fecha futura, que existen por
        /// captura y no deben notificarse antes de tiempo.
        /// </summary>
        public async Task<IEnumerable<Factura>> Obtener(int diasAtras = 0)
        {
            Console.WriteLine($"Obteniendo facturas de los últimos {diasAtras} días...");
            string sql = @"SELECT
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
                                          AND v.FechaEmision >= DATEADD(DAY, -@DiasAtras, CAST(GETDATE() AS DATE))
                                          AND v.FechaEmision <  DATEADD(DAY, 1, CAST(GETDATE() AS DATE));";
            using var conexion = new SqlConnection(_sqlConexion);
            return await conexion.QueryAsync<Factura>(sql, new { DiasAtras = diasAtras });
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



        /// <summary>
        /// Cobranza vencida por cliente: lo que ya se pasó de la fecha de pago.
        ///
        /// El contacto es el de cuentas por pagar (contactoCXP), que no es el mismo que recibe los
        /// CFDI del día.
        ///
        /// El filtro 'x.email is not null' deja fuera a los clientes sin contacto capturado en el
        /// CRM: no hay a quién escribirles, así que no llegan al proceso. Es una decisión
        /// deliberada y tiene un costo — esas cuentas vencidas quedan invisibles para este
        /// reporte y hay que vigilarlas por otro medio.
        ///
        /// Devuelve una fila por factura y por contacto; agrupar es trabajo del servicio.
        /// </summary>
        public async Task<IEnumerable<FacturaCobranzaVencida>> ObtenerFacturasCobranzaVencida()
        {
            string sql = @"
            WITH FacturasNotificaficadas
                AS
                (
                SELECT
                ev.MovId 
                FROM CorreosCXC.notif.EnvioFactura ev
                JOIN CorreosCXC.notif.Envio e ON e.IdEnvio=ev.IdEnvio
                )
 SELECT

                                v.Cliente,
                                v.Nombre                            AS RazonSocial,
                                Factura = v.Mov + ' ' + v.MovID,
                                v.MovID,
                                v.FechaEmision,
                                v.Condicion,
                                v.Vencimiento,
                                v.Moneda,
                                v.TotalVencido,
                                x.NombreAgente,
                                x.Tratamiento,
                                UPPER(x.Nombre)                     AS Nombre,
                                x.Cargo,
                                x.Email
                            FROM etl_mstr.dbo.v_AntiguedadCxC v
                            LEFT JOIN (SELECT c.RazonSocial, c.NombreAgente, ctos.Tratamiento, ctos.Nombre,
                                                  ctos.Cargo, ctos.Email, ctos.Telefonos, c.ClienteINT
                                           FROM litocrm.dbo.v_catClientes c
                                               LEFT JOIN (SELECT Tratamiento, Nombre, Cargo, Departamento, Telefonos, Email, idClienteINT
                                                          FROM LitoCRM.dbo.v_catContactos
                                                          WHERE contactoCXP = 1 AND activo = 1) ctos
                                                   ON ctos.idClienteINT = c.ClienteINT) x ON x.ClienteINT = v.Cliente
                                LEFT JOIN FacturasNotificaficadas fn ON fn.MovId = v.MovID                   
                            WHERE v.Mov       = 'Factura Electronica'
                            AND fn.MovId is null                              
                            AND v.Categoria = 'VENCIDAS'    
                            and v.Cliente= '10040'                            
                            AND x.email is not null
                            ORDER BY v.Cliente, v.Vencimiento;";
            using var conexion = new SqlConnection(_sqlConexion);
            return await conexion.QueryAsync<FacturaCobranzaVencida>(sql);
        }


         public async Task<IEnumerable<FacturaCobranzaVencida>> ObtenerFacturasCobranzaVencidaSinContestar()
        {
            string sql = @"
            WITH EnviosNoContestados
                AS
                (
                SELECT                
                ev.MovId 
                FROM CorreosCXC.notif.EnvioFactura ev
                JOIN CorreosCXC.notif.Envio e ON e.IdEnvio=ev.IdEnvio
                AND E.Estado NOT IN ('CONTESTADO')
                )
                    SELECT

                                v.Cliente,
                                v.Nombre       AS RazonSocial,
                                Factura = v.Mov + ' ' + v.MovID,
                                v.MovID,
                                v.FechaEmision,
                                v.Condicion,
                                v.Vencimiento,
                                v.Moneda,
                                v.TotalVencido,
                                x.NombreAgente,
                                x.Tratamiento,
                                UPPER(x.Nombre)                     AS Nombre,
                                x.Cargo,
                                x.Email
                            FROM etl_mstr.dbo.v_AntiguedadCxC v
                            LEFT JOIN (SELECT c.RazonSocial, c.NombreAgente, ctos.Tratamiento, ctos.Nombre,
                                                  ctos.Cargo, ctos.Email, ctos.Telefonos, c.ClienteINT
                                           FROM litocrm.dbo.v_catClientes c
                                               LEFT JOIN (SELECT Tratamiento, Nombre, Cargo, Departamento, Telefonos, Email, idClienteINT
                                                          FROM LitoCRM.dbo.v_catContactos
                                                          WHERE contactoCXP = 1 AND activo = 1) ctos
                                                   ON ctos.idClienteINT = c.ClienteINT) x ON x.ClienteINT = v.Cliente
                            LEFT JOIN EnviosNoContestados fn ON fn.MovId = v.MovID                   
                            WHERE  1=1                   
                            AND v.Mov       = 'Factura Electronica'         
                            AND fn.MovId is not null                              
                            AND v.Categoria = 'VENCIDAS'                              
                            AND x.email is not null
                            and v.Cliente= '10040'
                            and v.MovID not in ('CFDI66501','CFDI66502')
                            ORDER BY v.Cliente, v.Vencimiento;
                            ";
            using var conexion = new SqlConnection(_sqlConexion);
            return await conexion.QueryAsync<FacturaCobranzaVencida>(sql);
        }




    
        
    
    
    
    }
}
