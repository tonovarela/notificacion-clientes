# Despliegue en producción con schedule

Guía para publicar la imagen en Docker Hub y ejecutarla en un servidor de producción de forma
programada.

El punto que define todo lo demás: **esta aplicación es un proceso batch de una sola pasada**.
Consulta las facturas del día, manda los correos, escribe la bitácora y termina con código de
salida `0` o `1`. No es un servicio que se queda arriba. Por eso:

- **No** se usa `restart: always`. El proceso termina y Docker lo reiniciaría en bucle, mandando
  correos repetidos a los clientes.
- **No** se mete un scheduler dentro del contenedor. Quien agenda es el servidor.
- El schedule vive en el crontab del host y cada disparo levanta un contenedor nuevo con
  `docker run --rm`.

Archivos de esta carpeta:

| Archivo | Destino en el servidor | Qué hace |
|---|---|---|
| `run.sh` | `/home/docker/notificacion-clientes/run.sh` | Levanta el contenedor, detecta solapamiento, corta la corrida colgada y alerta si falla. Recibe el proceso como argumento. |
| `sql/001-seguimiento.sql` | se corre en SQL Server | Crea la base `CorreosCXC` y el esquema `notif` del seguimiento. Idempotente. Sólo hace falta si se enciende `Seguimiento__Habilitado`. |

El schedule no es un archivo del repositorio: son cuatro líneas en el crontab del usuario
`notificaciones`, que se transcriben en el punto 4.

### Los cuatro procesos

El mismo ejecutable atiende cuatro corridas distintas y **el argumento es obligatorio**: sin él no
manda ningún correo y termina con código `64`, imprimiendo el uso.

| Proceso | Argumento | Cuándo | Qué manda | Bitácora |
|---|---|---|---|---|
| Clientes | `--clientes` | Lun a vie 18:00 | Las facturas del día a cada cliente, con XML y PDF | `envios-*.log` |
| Vendedores | `--vendedores` | Mar y vie 09:00 | La cartera sin ingresar a revisión a cada vendedor | `revision-vendedores-*.log` |
| Cobranza | `--cobranza` | Mar y vie 09:00 | El estado de cuenta vencido a cada cliente | `cobranza-*.log` |
| Respuestas | `--respuestas` | Lun a vie 10:00 | Nada: lee el buzón y marca estados | `respuestas-*.log` |

`--cobranza` manda dos poblaciones según el día: el martes, facturas que nunca se han notificado;
el viernes, las ya notificadas cuyo envío sigue sin contestar. El corte lo hace la consulta,
cruzando la antigüedad de saldos contra `CorreosCXC.notif`. Con `--recordatorio` o `--primer-aviso`
se fuerza la población en una corrida manual.

Por eso el recordatorio necesita `Seguimiento__Habilitado=true`: se arma con lo que quedó
registrado en `notif.EnvioFactura`, así que sin registro su población sale vacía. El proceso lo
avisa en el log antes de no mandar nada.

`--respuestas` es lo que hace útil al recordatorio: abre el buzón por IMAP en sólo lectura, cruza
los correos contra los `Message-Id` de los envíos abiertos y marca `CONTESTADO` o `FALLIDO` en
`notif.Envio`. **No manda ningún correo.** Si deja de correr entre el martes y el viernes, el
recordatorio le llega también a quien ya había contestado.

`run.sh` recibe el proceso sin los guiones (`run.sh clientes`), valida que sea uno de los cuatro
válidos y sale con código 64 si no lo es. Cada proceso corre con su propio `--name`, por eso
cobranza y vendedores pueden compartir las 09:00 del martes y viernes sin bloquearse.

---

## 1. Publicar la imagen en Docker Hub

### Construcción multi-arquitectura

Si compilas en Mac con chip Apple (ARM64) y el servidor es x86_64, una imagen construida con
`docker build` normal **no arranca** en el servidor: falla con `exec format error`. Hay que
construir para las dos arquitecturas:

```bash
# Una sola vez por equipo
docker buildx create --use --name lito-builder

# Cada versión
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  -t tonovarela/notificacion-clientes:1.0.0 \
  -t tonovarela/notificacion-clientes:latest \
  --push .
```

