# Notificación de facturas

Aplicación de consola en .NET 8 que manda dos avisos distintos por correo, ambos sobre facturas y
ambos con la misma configuración, servidor SMTP y bitácora.

| Proceso | Argumento | A quién le llega | Qué lleva |
|---|---|---|---|
| **Facturas del día** | *(ninguno)* | A los contactos del cliente marcados en el CRM | Un correo por cliente con el XML y el PDF de sus CFDI adjuntos |
| **Cartera por vendedor** | `--vendedores` | Al vendedor dueño de la cuenta | Un correo por vendedor con las facturas vencidas que sus clientes todavía no ingresan a revisión |

Son procesos independientes: se ejecutan por separado, cada uno escribe su propia bitácora y usan
plantillas distintas. Comparten el ejecutable porque comparten configuración y conexión SMTP.

### Facturas del día

1. Consulta en SQL Server las facturas electrónicas concluidas del día, junto con los contactos del
   CRM marcados para recibir CFDI.
2. Agrupa por cliente y descarga del API de facturas el XML y el PDF de cada CFDI.
3. Arma un correo HTML por cliente a partir de una plantilla y lo envía por SMTP con los adjuntos.

### Cartera por vendedor

1. Consulta la vista de antigüedad de saldos por las facturas en situación `NO INGRESADA` con más
   de 30 días de vencidas, y les cruza el agente del CRM.
2. Agrupa por **correo del vendedor** —no por nombre, porque el mismo agente puede venir capturado
   de varias formas— y dentro de cada vendedor, por cliente.
3. Manda un correo por vendedor con su cartera desglosada. Aquí no se adjunta ningún CFDI: es un
   corte para dar seguimiento, no un envío de documentos.

Las facturas cuyo agente no existe en el CRM no se pierden: caen en el buzón de cobranza y el correo
trae un aviso visible para que se corrija el catálogo.

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
| `ConnectionStrings` | `SqlServer` | Cadena de conexión. El usuario necesita permisos en las bases `Lito`, `LitoCRM` y `etl_mstr`. |
| `ApiFacturas` | `UrlDescarga` | URL base del API. El CFDI y `?tipo=xml\|pdf` se agregan en tiempo de ejecución. |
| `ApiFacturas` | `TimeoutSegundos` | Timeout de las descargas. Por omisión 60. |
| `Correo` | `Plantilla` | Plantilla HTML del correo al cliente, relativa al ejecutable. |
| `Correo` | `PlantillaVendedor` | Plantilla HTML de la cartera que se manda al vendedor. |
| `Correo` | `Logo` | Imagen que se incrusta en el encabezado de ambos correos. |
| `Bitacora` | `Ruta` | Directorio de las bitácoras de ejecución. Por omisión `Logs`, relativo al ejecutable. |
| `Smtp` | `Host`, `Puerto` | Servidor de salida. El puerto define el modo de cifrado (ver abajo). |
| `Smtp` | `Usuario`, `Password` | Credenciales. En Gmail se usa una contraseña de aplicación, no la del buzón. |
| `Smtp` | `RemitenteNombre`, `RemitenteEmail` | Remitente que ve quien recibe el correo. |
| `Smtp` | `ModoPrueba` | Si es `true`, **ningún correo llega al destinatario real**: todo se redirige a `CorreoPrueba`. |
| `Smtp` | `CorreoPrueba` | Buzón que recibe todo mientras `ModoPrueba` esté activo. |
| `Smtp` | `ModoPruebaVendedores` | Lo mismo, pero solo para el aviso a vendedores. Opcional: si falta, hereda `ModoPrueba`. |
| `Smtp` | `CorreoPruebaVendedores` | Buzón único que recibe los avisos a vendedores en modo prueba. Opcional: si falta, usa `CorreoPrueba`. |
| `Smtp` | `CopiaOculta` | Direcciones en CCO de cada correo, separadas por coma. Opcional. |

Si falta alguna llave obligatoria, la aplicación falla al arrancar con un mensaje que dice cuál.
`Correo:PlantillaVendedor` tiene valor por omisión, así que solo hace falta declararla si mueves el
archivo.

### Variables de entorno

Cualquier llave se puede definir también como variable de entorno, y **pisa** al valor del archivo.
El nombre es el mismo cambiando `:` por `__`:

| Llave | Variable |
|---|---|
| `ConnectionStrings:SqlServer` | `ConnectionStrings__SqlServer` |
| `ApiFacturas:UrlDescarga` | `ApiFacturas__UrlDescarga` |
| `Correo:PlantillaVendedor` | `Correo__PlantillaVendedor` |
| `Smtp:Password` | `Smtp__Password` |

