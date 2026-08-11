# Notificación de facturas a clientes

Aplicación de consola en .NET 8 que, para las facturas emitidas en el día, envía a cada cliente un
correo HTML con el XML y el PDF de sus CFDI adjuntos.

El proceso es:

1. Consulta en SQL Server las facturas electrónicas concluidas del día, junto con los contactos del
   CRM marcados para recibir CFDI.
2. Agrupa por cliente y descarga del API de facturas el XML y el PDF de cada CFDI.
3. Arma un correo HTML por cliente a partir de una plantilla y lo envía por SMTP con los adjuntos.

## Requisitos

- .NET SDK 8.0
- Acceso de red a la base de datos SQL Server, al API de facturas y al servidor SMTP

## Configuración

La configuración vive en `appsettings.json`, que **no se versiona** porque contiene credenciales.
Para preparar un ambiente:

```bash
cp appsettings.example.json appsettings.json
```

Y llenar los valores:

| Sección | Llave | Descripción |
|---|---|---|
| `ConnectionStrings` | `SqlServer` | Cadena de conexión. El usuario necesita permisos en las bases `Lito` y `LitoCRM`. |
| `ApiFacturas` | `UrlDescarga` | URL base del API. El CFDI y `?tipo=xml\|pdf` se agregan en tiempo de ejecución. |
| `ApiFacturas` | `TimeoutSegundos` | Timeout de las descargas. Por omisión 60. |
| `Correo` | `Plantilla` | Ruta de la plantilla HTML, relativa al ejecutable. |
| `Correo` | `Logo` | Imagen que se incrusta en el encabezado del correo. |
| `Smtp` | `Host`, `Puerto` | Servidor de salida. El puerto define el modo de cifrado (ver abajo). |
| `Smtp` | `Usuario`, `Password` | Credenciales. En Gmail se usa una contraseña de aplicación, no la del buzón. |
| `Smtp` | `RemitenteNombre`, `RemitenteEmail` | Remitente que ve el cliente. |
| `Smtp` | `ModoPrueba` | Si es `true`, **ningún correo llega al cliente**: todo se redirige a `CorreoPrueba`. |
| `Smtp` | `CorreoPrueba` | Buzón que recibe todo mientras `ModoPrueba` esté activo. |

Si falta alguna llave obligatoria, la aplicación falla al arrancar con un mensaje que dice cuál.

### Modo prueba

`ModoPrueba` viene en `true` por omisión, a propósito: el programa trabaja con correos reales de
clientes y una ejecución accidental les enviaría facturas. Ponlo en `false` solo cuando hayas
validado el resultado en el buzón de pruebas.

### Puertos SMTP

El modo de cifrado se deduce del puerto, así que basta cambiar el número:

- **465** — SSL implícito (`SslOnConnect`), cifra desde que abre el socket.
- **587 y demás** — `STARTTLS`.
- `UsarSsl: false` — sin cifrado, solo para servidores internos.

## Ejecución

```bash
dotnet run
```

Con `--previsualizar` se genera el HTML de cada correo en el directorio de salida y **no se envía
nada**. Sirve para iterar el diseño de la plantilla:

```bash
dotnet run -- --previsualizar
```

## Estructura

```
Configuracion/   Lectura y validación de appsettings.json
DAO/             Acceso a SQL Server (Dapper)
Entity/          Modelos: Factura, ArchivoFactura, NotificacionCliente
Services/        Descarga de archivos, orquestación, plantilla, envío de correo y reporte
Plantillas/      Plantilla HTML del correo (Scriban)
Recursos/        Logo que se incrusta en el correo
```

`Program.cs` funciona solo como composition root: carga la configuración, arma las dependencias y
ejecuta el proceso.

## Plantilla del correo

`Plantillas/notificacion-cliente.html` se procesa con [Scriban](https://github.com/scriban/scriban).
Variables disponibles:

| Variable | Contenido |
|---|---|
| `razon_social` | Razón social del cliente |
| `cliente` | Número de cliente |
| `saludo` | Nombre del contacto, o "cliente" si el correo va a varios |
| `total_documentos` | Cantidad de facturas |
| `total_archivos` | Cantidad de adjuntos |
| `importe_total` | Suma de los importes, formateada |
| `documentos` | Lista con `mov_id`, `periodo`, `ejercicio` e `importe` |

El HTML usa tablas y estilos inline porque es lo que Outlook y Gmail respetan. El logo se manda
incrustado (`cid:logo`) en vez de como URL, ya que los clientes de correo bloquean las imágenes
remotas por omisión.

## Dependencias

| Paquete | Para qué |
|---|---|
| `Microsoft.Data.SqlClient` | Driver de SQL Server |
| `Dapper` | Mapeo de las consultas a objetos |
| `MailKit` | Envío SMTP (`System.Net.Mail.SmtpClient` está obsoleto) |
| `Scriban` | Motor de plantillas HTML |
| `Microsoft.Extensions.Configuration.Json` | Lectura de `appsettings.json` |

## Despliegue

```bash
dotnet publish -c Release
```

La plantilla, el logo y `appsettings.json` se copian al directorio de salida. En el servidor hay que
crear el `appsettings.json` con los valores del ambiente, ya que no viene en el repositorio.

Está pensado para correr una vez al día mediante una tarea programada, después de la emisión de
facturas.