### Imágenes de prueba

El `Dockerfile` deja `Smtp__ModoPrueba` y `Smtp__ModoPruebaVendedores` en `false` por omisión, y
expone el argumento `MODO_PRUEBA` para invertirlos sin editar el archivo:

```bash
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  --build-arg MODO_PRUEBA=true \
  -t tonovarela/notificacion-clientes:1.0.2 \
  --push .
```

Una imagen así **no le manda nada a ningún cliente**: todo se redirige al buzón de pruebas. Dos
reglas al usarla:

- **Nunca la etiquetes `latest`.** Es la etiqueta que alguien toma cuando tiene prisa, y una
  corrida en modo prueba termina con código `0`: se ve idéntica a una exitosa mientras ningún
  cliente recibe su factura.
- **Revisa la bitácora, no el código de salida.** El encabezado dice `Modo prueba : SI/NO`; es la
  única señal que distingue las dos situaciones sin abrir la imagen.

Para pasar a producción sin reconstruir, basta con `Smtp__ModoPrueba=false` en el `.env` del
servidor: el `--env-file` pisa lo que trae la imagen.

### Versionado

Etiqueta siempre con versión semántica **además** de `latest`. En producción el schedule apunta a
la versión fija (`:1.0.0`), nunca a `:latest`.

El motivo es concreto: si el schedule usa `latest` y alguien sube una imagen con un error un
martes, el proceso de facturación del miércoles falla sin que nadie haya tocado el servidor, y el
diagnóstico se vuelve un misterio. Con versión fija, el servidor sólo cambia cuando alguien lo
decide.

En `run.sh` la versión está en la variable `IMAGEN`, no en el crontab. Así el rollback es cambiar
una línea y el schedule nunca se toca.

### Repositorio privado

La imagen contiene la plantilla del correo, el logo y la lógica de negocio. Márcala **privada** en
Docker Hub. En el servidor, `docker login` una sola vez con el usuario que corre el schedule; las
credenciales quedan en su `~/.docker/config.json`.

### Verificar que no se coló ningún secreto

El `.dockerignore` ya excluye `appsettings.json`, `appsettings.*.json` y `.env`. Confírmalo
después de construir, antes de publicar:

```bash
docker run --rm --entrypoint sh tonovarela/notificacion-clientes:1.0.0 \
  -c "ls -la /app | grep -i appsettings; id"
```

Debe listar únicamente `appsettings.example.json`. El `id` te devuelve el usuario no-root con el
que corre la aplicación (en las imágenes de .NET 8 es `app`, **UID 1654**): anótalo, se usa en el
paso 3.

> Si agregas la carpeta `deploy/` al `.dockerignore` evitas que estos archivos de despliegue viajen
> dentro del contexto de construcción. No es un problema de seguridad, sólo de higiene.

---

## 2. Preparar el servidor

El despliegue vive en `/home/docker/<nombre-del-contenedor>`, que es la convención del servidor:
una carpeta por aplicación, con todo lo suyo dentro. Aquí el contenedor se llama
`notificacion-clientes`, así que la carpeta es `/home/docker/notificacion-clientes`.

```
/home/docker/notificacion-clientes/
├── .env            # credenciales reales — chmod 600
├── run.sh          # wrapper que invoca el schedule — chmod 750
└── logs/           # bitácoras, sobreviven al contenedor
```

Todo cuelga de ahí: la configuración, el script y **las bitácoras**. Fuera de esa carpeta no hay
nada de esta aplicación salvo las dos líneas del crontab.

```bash
sudo useradd --system --shell /usr/sbin/nologin notificaciones
sudo usermod -aG docker notificaciones

sudo mkdir -p /home/docker/notificacion-clientes/logs
sudo cp deploy/run.sh /home/docker/notificacion-clientes/
sudo chmod 750 /home/docker/notificacion-clientes/run.sh
sudo chown -R notificaciones:notificaciones /home/docker/notificacion-clientes

# /home/docker suele existir ya con otros despliegues; el usuario del schedule sólo necesita
# poder atravesarlo para llegar a su carpeta.
sudo chmod o+x /home/docker
```