`appsettings.json` es opcional: si no existe, la configuración se toma por completo del entorno. Así
es como corre en Docker.

### Modo prueba

`ModoPrueba` viene en `true` por omisión, a propósito: el programa trabaja con correos reales de
clientes y vendedores, y una ejecución accidental les enviaría avisos. Ponlo en `false` solo cuando
hayas validado el resultado en el buzón de pruebas.

Aplica igual a los dos procesos: lo que redirige son los destinatarios reales, y las direcciones de
`CopiaOculta` reciben su copia igual que en una corrida normal.

Los dos procesos se programan por separado, así que también se pueden probar por separado:
`ModoPruebaVendedores` y `CorreoPruebaVendedores` controlan únicamente el aviso a vendedores
(`--vendedores`). Con eso el envío a clientes puede quedarse en producción mientras el de vendedores
sigue saliendo a un solo buzón:

```
Smtp__ModoPrueba=false
Smtp__ModoPruebaVendedores=true
Smtp__CorreoPruebaVendedores=pruebas@litoprocess.com
```

Si no se declaran, el proceso de vendedores se comporta como antes y sigue la bandera general. En
modo prueba de vendedores el correo va a **un solo destinatario**, el buzón de pruebas, aunque el
cuerpo traiga la cartera completa del vendedor; la consola y la bitácora dejan constancia de que la
corrida fue de prueba y de a dónde se redirigió.

### Copia oculta (CCO)

`Smtp:CopiaOculta` agrega una lista de direcciones en CCO a **cada** correo —tanto los del cliente
como los del vendedor—, útil para que el buzón de cobranza conserve una copia de todo lo que se
notificó. En `appsettings.json` va como arreglo:

```json
"CopiaOculta": ["cxc@litoprocess.com", "respaldo@litoprocess.com"]
```

En Docker cabe en una sola variable, separando con coma o punto y coma:

```
Smtp__CopiaOculta=cxc@litoprocess.com,respaldo@litoprocess.com
```

También se acepta el arreglo indexado (`Smtp__CopiaOculta__0`, `Smtp__CopiaOculta__1`, …) y una sola
dirección suelta.

Van en CCO, así que quienes reciben el correo no ven esas direcciones ni se ven entre ellos. **La
copia oculta se manda siempre, incluso con `ModoPrueba` activo**: es el registro de la facturación y
debe recibir lo mismo que se envió, sin importar el modo de la corrida. Ojo con eso al hacer
ensayos, porque son buzones reales los que reciben cada prueba. Si una dirección está mal escrita,
la ejecución se detiene antes de conectarse al servidor y lo dice en la bitácora.

### Puertos SMTP

El modo de cifrado se deduce del puerto, así que basta cambiar el número:

- **465** — SSL implícito (`SslOnConnect`), cifra desde que abre el socket.
- **587 y demás** — `STARTTLS`.
- `UsarSsl: false` — sin cifrado, solo para servidores internos.

## Ejecución

```bash
dotnet run                  # facturas del día a los clientes
dotnet run -- --vendedores  # cartera pendiente de revisión a los vendedores
```

Con `--previsualizar` se genera el HTML de cada correo en el directorio de salida y **no se envía
nada**. Sirve para iterar el diseño de la plantilla, y funciona con los dos procesos:

```bash
dotnet run -- --previsualizar
dotnet run -- --vendedores --previsualizar
```

Los archivos quedan como `previsualizacion-cliente-<cliente>.html` y
`previsualizacion-vendedor-<correo>.html` junto al ejecutable. Ojo: la previsualización sí consulta
la base de datos —lo único que se salta es el envío—, así que el contenido es el real de la corrida.

## Bitácora

Cada corrida deja un archivo de evidencia en el directorio `Logs` (configurable con `Bitacora:Ruta`),
con la fecha y hora del inicio en el nombre para que no se pisen entre sí y queden ordenados. Cada
proceso escribe el suyo, así que las dos corridas del día no se mezclan:

```
Logs/envios-2026-08-11_09-30-45.log                 # facturas del día a clientes
Logs/revision-vendedores-2026-08-11_19-00-12.log    # cartera a vendedores
```

Ambos abren con un encabezado que trae el remitente, el servidor SMTP, si la corrida fue en modo
prueba y un resumen de totales. Después viene el detalle.

Cliente por cliente, con el estado del envío (`ENVIADO` / `FALLO`), los destinatarios reales, los
contactos del CRM, los importes y cada factura con sus adjuntos:

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

Y vendedor por vendedor, con su cartera desglosada por cliente:

