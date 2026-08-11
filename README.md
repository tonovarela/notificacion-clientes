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
| `Bitacora` | `Ruta` | Directorio de las bitácoras de ejecución. Por omisión `Logs`, relativo al ejecutable. |
| `Smtp` | `Host`, `Puerto` | Servidor de salida. El puerto define el modo de cifrado (ver abajo). |
| `Smtp` | `Usuario`, `Password` | Credenciales. En Gmail se usa una contraseña de aplicación, no la del buzón. |
| `Smtp` | `RemitenteNombre`, `RemitenteEmail` | Remitente que ve el cliente. |
| `Smtp` | `ModoPrueba` | Si es `true`, **ningún correo llega al cliente**: todo se redirige a `CorreoPrueba`. |
| `Smtp` | `CorreoPrueba` | Buzón que recibe todo mientras `ModoPrueba` esté activo. |
| `Smtp` | `CopiaOculta` | Direcciones en CCO de cada correo, separadas por coma. Opcional. |

Si falta alguna llave obligatoria, la aplicación falla al arrancar con un mensaje que dice cuál.

### Variables de entorno

Cualquier llave se puede definir también como variable de entorno, y **pisa** al valor del archivo.
El nombre es el mismo cambiando `:` por `__`:

| Llave | Variable |
|---|---|
| `ConnectionStrings:SqlServer` | `ConnectionStrings__SqlServer` |
| `ApiFacturas:UrlDescarga` | `ApiFacturas__UrlDescarga` |
| `Smtp:Password` | `Smtp__Password` |

`appsettings.json` es opcional: si no existe, la configuración se toma por completo del entorno. Así
es como corre en Docker.

### Modo prueba

`ModoPrueba` viene en `true` por omisión, a propósito: el programa trabaja con correos reales de
clientes y una ejecución accidental les enviaría facturas. Ponlo en `false` solo cuando hayas
validado el resultado en el buzón de pruebas.

Lo que redirige son los destinatarios del cliente; las direcciones de `CopiaOculta` reciben su copia
igual que en una corrida normal.

### Copia oculta (CCO)

`Smtp:CopiaOculta` agrega una lista de direcciones en CCO a **cada** correo, útil para que el buzón
de cobranza conserve una copia de todo lo que se notificó. En `appsettings.json` va como arreglo:

```json
"CopiaOculta": ["cxc@litoprocess.com", "respaldo@litoprocess.com"]
```

En Docker cabe en una sola variable, separando con coma o punto y coma:

```
Smtp__CopiaOculta=cxc@litoprocess.com,respaldo@litoprocess.com
```

También se acepta el arreglo indexado (`Smtp__CopiaOculta__0`, `Smtp__CopiaOculta__1`, …) y una sola
dirección suelta.

Van en CCO, así que los clientes no ven esas direcciones ni se ven entre ellos. **La copia oculta se
manda siempre, incluso con `ModoPrueba` activo**: es el registro de la facturación y debe recibir lo
mismo que se envió, sin importar el modo de la corrida. Ojo con eso al hacer ensayos, porque son
buzones reales los que reciben cada prueba. Si una dirección está mal escrita, la ejecución se
detiene antes de conectarse al servidor y lo dice en la bitácora.

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

## Bitácora

Cada corrida deja un archivo de evidencia en el directorio `Logs` (configurable con `Bitacora:Ruta`),
con la fecha y hora del inicio en el nombre para que no se pisen entre sí y queden ordenados:

```
Logs/envios-2026-08-11_09-30-45.log
```

Contiene un encabezado con el remitente, el servidor SMTP, si la corrida fue en modo prueba y un
resumen de totales; después, cliente por cliente, el estado del envío (`ENVIADO` / `FALLO`), los
destinatarios reales, los contactos del CRM, los importes y cada factura con sus adjuntos:

```
[001] ENVIADO | Cliente C000123 - COMERCIALIZADORA DEL BAJIO S.A. DE C.V.
      Destinatarios : lmendez@bajio.com, rortiz@bajio.com
      Contactos CRM : Laura Méndez <lmendez@bajio.com>, Raúl Ortiz <rortiz@bajio.com>
      Importes      : Subtotal $15,900.00 | IVA $2,544.00 | Total $18,444.00
      Facturas (2):
        - FAC-88120 | Periodo 8/2026 | Total $14,500.00 MXN
            XML: FAC-88120.xml (4821 bytes)
            PDF: FAC-88120.pdf (96410 bytes)
```

Si el proceso se interrumpe (no levanta la conexión SMTP, se cae la base) la bitácora se escribe de
todos modos con el motivo, y el programa termina con código de salida 1. Los importes se formatean
siempre en `es-MX` para que la evidencia no dependa de la configuración regional del equipo.

## Estructura

```
Configuracion/   Lectura y validación de appsettings.json
DAO/             Acceso a SQL Server (Dapper)
Entity/          Modelos: Factura, ArchivoFactura, NotificacionCliente
Services/        Descarga de archivos, orquestación, plantilla, envío de correo, reporte y bitácora
Plantillas/      Plantilla HTML del correo (Scriban)
Recursos/        Logo que se incrusta en el correo
Logs/            Bitácora de cada ejecución (no se versiona)
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
| `Microsoft.Extensions.Configuration.EnvironmentVariables` | Lectura de la configuración desde el entorno |

## Docker

La imagen no incluye `appsettings.json`: toda la configuración entra por variables de entorno, que
se declaran en un archivo `.env` (tampoco versionado).

```bash
cp .env.example .env      # y llenar credenciales
docker compose build
docker compose run --rm notificacion-clientes
```

`docker compose up` hace lo mismo; se usa `run --rm` porque es un proceso de una sola pasada y así
no queda el contenedor detenido. Para previsualizar sin enviar nada:

```bash
docker compose run --rm notificacion-clientes --previsualizar
```

Detalles de la imagen:

- Build en dos etapas (`sdk:8.0` → `runtime:8.0`); solo el resultado de `dotnet publish` queda en la
  imagen final.
- Corre como el usuario sin privilegios `app` que traen las imágenes oficiales.
- `TZ` (por omisión `America/Mexico_City`) define la zona horaria de las fechas del correo y de la
  bitácora.
- Las bitácoras quedan en `./logs` del host, montado como volumen en `/app/Logs`.
- El SQL Server y el API viven en la LAN, así que el contenedor los alcanza por la red del host con
  sus IPs normales.

## Despliegue

```bash
dotnet publish -c Release
```

La plantilla, el logo y `appsettings.json` se copian al directorio de salida. En el servidor hay que
crear el `appsettings.json` con los valores del ambiente —o definir las variables de entorno
equivalentes—, ya que no viene en el repositorio.

Está pensado para correr una vez al día mediante una tarea programada (o `docker compose run` desde
cron), después de la emisión de facturas.