`run.sh` deduce su carpeta base de dónde está instalado, así que si mañana el despliegue se mueve
o la carpeta se llama distinto, el `.env` y las bitácoras lo siguen sin editar nada. Lo único que
hay que actualizar en ese caso son las dos rutas del crontab.

### Configuración: sólo variables de entorno

`AppSettings.Cargar()` lee `appsettings.json` como **opcional** y las variables de entorno lo
pisan. En el servidor **no copies `appsettings.json`**: duplicar el lugar donde viven las
contraseñas es cómo se termina con dos configuraciones que no coinciden.

Copia `.env.example` del repositorio a `/home/docker/notificacion-clientes/.env` y ajústalo:

```bash
sudo install -o notificaciones -g notificaciones -m 600 .env.example /home/docker/notificacion-clientes/.env
sudo -u notificaciones nano /home/docker/notificacion-clientes/.env
```

Valores que **obligatoriamente** cambian respecto al ejemplo:

| Variable | Producción | Por qué |
|---|---|---|
| `Smtp__ModoPrueba` | `false` | Con `true` **ningún cliente recibe nada**: todo se redirige a `Smtp__CorreoPrueba`. Es el interruptor más delicado del sistema. |
| `Smtp__ModoPruebaVendedores` | `false` | El mismo interruptor, pero solo para la corrida `--vendedores`; se redirige a `Smtp__CorreoPruebaVendedores`. Si se omite, hereda `Smtp__ModoPrueba`, así que sirve para dejar clientes en producción y vendedores en prueba. |
| `Smtp__Password` | contraseña de aplicación real | En Gmail no es la contraseña del buzón. |
| `ConnectionStrings__SqlServer` | cadena real | El usuario necesita permisos en `Lito` y `LitoCRM`. |
| `Bitacora__Ruta` | `/app/Logs` | Ruta absoluta: `Path.Combine` la respeta tal cual y cae en el volumen montado. |
| `TZ` | `America/Mexico_City` | Afecta las fechas impresas en el correo. |
| `Facturas__DiasAtras` | `0` | Días hacia atrás de la consulta de facturas. `0` = sólo las de hoy. Ampliarlo hace que una misma factura abra un envío nuevo cada día. |
| `Seguimiento__Habilitado` | `true` para cobranza | Sin esto no se registra nada, y **el recordatorio del viernes se queda sin población**: no sale ningún correo. Exige el script SQL y el permiso de escritura. |
| `Seguimiento__DiasVentanaMaxima` | `30` | Tope duro de cuántos días atrás se lee el buzón. Nada cierra los envíos sin contestar, así que sin él la búsqueda IMAP crece sin límite. |
| `Imap__Usuario` / `Imap__Password` | vacíos | Vacíos = se usan los de `Smtp`. Es la misma cuenta; duplicar la credencial es cómo se desincronizan. |

### Base de datos: el esquema del seguimiento

> **Hay que volver a correr el script.** Se agregó la tabla `notif.EnvioRecordatorio`, que guarda
> qué recordatorios ha recibido cada envío. El script es idempotente: la crea, migra a ella lo que
> hubiera en la columna `RecordatorioMessageId` de una versión intermedia, y luego tira esa
> columna. **Sin correrlo, `--cobranza` y `--respuestas` truenan.**

Hace falta para `--cobranza` y `--respuestas`. Sin esto, `Seguimiento__Habilitado` tiene que
quedar en `false`: la aplicación manda los correos igual que siempre, no registra nada, y **el
recordatorio del viernes se queda sin población** — no sale ningún correo.

El seguimiento **no vive en `Lito`**: se crea una base aparte, `CorreosCXC`, con el esquema
`notif` dentro. Son datos de esta aplicación, no del ERP, y separarlos permite darle escritura al
usuario ahí sin tocar los permisos que tiene sobre `Lito` y `LitoCRM`, donde sólo lee.

El script se cambia solo a su base, así que se corre **sin** `-d`:

```bash
sqlcmd -S SERVIDOR -i deploy/sql/001-seguimiento.sql
```

