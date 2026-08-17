# Plan: acuse y recordatorio

Detectar qué clientes contestaron el correo de facturas, dejarlo registrado, y reenviar una sola
vez a los que no respondieron.

> Versión publicada (misma información, con formato):
> <https://claude.ai/code/artifact/ee627384-03f2-43b7-87ba-2cd34bc7e166>
> El HTML fuente está en `docs/plan-acuse-recordatorio.html`.

Plan sobre el commit `4efcaf8`. Esfuerzo estimado: ~3 días.

## Decisiones tomadas

| Tema | Decisión | Por qué |
|---|---|---|
| Persistencia | Tablas propias en SQL Server, esquema `notif` | Consultable desde el ERP y sobrevive al contenedor. La bitácora de hoy es texto plano y no se puede consultar. |
| Detección | IMAP sobre el buzón remitente (`cxc@litoprocess.com`) | Cruce por los headers `In-Reply-To` / `References`: es exacto y no depende de heurísticas sobre el asunto. |
| Política | Un solo recordatorio | A los N días sin respuesta se reenvía una vez; si tampoco contestan, se cierra como `SIN_RESPUESTA` y se revisa a mano. |

## Ciclo de vida de un envío

Cada correo que sale queda como un renglón en `notif.Envio` y se mueve por estos estados:

```
ENVIADO ──(N días)──┬── CONTESTADO
                    └── RECORDADO ──(N días)──┬── CONTESTADO
                                              └── SIN_RESPUESTA
```

El recordatorio nace como un renglón nuevo ligado al original (`IdEnvioOriginal`), así que la
evidencia de los dos intentos queda separada pero relacionada.

Un envío que ni siquiera salió del SMTP queda en `FALLIDO` y nunca entra al ciclo de recordatorios:
eso ya lo reporta la bitácora del día y se atiende a mano.

## Antes de empezar: tres cosas que rompen el seguimiento

### 1. La consulta trae 21 días, no el día — BLOQUEANTE

`DAO/FacturaDAO.cs:37` filtra `FechaEmision >= DATEADD(DAY, -21, ...)` y tiene comentada la línea
del día. Hoy eso solo significa que cada corrida reenvía las facturas de las últimas tres semanas.
**Con seguimiento encima, cada corrida abriría un envío nuevo por las mismas facturas** y a los N
días dispararía un recordatorio por cada uno.

Hay que volver al filtro del día —o dejar el rango como llave de configuración— y hacer que el
registro sea idempotente por cliente + MovID.

### 2. En modo prueba nadie puede contestar — BLOQUEANTE

Con `Smtp:ModoPrueba` activo todo se redirige al buzón de pruebas, así que el cliente real nunca ve
el correo. Si el seguimiento no distingue esos envíos, **cada ensayo genera un pendiente que a los
N días manda un recordatorio de verdad**.

Por eso `notif.Envio` guarda la columna `ModoPrueba` y la consulta de pendientes la excluye.

### 3. Gmail puede reescribir el Message-Id — VERIFICAR

Todo el cruce depende de que el `Message-Id` que ponemos sea el mismo que el cliente ve y
referencia al contestar. `smtp.gmail.com` puede reemplazarlo al aceptar el mensaje; si eso pasa, el
`In-Reply-To` de la respuesta apunta a un id que no tenemos guardado y no casa nada.

Se verifica en la Fase 0 antes de escribir código de producción, y el diseño trae plan B por si
resulta que sí lo reescribe.

---

## Fase 0 — Spike: ¿sobrevive nuestro Message-Id? (~1 h)

Sin escribir código de producción. Se manda un correo por el flujo actual a una cuenta propia, se
contesta desde ahí, y se comparan tres valores: el `Message-Id` que MimeKit generó, el que aparece
en el correo recibido, y el `In-Reply-To` de la respuesta.

- **Si coinciden** — el cruce por `Message-Id` basta y el resto del plan corre tal cual.
- **Si Gmail lo reescribió** — se agrega un paso: después de enviar, se busca el mensaje en
  `[Gmail]/Sent Mail` por un header propio `X-Notificacion-Id` (un GUID que sí controlamos) y se
  guarda el `Message-Id` real que quedó. Son ~30 líneas más en `CorreoService` y una conexión IMAP
  extra al final de la corrida.