```
[001] ENVIADO | JUAN PEREZ <jperez@litoprocess.com>
      Destinatarios : jperez@litoprocess.com
      Cartera       : 2 clientes | 3 facturas | Saldo $66,011.25 | Maxima 100 dias vencida
        Cliente 2044 - GRUPO INDUSTRIAL LOPEZ | 1 facturas | $45,210.75
          - Factura Electronica CFDI66412 | Emitida 09/04/2026 | Vence 09/05/2026 | 100 dias | Saldo $45,210.75
```

Si el proceso se interrumpe (no levanta la conexión SMTP, se cae la base) la bitácora se escribe de
todos modos con el motivo, y el programa termina con código de salida 1. Los importes se formatean
siempre en `es-MX` para que la evidencia no dependa de la configuración regional del equipo.

## Estructura

```
Configuracion/   Lectura y validación de appsettings.json
DAO/             Acceso a SQL Server (Dapper)
Entity/          Modelos: Factura y NotificacionCliente (clientes),
                 FacturaRevisionVendedor y NotificacionVendedor (vendedores)
Services/        Descarga de archivos, orquestación, plantillas, envío de correo, reporte y bitácora
Plantillas/      Plantillas HTML de los correos (Scriban)
Recursos/        Logo que se incrusta en los correos
Logs/            Bitácora de cada ejecución (no se versiona)
```

`Program.cs` funciona solo como composition root: carga la configuración, arma las dependencias y
ejecuta el proceso que le pidieron por argumento.

Lo que comparten los dos flujos está en un solo lugar: `CorreoService` abre una sola conexión SMTP y
manda el lote sin importar de qué tipo sea, y `PlantillaCompilada` lee y compila cada plantilla una
sola vez, la primera vez que se usa.

## Plantillas de los correos

Se procesan con [Scriban](https://github.com/scriban/scriban). Los importes, las fechas y los
plurales llegan ya formateados en `es-MX`: la plantilla solo acomoda texto listo.

### `Plantillas/notificacion-cliente.html`

| Variable | Contenido |
|---|---|
| `cliente` | Número de cliente |
| `razon_social` | Razón social del cliente |
| `saludo` | Nombre del contacto, o "cliente" si el correo va a varios |
| `total_documentos` | Cantidad de facturas |
| `total_archivos` | Cantidad de adjuntos |
| `subtotal_general`, `iva_general`, `total_general` | Totales del correo, formateados |
| `documentos` | Lista con `mov_id`, `periodo`, `ejercicio`, `subtotal`, `iva` y `total` |

### `Plantillas/notificacion-vendedor.html`

| Variable | Contenido |
|---|---|
| `vendedor` | Nombre del agente |
| `sin_agente_valido` | `true` cuando la cartera cayó en cobranza por no tener agente en el CRM |
| `fecha_corte` | Fecha de la corrida, en texto |
| `total_clientes`, `total_facturas` | Tamaño de la cartera |
| `saldo_total` | Saldo pendiente del vendedor, formateado |
| `dias_vencido_maximo` | Días de la factura más atrasada |
| `clientes` | Lista con `cliente`, `razon_social`, `total_facturas`, `saldo`, `dias_vencido_maximo` y `facturas` |
| `clientes[].facturas` | Lista con `factura`, `mov_id`, `fecha_emision`, `vencimiento`, `dias_vencido`, `antiguedad` y `saldo` |

Los clientes vienen ordenados del más atrasado al menos, y las facturas de cada uno por fecha de
vencimiento. Las de más de 60 días se marcan en rojo.

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
no queda el contenedor detenido. Lo que va después del nombre del servicio son los argumentos del
programa:

```bash
docker compose run --rm notificacion-clientes --vendedores
docker compose run --rm notificacion-clientes --previsualizar
docker compose run --rm notificacion-clientes --vendedores --previsualizar
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

Las plantillas, el logo y `appsettings.json` se copian al directorio de salida. En el servidor hay
que crear el `appsettings.json` con los valores del ambiente —o definir las variables de entorno
equivalentes—, ya que no viene en el repositorio.

Cada proceso corre una vez al día mediante una tarea programada (o `docker compose run` desde cron),
en horarios distintos: el de clientes después de la emisión de facturas, y el de vendedores cuando
convenga que revisen su cartera.

`deploy/` trae el timer de systemd y el `run.sh` de la corrida de clientes; ver
[deploy/DESPLIEGUE.md](deploy/DESPLIEGUE.md). Para agendar también la de vendedores hace falta una
segunda unidad que pase `--vendedores` al contenedor y use un `--name` distinto, para que las dos
corridas no se bloqueen entre sí por el control de solapamiento.