Es idempotente: cada objeto va detrás de su propia guarda, así que correrlo dos veces no daña lo
que ya exista.

**Vuélvelo a correr en cada actualización de la imagen**, no sólo la primera vez. El script no
sólo crea: también agrega columnas que versiones nuevas necesitan y recrea los índices cuya
definición cambió. Saltarse este paso produce el error más confuso posible — la aplicación
conecta bien, encuentra la tabla, y truena con *"El nombre de columna 'X' no es válido"*
**después** de haber mandado los correos.

Los índices se tiran y se recrean a propósito. Un `IF NOT EXISTS` dejaría el índice viejo intacto
al cambiar su definición, y el script parecería haber corrido bien sin haber hecho nada.

**El requisito que hay que acordar con el DBA** es el único externo de todo el módulo: la cadena
de conexión sigue apuntando a `Lito` —de ahí salen las facturas— y la aplicación llega al
seguimiento por nombre de tres partes (`CorreosCXC.notif.Envio`). Para que eso funcione, su login
necesita un usuario en `CorreosCXC` con lectura y escritura. Al final del script hay un bloque
comentado con las tres líneas que lo hacen; ajusta el nombre del login antes de descomentarlo.

Si falta el permiso, la corrida truena al registrar el primer envío —después de haber mandado los
correos.

### Primeras corridas en modo prueba

Deja `Smtp__ModoPrueba=true` durante una o dos corridas programadas completas. Así validas desde
el servidor real la conectividad a SQL Server, al API de facturas y al SMTP, revisando los correos
en el buzón de pruebas, sin riesgo de mandar CFDI equivocados a clientes.

Hay dos señales de la bitácora que **sólo se pueden comprobar contra el servidor real**, y ésta es
la corrida donde hay que mirarlas:

- **`Aviso rebote: SI/NO`**, en el encabezado de `cobranza-*.log`. Dice si al servidor de salida se
  le pudo pedir que avise cuando un correo no se entregue —la extensión DSN del RFC 3461—. Con `SI`
  el correo sale sellado con nuestro token y el rebote que llegue después se reconoce de forma
  exacta. Con `NO` el envío es igual de válido, pero el rebote habrá que casarlo por el hilo o por
  la dirección, que son caminos aproximados; el día que un rebote no aparezca por ningún lado, esta
  línea es lo primero que lo explica.
- **`Direcciones con error : N`**, en el resumen de las tres bitácoras de envío. Cuenta los
  contactos que no recibieron el correo. Las direcciones del CRM se revisan **también en modo
  prueba** —el correo se redirige al buzón de pruebas, pero un dato mal capturado sigue siendo un
  dato malo—, así que este número ya es real desde la primera corrida. Si sale alto no es una falla
  del despliegue, es cartera de contactos por depurar; el punto es enterarse aquí y no después del
  primer reclamo.

---

## 3. Permisos del volumen de bitácoras

El contenedor corre como `USER $APP_UID` (no-root, UID 1654). El `chown` del Dockerfile aplica a
`/app/Logs` **dentro de la imagen**, pero al montar `./logs` del host ese chown queda tapado: manda
el dueño del directorio del host.

Si `/home/docker/notificacion-clientes/logs` pertenece a `root`, la aplicación **no puede escribir la
bitácora**, que es justamente la evidencia que necesitas cuando algo falla:

```bash
sudo chown -R 1654:1654 /home/docker/notificacion-clientes/logs
```

Ajusta el UID a lo que devolvió el `id` del paso 1.

---

## 4. Schedule con cron

El schedule vive en el crontab del usuario `notificaciones`, **nunca en el de root**: la corrida
sólo necesita hablar con el socket de Docker, y ese usuario ya está en el grupo `docker`.

```bash
sudo -u notificaciones crontab -e
```