El header `X-Notificacion-Id` se agrega en ambos casos: no cuesta nada y es lo que permite
reconciliar si algo se descuadra.

## Fase 1 — Persistencia: el esquema `notif` (~4 h)

Un script idempotente que el DBA pueda leer de corrido y correr dos veces sin daño. Va en el repo y
se documenta en `DESPLIEGUE.md` como paso previo a la primera corrida.

```sql
-- deploy/sql/001-seguimiento.sql
IF SCHEMA_ID('notif') IS NULL EXEC('CREATE SCHEMA notif');
GO

IF OBJECT_ID('notif.Envio') IS NULL
CREATE TABLE notif.Envio (
    IdEnvio            INT IDENTITY(1,1) CONSTRAINT PK_notif_Envio PRIMARY KEY,
    Cliente            VARCHAR(20)      NOT NULL,
    RazonSocial        NVARCHAR(255)    NULL,
    -- Sin <>, tal como lo expone MimeKit. 255 cabe en un indice unico (limite 900 bytes).
    MessageId          VARCHAR(255)     NOT NULL,
    -- Viaja como header X-Notificacion-Id: es el id que SI controlamos nosotros.
    Token              UNIQUEIDENTIFIER NOT NULL,
    -- NULL = envio original. Si trae valor, es el recordatorio de ese envio.
    IdEnvioOriginal    INT              NULL REFERENCES notif.Envio(IdEnvio),
    Intento            TINYINT          NOT NULL DEFAULT 1,
    Asunto             NVARCHAR(500)    NOT NULL,
    Destinatarios      NVARCHAR(2000)   NOT NULL,
    -- Los envios en modo prueba jamas llegaron al cliente: no generan recordatorio.
    ModoPrueba         BIT              NOT NULL,
    FechaEnvio         DATETIME2(0)     NOT NULL,
    Estado             VARCHAR(16)      NOT NULL,
    Error              NVARCHAR(1000)   NULL,
    FechaRespuesta     DATETIME2(0)     NULL,
    RespondioEmail     NVARCHAR(320)    NULL,
    RespuestaMessageId VARCHAR(255)     NULL,
    RespuestaAsunto    NVARCHAR(500)    NULL,
    CONSTRAINT UQ_notif_Envio_MessageId UNIQUE (MessageId),
    CONSTRAINT CK_notif_Envio_Estado CHECK (Estado IN
        ('ENVIADO','FALLIDO','CONTESTADO','RECORDADO','SIN_RESPUESTA'))
);

-- Cubre la consulta de pendientes, que es la que corre todos los dias.
CREATE INDEX IX_notif_Envio_Pendientes ON notif.Envio
    (Estado, ModoPrueba, FechaEnvio) INCLUDE (Cliente, Intento, MessageId);

-- Que facturas iban en cada correo: sirve para reenviar los mismos adjuntos
-- y para no volver a abrir un envio por una factura ya notificada.
CREATE TABLE notif.EnvioFactura (
    IdEnvio INT           NOT NULL REFERENCES notif.Envio(IdEnvio),
    MovID   VARCHAR(50)   NOT NULL,
    Total   DECIMAL(18,4) NOT NULL,
    Moneda  VARCHAR(3)    NOT NULL,
    CONSTRAINT PK_notif_EnvioFactura PRIMARY KEY (IdEnvio, MovID)
);
```

Del lado de C#, un DAO con Dapper al estilo del que ya existe: `Registrar`, `ObtenerPendientes`,
`ObtenerParaConciliar`, `MarcarContestado`, `MarcarRecordado`, `MarcarSinRespuesta` y
`YaNotificada(cliente, movId)`.

| | Archivo | Qué |
|---|---|---|
| nuevo | `deploy/sql/001-seguimiento.sql` | esquema, idempotente |
| nuevo | `Entity/EnvioNotificacion.cs` | el renglón y su enum de estado |
| nuevo | `DAO/SeguimientoDAO.cs` | Dapper, mismo estilo que `FacturaDAO` |
| cambia | `DAO/FacturaDAO.cs` | volver al filtro del día |

## Fase 2 — Capturar lo que se envió (~3 h)

Hoy `CorreoService` manda el correo y devuelve un `ResultadoEnvio` que no conserva nada con qué
identificar el mensaje después. Se le agregan tres datos y el `Program` los persiste.

```csharp
// CorreoService.ArmarMensaje — fijamos nosotros el id, no lo dejamos al azar
var token = Guid.NewGuid();
mensaje.MessageId = MimeUtils.GenerateMessageId(_settings.RemitenteEmail.Split('@')[1]);
mensaje.Headers.Add("X-Notificacion-Id", token.ToString("N"));
```

`ResultadoEnvio` gana `MessageId`, `Token` y `Asunto`; el registro en base ocurre después de
`Enviar`, dentro del mismo `try` que ya escribe la bitácora, para que un fallo al guardar no deje
correos enviados sin rastro.

**Idempotencia.** Antes de armar el correo se consulta `YaNotificada(cliente, movId)`: si esa
factura ya salió en un envío que no está `FALLIDO`, no se vuelve a incluir. Es la red de seguridad
contra una doble corrida del timer o un rango de fechas mal puesto.

| | Archivo | Qué |
|---|---|---|
| cambia | `Services/CorreoService.cs` | Message-Id propio, header de token, `ResultadoEnvio` ampliado |
| cambia | `Program.cs` | registrar el envío tras enviar |

## Fase 3 — Leer el buzón y cruzar respuestas (~6 h)

MailKit ya trae cliente IMAP, así que no hay paquete nuevo que agregar. El buzón se abre **en solo
lectura**: cobranza usa ese mismo inbox y un proceso automático no debería marcarle correos como
leídos.

```csharp
// Services/RespuestaService.cs
using var imap = new ImapClient();
await imap.ConnectAsync(_cfg.Host, _cfg.Puerto, SecureSocketOptions.SslOnConnect, ct);
await imap.AuthenticateAsync(_cfg.Usuario, _cfg.Password, ct);

var carpeta = await imap.GetFolderAsync(_cfg.Carpeta, ct);   // INBOX
await carpeta.OpenAsync(FolderAccess.ReadOnly, ct);          // no marca leidos

// Solo desde el pendiente mas viejo: no recorremos el buzon entero.
var uids = await carpeta.SearchAsync(SearchQuery.DeliveredAfter(desde), ct);

// Envelope trae From/Subject/Date/InReplyTo; References, la cadena del hilo.
// Los headers extra sirven para descartar autorrespuestas. Nunca se baja el cuerpo.
var resumenes = await carpeta.FetchAsync(uids,
    MessageSummaryItems.Envelope | MessageSummaryItems.References,
    new[] { "Auto-Submitted", "Precedence", "X-Autoreply" }, ct);
```

**Cómo casa cada respuesta**, en orden:

1. **Por hilo.** El `In-Reply-To`, o cualquier id de `References`, aparece entre los `MessageId`
   pendientes. Es exacto y es el camino normal.
2. **Por remitente.** Si lo anterior no dio nada: el `From` de la respuesta es uno de los
   destinatarios de un envío pendiente, la fecha es posterior al envío, y el asunto —quitando
   `Re:`, `RE:`, `RV:`, `Fwd:`— coincide con el original. Cubre a los clientes cuyo cliente de
   correo no conserva los headers del hilo.

**Qué NO cuenta como respuesta.** Sin este filtro, un "estoy fuera de la oficina" cerraría el
pendiente y el cliente nunca recibiría el recordatorio:

- `Auto-Submitted` con cualquier valor distinto de `no`, o `Precedence: bulk` / `auto_reply`, o
  presencia de `X-Autoreply`.
- Remitentes `mailer-daemon@` y `postmaster@`: eso es un rebote, no una respuesta. El envío se
  marca `FALLIDO` con el motivo, para que cobranza corrija el correo en el CRM.

La conciliación guarda `RespuestaMessageId`, así que volver a correr el comando no reprocesa lo ya
visto.