```cron
CRON_TZ=America/Mexico_City

# Facturas del día a cada cliente — lunes a viernes 18:00
0 18 * * 1-5 /home/docker/notificacion-clientes/run.sh clientes   >> /home/docker/notificacion-clientes/logs/cron.log 2>&1

# Estado de cuenta vencido a cada cliente — martes y viernes 09:00
# El martes manda lo nunca notificado; el viernes insiste sobre lo que sigue sin contestar.
# Las dos poblaciones las separa la consulta, y ambas requieren Seguimiento__Habilitado=true.
0  9 * * 2,5 /home/docker/notificacion-clientes/run.sh cobranza   >> /home/docker/notificacion-clientes/logs/cron.log 2>&1

# Cartera sin ingresar a revisión a cada vendedor — martes y viernes 09:00
0  9 * * 2,5 /home/docker/notificacion-clientes/run.sh vendedores >> /home/docker/notificacion-clientes/logs/cron.log 2>&1

# Lectura del buzón y marcado de estados — lunes a viernes 10:00. No manda correo.
# Es lo que evita que el recordatorio del viernes le insista a quien ya contestó.
0 10 * * 1-5 /home/docker/notificacion-clientes/run.sh respuestas >> /home/docker/notificacion-clientes/logs/cron.log 2>&1
```

La hora de `respuestas` no es arbitraria: va **después** de que la gente abrió su correo por la
mañana y **antes** del envío de las 18:00. Correrlo de madrugada no vería las respuestas de la
noche anterior que todavía no habían llegado al buzón.

Cobranza y vendedores comparten la hora a propósito: son dos correos distintos, a destinatarios
distintos, y cada uno corre en su propio contenedor —`notificacion-clientes-cobranza` y
`notificacion-clientes-vendedores`—, así que el candado anti-solapamiento no los enfrenta. Lo
único que comparten es la cuenta SMTP, y dos conexiones simultáneas no son problema para ella.
Si algún día se quisieran separar, mover una a `15 9 * * 2,5` basta.

Cuatro cosas de esas líneas que no son decorativas:

- **`CRON_TZ=America/Mexico_City`** es imprescindible si el servidor está en UTC. Sin ella la
  corrida de las 18:00 caería a las 12:00 hora de México —o cruzaría de día— y la consulta filtra
  por la fecha del día, así que mandaría las facturas equivocadas. Compruébalo con `date` en el
  servidor antes de confiarte. `CRON_TZ` es de Vixie cron (Debian, Ubuntu, RHEL); si tu cron no lo
  soporta, la alternativa es correr a la hora UTC equivalente y ajustarla en cada cambio de
  horario, que es exactamente el error que esto evita.
- **La redirección `>> ... 2>&1` no es opcional.** Sin ella cron intenta mandar la salida por
  correo local, que en un servidor sin MTA se descarta en silencio: perderías el rastro de por qué
  falló una corrida. `cron.log` guarda lo que imprime `run.sh`; la bitácora detallada de cada
  corrida es otro archivo, el que escribe la aplicación.
- **La ruta va absoluta.** El `PATH` de cron es mínimo y su directorio de trabajo es el `$HOME` del
  usuario, no el del despliegue.
- **`%` hay que escaparlo.** Cron lo interpreta como fin de comando y salto de línea. No aparece en
  estas dos líneas, pero muerde en cuanto alguien intenta agregarle un `date +%F` al nombre del log.

Verificación y operación diaria:

```bash
sudo -u notificaciones crontab -l          # qué está programado
tail -f /home/docker/notificacion-clientes/logs/cron.log   # seguir la corrida en vivo
grep 'run.sh' /var/log/syslog | tail -20   # confirmar que cron sí disparó (journalctl -u cron en RHEL)

# Corrida manual ahora, con el mismo usuario que la corre en automático
sudo -u notificaciones /home/docker/notificacion-clientes/run.sh clientes

# Ver el correo de cobranza sin enviarlo. --recordatorio / --primer-aviso fuerzan
# la población, que si no se decide por el día en que se corre.
sudo -u notificaciones /home/docker/notificacion-clientes/run.sh cobranza --previsualizar --primer-aviso
```

Esa última línea es la prueba que vale: correr el script como `root` o como tú puede funcionar
mientras la corrida programada falla, porque `notificaciones` es quien tiene —o no— acceso al
socket de Docker y permiso de lectura sobre el `.env`.

> **Si la hora llega y no pasa nada**, y `cron.log` está vacío, el sospechoso es el shell del
> usuario. `notificaciones` se creó con `/usr/sbin/nologin`. Cron ejecuta sus trabajos con
> `/bin/sh` y no con el shell de login, así que en Debian, Ubuntu y RHEL de fábrica esto funciona;
> pero si alguien habilitó `pam_shells` en `/etc/pam.d/cron`, PAM rechaza el trabajo **sin dejar
> rastro en `cron.log`**, porque el trabajo nunca llega a arrancar. Se confirma en el log de cron
> (`/var/log/syslog` o `journalctl -u cron`) y se arregla agregando `/usr/sbin/nologin` a
> `/etc/shells`, sin darle shell real al usuario.

### Lo que cron no hace por ti

Cron es un disparador y nada más. Estas tres garantías las cubre `run.sh`, no el schedule, y
conviene saberlo antes de tocarlas:

| Riesgo | Quién lo cubre |
|---|---|
| Dos corridas del mismo proceso encimadas | `--name notificacion-clientes-<proceso>`: el segundo `docker run` falla con código 125 y el script lo reporta como corrida omitida, no como falla. |
| Corrida colgada en el SMTP o en el API | `timeout $TIMEOUT_CORRIDA` (30 min por omisión). Al cortar, `run.sh` borra el contenedor: si quedara vivo, su `--name` bloquearía todas las corridas siguientes. |
| Corrida perdida por servidor apagado | **Nadie.** Cron no recupera disparos perdidos. Es la razón por la que el punto 7 insiste en una alerta por ausencia: es el único mecanismo que detecta que la corrida simplemente nunca ocurrió. |

---

## 5. Qué hace `run.sh`

- **Argumento obligatorio**: `clientes` o `vendedores`. Cualquier otra cosa —incluida la ausencia
  de argumento— sale con código 64 sin levantar nada. Es a propósito: la imagen sin argumentos no
  manda correos y termina en `0`, y esa falla silenciosa pasaría por corrida exitosa.
- **`--name notificacion-clientes-<proceso>`**: es el anti-solapamiento. Si la corrida anterior de
  ese proceso sigue viva (API lenta, muchos CFDI), el nuevo `docker run` falla de inmediato con
  código 125 y no se duplican correos. El script distingue ese caso y lo reporta como corrida
  omitida, no como falla. El nombre lleva el proceso para que las dos corridas no se estorben.
- **`--rm`**: limpia el contenedor y los volúmenes anónimos que genera el `VOLUME` del Dockerfile.
  Sin esto se acumulan con cada corrida.
- **No hace `docker pull`**: actualizar es un paso de despliegue deliberado, no del schedule.
- **`--memory` / `--cpus`**: acota el proceso, que descarga XML y PDF a memoria, para que no
  compita con SQL Server si comparten servidor.
- **Verificaciones previas**: si falta el `.env` o el directorio de logs, falla con un mensaje
  claro sin levantar nada.
- **Alerta al fallar**: adjunta el encabezado de la última bitácora, que trae el motivo del error.
- **Carpeta base deducida**: `BASE` sale de la ruta donde está instalado `run.sh`, y de ahí
  cuelgan `.env` y `logs/`. Mover o renombrar el despliegue no obliga a editar el script; se puede
  forzar otra ruta exportando `BASE` antes de invocarlo.
- **Tope de duración** (`TIMEOUT_CORRIDA`, 30 min): envuelve el `docker run` en `timeout`. Al
  cortar, `timeout` mata al cliente de docker pero **no** al contenedor, así que el script lo borra
  a mano: si quedara vivo, su `--name` bloquearía todas las corridas siguientes de ese proceso y la
  falla se propagaría en cadena hasta que alguien lo notara. Si el servidor no tuviera el comando
  `timeout` —no es el caso en Linux, viene en coreutils— el script avisa y corre sin tope, porque
  perder la corrida del día por un binario ausente es peor que arriesgar una colgada.