| | Archivo | Qué |
|---|---|---|
| nuevo | `Services/RespuestaService.cs` | IMAP, cruce y descartes |
| nuevo | `Configuracion/ImapSettings.cs` | cae a las credenciales de `Smtp` si no se define |
| cambia | `Configuracion/AppSettings.cs` | secciones `Imap` y `Seguimiento` |

## Fase 4 — Bitácora del seguimiento (~2 h)

La corrida de seguimiento deja su propio archivo, con el mismo formato y criterio que el de envíos:
`Logs/seguimiento-2026-08-17_10-00-12.log`. Tres bloques —respuestas detectadas, recordatorios
enviados, cerrados sin respuesta— y el mismo comportamiento ante un error fatal: se escribe de
todos modos con el motivo y sale con código 1.

```
[003] CONTESTADO | Cliente C000123 - COMERCIALIZADORA DEL BAJIO S.A. DE C.V.
      Envio original : 2026-08-12 19:04:11 (intento 1)
      Respondio      : Laura Mendez <lmendez@bajio.com> el 2026-08-13 09:22
      Asunto         : Re: Facturas del dia - COMERCIALIZADORA DEL BAJIO
      Cruce          : In-Reply-To

[007] RECORDATORIO | Cliente C000456 - IMPRESOS DEL NORTE S.A. DE C.V.
      Envio original : 2026-08-12 19:04:33 (5 dias sin respuesta)
      Destinatarios  : compras@impresosnorte.com
      Adjuntos       : 2 facturas re-enviadas (FAC-88131, FAC-88140)
```

La base es la bitácora consultable; el archivo es la evidencia que se lee sin abrir SQL, igual que
hoy.

| | Archivo | Qué |
|---|---|---|
| cambia | `Services/BitacoraService.cs` | método `EscribirSeguimiento` |
| cambia | `Services/ReporteConsola.cs` | salida del comando en pantalla |

## Fase 5 — El recordatorio (~5 h)

Se seleccionan los envíos en `ENVIADO`, con `ModoPrueba = 0`, `Intento = 1` y más de `DiasEspera`
días encima. Para cada uno se vuelven a descargar del API los XML y PDF de los `MovID` guardados en
`notif.EnvioFactura` —el cliente probablemente nunca los abrió, mandarlo sin adjuntos lo obligaría
a buscar el correo anterior— y se arma un mensaje nuevo **dentro del hilo original**:

```csharp
mensaje.Subject = $"Re: {envio.Asunto}";
mensaje.InReplyTo = envio.MessageId;
mensaje.References.Add(envio.MessageId);
```

Así le llega al cliente como continuación de la conversación y no como un correo suelto —y si
contesta, su `In-Reply-To` cae en la misma cadena, que el cruce de la Fase 3 ya sabe leer.

La plantilla es nueva porque el texto es otro: aquí se le recuerda que las facturas siguen sin
acuse. Recibe dos variables extra, `dias_transcurridos` y `fecha_envio_original`, sobre el mismo
modelo que ya arma `PlantillaService`.

**Cierre del ciclo.** El original pasa a `RECORDADO` y el recordatorio nace como renglón nuevo con
`Intento = 2` e `IdEnvioOriginal`. Como la consulta de pendientes exige `Intento = 1`, no existe un
tercer intento por construcción. Pasados otros `DiasEspera`, ambos renglones se cierran en
`SIN_RESPUESTA` y salen en la bitácora para revisión manual.

| | Archivo | Qué |
|---|---|---|
| nuevo | `Plantillas/recordatorio-cliente.html` | mismo diseño, otro texto |
| nuevo | `Services/RecordatorioService.cs` | selección, re-descarga y reenvío |
| cambia | `Services/PlantillaService.cs` | plantilla como parámetro, no fija |
| cambia | `Services/CorreoService.cs` | envío dentro de un hilo existente |

## Fase 6 — Comandos y agenda (~3 h)

`Program.cs` es hoy un composition root de una sola pasada. Pasa a despachar por argumento, y el
armado de dependencias se mueve a una clase aparte para que no crezca:

| Comando | Qué hace | Agenda |
|---|---|---|
| (sin args) | Envía las facturas del día y registra cada envío. Es el comportamiento actual más la Fase 2. | L–V 19:00 |
| `--seguimiento` | Concilia respuestas y manda los recordatorios que toquen, en una sola pasada. | L–V 10:00 |
| `--revisar-respuestas` | Solo la conciliación. Para correr a mano y ver qué detecta sin mandar nada. | manual |
| `--recordatorios` | Solo los reenvíos. Útil para reintentar si el SMTP falló a media pasada. | manual |
| `--previsualizar` | Genera el HTML en disco sin enviar. Ya existe comentado en `Program.cs:37`; se descomenta. | manual |

**Dos detalles del despliegue que hay que tocar.** `run.sh` pasa los argumentos al contenedor con
`"$@"`; y el `--name` fijo, que hoy es el anti-solapamiento, tiene que variar por comando
(`notificacion-clientes-seguimiento`) porque si no, la corrida de las 10:00 se bloquearía contra un
contenedor de envío que siguiera vivo, y viceversa.

| | Archivo | Qué |
|---|---|---|
| cambia | `Program.cs` | despacho por argumento |
| nuevo | `Comandos/` | una clase por comando |
| cambia | `deploy/run.sh` | `"$@"` y nombre de contenedor por comando |
| nuevo | `deploy/notificacion-seguimiento.service` | y su `.timer`, L–V 10:00 |

## Fase 7 — Configuración y documentación (~2 h)

```jsonc
"Imap": {
  "Host": "imap.gmail.com",
  "Puerto": 993,
  // Vacios = se usan las credenciales de Smtp. Es la misma cuenta.
  "Usuario": "",
  "Password": "",
  "Carpeta": "INBOX"
},
"Seguimiento": {
  "Habilitado": true,
  "DiasEspera": 3,
  "SoloDiasHabiles": true
}
```

Las mismas llaves como variables de entorno (`Imap__Host`, `Seguimiento__DiasEspera`), igual que
todo lo demás. Y en el README, una sección de seguimiento con el ciclo de estados, el script SQL
como paso previo y los comandos nuevos.

| | Archivo | Qué |
|---|---|---|
| cambia | `appsettings.example.json` | secciones `Imap` y `Seguimiento` |
| cambia | `.env.example` | equivalentes con `__` |
| cambia | `README.md` | sección de seguimiento |
| cambia | `deploy/DESPLIEGUE.md` | script SQL y segundo timer |

---

## Riesgos

| Riesgo | Consecuencia | Mitigación |
|---|---|---|
| Message-Id reescrito | Ninguna respuesta casa por hilo y todos los clientes reciben recordatorio. | Fase 0 lo detecta antes de escribir código; el cruce por remitente + asunto lo cubre igual. |
| Falso positivo | Un autorreply cierra el pendiente y el cliente nunca recibe el recordatorio. | Descarte por `Auto-Submitted`, `Precedence` y `X-Autoreply`. Falla del lado seguro: ante la duda, insistir. |
| Recordatorio indebido | Se le insiste a un cliente que sí contestó, por otro canal o a otro buzón. | Un solo recordatorio por envío, por construcción de la consulta. El daño máximo es un correo de más. |
| Contraseña IMAP | Una credencial más que rota y que da acceso de lectura a todo el buzón de cobranza. | Misma contraseña de aplicación del SMTP, solo por variable de entorno, y la carpeta se abre en `ReadOnly`. |
| Permisos en la BD | El usuario de la aplicación hoy solo lee; el esquema `notif` necesita escritura. | Acordarlo con el DBA antes de la Fase 1. Es el único requisito externo del plan. |

## Orden de trabajo

Las fases van en orden y cada una deja el proyecto en un estado que corre. El primer corte útil es
la Fase 2: aun sin IMAP, ya queda registro consultable de todo lo que se envía. El seguimiento
completo entra vivo en la Fase 5.

Sugerencia para la puesta en producción: correr las Fases 1 a 3 con `Smtp:ModoPrueba` en `true`
durante una semana. El registro y la conciliación funcionan igual, los recordatorios no salen
porque la consulta excluye los envíos de prueba, y se alcanza a ver si el cruce de respuestas
acierta antes de que le llegue nada a un cliente.