Variables que puedes ajustar sin editar el archivo (o editando la sección de configuración):
`IMAGEN`, `BASE`, `ARCHIVO_ENV`, `DIRECTORIO_LOGS`, `TIMEOUT_CORRIDA`, `CORREO_ALERTA`,
`URL_MONITOREO`, `LIMITE_MEMORIA`, `LIMITE_CPU`.

---

## 6. Actualizar a una versión nueva

```bash
# 1. Correr el script SQL. Es idempotente y agrega lo que la versión nueva necesite.
#    Va ANTES de cambiar la imagen: una imagen nueva contra un esquema viejo truena
#    después de haber mandado los correos.
sqlcmd -S SERVIDOR -i deploy/sql/001-seguimiento.sql

# 2. Traer la imagen
docker pull tonovarela/notificacion-clientes:1.1.0

# 3. Probar en seco, sin tocar a los clientes. El argumento del final no es opcional:
#    sin él la imagen no manda un solo correo y termina en 0, que parece una corrida exitosa.
docker run --rm \
  --env-file /home/docker/notificacion-clientes/.env \
  -e Smtp__ModoPrueba=true \
  -e Smtp__ModoPruebaVendedores=true \
  -v /home/docker/notificacion-clientes/logs:/app/Logs \
  tonovarela/notificacion-clientes:1.1.0 --clientes

# 4. Si todo salió bien, actualizar la variable IMAGEN en run.sh
sudo -u notificaciones nano /home/docker/notificacion-clientes/run.sh
```

No hay que tocar el crontab: el cambio está dentro de `run.sh`.

**Rollback**: regresar la versión anterior en `IMAGEN`. El esquema no se revierte, y no hace
falta: las columnas nuevas tienen valor por omisión, así que una imagen vieja las ignora sin
enterarse.

---

## 7. Monitoreo

La aplicación ya entrega dos señales aprovechables:

1. **Código de salida**: el `catch` de `Program.cs` pone `ExitCode = 1` ante un error fatal.
   `run.sh` lo detecta y manda la alerta. Cron no hace nada con ese código —ni siquiera lo
   registra—, así que `CORREO_ALERTA` es lo único que convierte una falla en un aviso.
2. **Bitácora en disco**: se escribe **incluso cuando hay error fatal**, con el motivo incluido.
   Un archivo por corrida, `envios-YYYY-MM-DD_HH-mm-ss.log`.

Falta cubrir un tercer caso, que es el que se olvida y el más silencioso:

3. **Alerta por ausencia**. Si el servidor se apaga, Docker muere o alguien comenta la línea del
   crontab,
   no hay ningún código de salida que falle: simplemente no pasa nada, y nadie se entera hasta que
   un cliente reclama que no recibió su factura. Configura `URL_MONITOREO` en `run.sh` apuntando a
   un healthchecks.io (o Uptime Kuma en modo push) con el periodo esperado: ese servicio avisa
   cuando el ping **no llega**.

Alerta por correo: define `CORREO_ALERTA` en `run.sh`. Requiere `mailutils` o equivalente
instalado en el servidor.

Rotación de bitácoras, para que `logs/` no crezca sin límite:

```cron
0 3 1 * * find /home/docker/notificacion-clientes/logs -name '*.log' ! -name 'cron.log' -mtime +90 -delete
```

El patrón cubre los cuatro prefijos —`envios-`, `revision-vendedores-`, `cobranza-` y
`respuestas-`— y deja fuera `cron.log`, que es acumulativo y no rota por fecha.

---

## 8. Los tres riesgos a vigilar

### `Smtp__ModoPrueba`

Es un booleano en un archivo de texto que separa dos mundos completamente distintos: *nadie recibe
nada* y *todos los clientes reciben sus CFDI*.

La bitácora ya registra en qué modo corrió cada ejecución (`Modo prueba : SI/NO`). Vale la pena
revisarla después del primer despliegue en modo real, y tenerlo presente al diagnosticar un
"no llegaron los correos": lo primero que hay que descartar es que la corrida fue en modo prueba.

### Las direcciones que no reciben el correo

Un envío puede quedar en `ENVIADO` y aun así no haberle llegado a un contacto: la dirección estaba
mal capturada en el CRM, o el servidor la rechazó al entregarla. El correo sí sale para los demás
contactos del cliente —un contacto malo ya no cancela a los otros—, así que la corrida termina en
`0` y desde afuera no se ve nada raro. Lo que hay que revisar:

| Señal | Dónde | Qué significa |
|---|---|---|
| `Direcciones con error : N` | resumen de `envios-*.log`, `cobranza-*.log` y `revision-vendedores-*.log` | Cuántos contactos se quedaron sin el correo en toda la corrida. |
| `INVALIDA` | detalle del cliente | Lo capturado en el CRM no es una dirección; ni se intentó. |
| `RECHAZADA` | detalle del cliente | El servidor la rechazó con un código SMTP, que va tal cual. Un `5xx` señala a la dirección; un `4xx` es temporal. |
| Columna `Error` de `notif.Envio` | base de datos | La misma nota sobre el renglón `ENVIADO`. Es donde cobranza ve a quién corregirle el dato sin abrir la bitácora. |

Cuando **ningún** contacto del cliente acepta el correo, el envío se registra como fallido y **no
deja renglón** en `notif.Envio`: la factura se sigue viendo como no notificada y vuelve a entrar en
la población del martes siguiente. Es a propósito —registrarla la daría por notificada y el cliente
saldría de la lista sin haber recibido nada—, pero tiene una consecuencia que conviene tener
presente: ese caso se revisa en la bitácora, no en la tabla.

En `respuestas-*.log` hay una cuarta cuenta, `Entregas retrasadas`, que **no** cambia el estado de
nada: es el servidor avisando que sigue intentando. Sólo el rebote definitivo cierra el envío como
`FALLIDO`. Un retraso que reaparece cada corrida contra la misma dirección acaba en fallo días
después, así que verlo repetirse es la advertencia temprana.

### El recordatorio se degrada en silencio

La cadena que lo sostiene tiene tres eslabones, y los tres viven en lugares distintos:

```
martes 09:00   --cobranza     registra el envío y sus facturas en CorreosCXC.notif
     ↓
viernes 09:00  --cobranza     insiste; NO registra, sólo estampa su Message-Id
                              sobre los envíos que ese recordatorio cubrió
     ↓
L–V 10:00      --respuestas   lee el buzón y marca CONTESTADO: al envío si
                              contestaron el del martes, o a TODO el grupo si
                              contestaron el recordatorio
```

Contestar el recordatorio cierra de golpe todos los envíos que ese correo reclamaba, aunque vengan
de semanas distintas. Contestar el del martes cierra sólo ése.

Si el eslabón de en medio deja de correr —se comentó la línea del crontab, IMAP dejó de
autenticar, el seguimiento se apagó—, **el viernes no falla**: le manda el recordatorio también a
quien ya había contestado el martes. El daño es reputacional y no deja error.

**Pendiente conocido:** la ventana de búsqueda se mide contra `Envio.FechaEnvio`, que es la del
primer aviso. Un cliente al que se le lleva insistiendo más de `DiasVentanaMaxima` días queda fuera
del cruce, así que **su respuesta al recordatorio de ayer no se detecta** — y son justo los morosos
más viejos, los que más recordatorios reciben. El arreglo es comparar contra la fecha más reciente
entre el envío y su último recordatorio, que ya se guarda en `notif.EnvioRecordatorio.FechaEnvio`.

Peor que antes en un punto: ya nada cierra por vigencia los envíos que nadie contesta, así que un
renglón atorado en `ENVIADO` se queda ahí. `Seguimiento__DiasVentanaMaxima` es lo único que evita
que la búsqueda IMAP crezca sin límite.

Lo que sí queda registrado es el encabezado de las dos bitácoras:

```
 Poblacion   : RECORDATORIO - facturas ya notificadas que siguen sin contestar
   Marcados CONTESTADO    : 0
```

Un `Marcados CONTESTADO : 0` varios días seguidos en `respuestas-*.log` es la señal de que la
cadena se rompió. Vale la pena revisarlo junto con `Modo prueba` después de cada despliegue.
