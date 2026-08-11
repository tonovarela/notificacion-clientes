# Despliegue en producción con schedule

Guía para publicar la imagen en Docker Hub y ejecutarla en un servidor de producción de forma
programada.

El punto que define todo lo demás: **esta aplicación es un proceso batch de una sola pasada**.
Consulta las facturas del día, manda los correos, escribe la bitácora y termina con código de
salida `0` o `1`. No es un servicio que se queda arriba. Por eso:

- **No** se usa `restart: always`. El proceso termina y Docker lo reiniciaría en bucle, mandando
  correos repetidos a los clientes.
- **No** se mete un scheduler dentro del contenedor. Quien agenda es el servidor.
- El schedule vive en el host (`systemd timer` o `cron`) y cada disparo levanta un contenedor
  nuevo con `docker run --rm`.

Archivos de esta carpeta:

| Archivo | Destino en el servidor | Qué hace |
|---|---|---|
| `run.sh` | `/opt/notificacion-clientes/run.sh` | Levanta el contenedor, detecta solapamiento, alerta si falla. |
| `notificacion-clientes.service` | `/etc/systemd/system/` | Unidad `oneshot` que invoca `run.sh`. |
| `notificacion-clientes.timer` | `/etc/systemd/system/` | Horario de las corridas. |

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

### Versionado

Etiqueta siempre con versión semántica **además** de `latest`. En producción el schedule apunta a
la versión fija (`:1.0.0`), nunca a `:latest`.

El motivo es concreto: si el schedule usa `latest` y alguien sube una imagen con un error un
martes, el proceso de facturación del miércoles falla sin que nadie haya tocado el servidor, y el
diagnóstico se vuelve un misterio. Con versión fija, el servidor sólo cambia cuando alguien lo
decide.

En `run.sh` la versión está en la variable `IMAGEN`, no en el timer ni en el crontab. Así el
rollback es cambiar una línea y el schedule nunca se toca.

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

```
/opt/notificacion-clientes/
├── .env            # credenciales reales — chmod 600
├── run.sh          # wrapper que invoca el schedule — chmod 750
└── logs/           # bitácoras, sobreviven al contenedor
```

```bash
sudo useradd --system --shell /usr/sbin/nologin notificaciones
sudo usermod -aG docker notificaciones

sudo mkdir -p /opt/notificacion-clientes/logs
sudo cp deploy/run.sh /opt/notificacion-clientes/
sudo chmod 750 /opt/notificacion-clientes/run.sh
sudo chown -R notificaciones:notificaciones /opt/notificacion-clientes
```

### Configuración: sólo variables de entorno

`AppSettings.Cargar()` lee `appsettings.json` como **opcional** y las variables de entorno lo
pisan. En el servidor **no copies `appsettings.json`**: duplicar el lugar donde viven las
contraseñas es cómo se termina con dos configuraciones que no coinciden.

Copia `.env.example` del repositorio a `/opt/notificacion-clientes/.env` y ajústalo:

```bash
sudo install -o notificaciones -g notificaciones -m 600 .env.example /opt/notificacion-clientes/.env
sudo -u notificaciones nano /opt/notificacion-clientes/.env
```

Valores que **obligatoriamente** cambian respecto al ejemplo:

| Variable | Producción | Por qué |
|---|---|---|
| `Smtp__ModoPrueba` | `false` | Con `true` **ningún cliente recibe nada**: todo se redirige a `Smtp__CorreoPrueba`. Es el interruptor más delicado del sistema. |
| `Smtp__Password` | contraseña de aplicación real | En Gmail no es la contraseña del buzón. |
| `ConnectionStrings__SqlServer` | cadena real | El usuario necesita permisos en `Lito` y `LitoCRM`. |
| `Bitacora__Ruta` | `/app/Logs` | Ruta absoluta: `Path.Combine` la respeta tal cual y cae en el volumen montado. |
| `TZ` | `America/Mexico_City` | Afecta las fechas impresas en el correo. |

### Primeras corridas en modo prueba

Deja `Smtp__ModoPrueba=true` durante una o dos corridas programadas completas. Así validas desde
el servidor real la conectividad a SQL Server, al API de facturas y al SMTP, revisando los correos
en el buzón de pruebas, sin riesgo de mandar CFDI equivocados a clientes.

---

## 3. Permisos del volumen de bitácoras

El contenedor corre como `USER $APP_UID` (no-root, UID 1654). El `chown` del Dockerfile aplica a
`/app/Logs` **dentro de la imagen**, pero al montar `./logs` del host ese chown queda tapado: manda
el dueño del directorio del host.

Si `/opt/notificacion-clientes/logs` pertenece a `root`, la aplicación **no puede escribir la
bitácora**, que es justamente la evidencia que necesitas cuando algo falla:

```bash
sudo chown -R 1654:1654 /opt/notificacion-clientes/logs
```

Ajusta el UID a lo que devolvió el `id` del paso 1.

---

## 4. Schedule con systemd (recomendado)

Frente a cron, systemd da mejor observabilidad (`journalctl`, `systemctl list-timers`), tope de
duración, limpieza del contenedor huérfano y recuperación de corridas perdidas.

```bash
sudo cp deploy/notificacion-clientes.service /etc/systemd/system/
sudo cp deploy/notificacion-clientes.timer   /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now notificacion-clientes.timer
```

Verificación y operación diaria:

```bash
systemctl list-timers notificacion-clientes.timer   # próxima corrida
systemctl start notificacion-clientes.service       # corrida manual ahora
journalctl -u notificacion-clientes.service -n 100  # salida de la última corrida
journalctl -u notificacion-clientes.service -f      # seguir en vivo
```

Detalles del `.timer` que conviene entender antes de ajustarlo:

- **`Timezone=America/Mexico_City`**: imprescindible si el servidor está en UTC. Sin esta línea la
  corrida de las 19:00 caería a las 13:00 hora de México, o cruzaría de día, y la consulta filtra
  por la fecha del día. Requiere systemd 246 o superior (`systemctl --version`).
- **`Persistent=true`**: si el servidor estaba apagado a la hora programada, la corrida se ejecuta
  al encender. Recupera **una** corrida, no una por día perdido, así que no genera correos
  duplicados. Ponlo en `false` si prefieres que una corrida perdida se revise a mano.
- **`OnCalendar=Mon..Fri 19:00`**: ajusta el horario a cuando ya estén concluidas las facturas del
  día.

Y del `.service`:

- **`Type=oneshot`**: systemd espera a que el proceso termine y considera la unidad fallida si el
  código de salida es distinto de cero.
- **`TimeoutStartSec=30min`** con **`ExecStopPost=docker rm -f`**: si systemd tiene que matar una
  corrida colgada, el contenedor quedaría huérfano y su `--name` bloquearía la corrida del día
  siguiente. Esa línea lo limpia.
- **`Restart=no`**: un reintento automático repetiría correos ya enviados a clientes.

### Alternativa: cron

Si el servidor no usa systemd:

```cron
CRON_TZ=America/Mexico_City
0 19 * * 1-5 /opt/notificacion-clientes/run.sh >> /opt/notificacion-clientes/logs/cron.log 2>&1
```

`CRON_TZ` cumple el mismo papel que `Timezone=` en el timer y es igual de necesario. El crontab va
en el usuario `notificaciones`, no en root.

---

## 5. Qué hace `run.sh`

- **`--name notificacion-clientes` fijo**: es el anti-solapamiento. Si la corrida anterior sigue
  viva (API lenta, muchos CFDI), el nuevo `docker run` falla de inmediato con código 125 y no se
  duplican correos. El script distingue ese caso y lo reporta como corrida omitida, no como falla.
- **`--rm`**: limpia el contenedor y los volúmenes anónimos que genera el `VOLUME` del Dockerfile.
  Sin esto se acumulan con cada corrida.
- **No hace `docker pull`**: actualizar es un paso de despliegue deliberado, no del schedule.
- **`--memory` / `--cpus`**: acota el proceso, que descarga XML y PDF a memoria, para que no
  compita con SQL Server si comparten servidor.
- **Verificaciones previas**: si falta el `.env` o el directorio de logs, falla con un mensaje
  claro sin levantar nada.
- **Alerta al fallar**: adjunta el encabezado de la última bitácora, que trae el motivo del error.

Variables que puedes ajustar sin editar el archivo (o editando la sección de configuración):
`IMAGEN`, `BASE`, `ARCHIVO_ENV`, `DIRECTORIO_LOGS`, `CORREO_ALERTA`, `URL_MONITOREO`,
`LIMITE_MEMORIA`, `LIMITE_CPU`.

---

## 6. Actualizar a una versión nueva

```bash
# 1. Traer la imagen
docker pull tonovarela/notificacion-clientes:1.1.0

# 2. Probar en seco, sin tocar a los clientes
docker run --rm \
  --env-file /opt/notificacion-clientes/.env \
  -e Smtp__ModoPrueba=true \
  -v /opt/notificacion-clientes/logs:/app/Logs \
  tonovarela/notificacion-clientes:1.1.0

# 3. Si todo salió bien, actualizar la variable IMAGEN en run.sh
sudo -u notificaciones nano /opt/notificacion-clientes/run.sh
```

No hace falta `daemon-reload` ni reiniciar el timer: el cambio está dentro de `run.sh`.

**Rollback**: regresar la versión anterior en `IMAGEN`. Nada más.

---

## 7. Monitoreo

La aplicación ya entrega dos señales aprovechables:

1. **Código de salida**: el `catch` de `Program.cs` pone `ExitCode = 1` ante un error fatal.
   systemd marca la unidad como `failed` y `run.sh` manda la alerta.
2. **Bitácora en disco**: se escribe **incluso cuando hay error fatal**, con el motivo incluido.
   Un archivo por corrida, `envios-YYYY-MM-DD_HH-mm-ss.log`.

Falta cubrir un tercer caso, que es el que se olvida y el más silencioso:

3. **Alerta por ausencia**. Si el servidor se apaga, Docker muere o alguien deshabilita el timer,
   no hay ningún código de salida que falle: simplemente no pasa nada, y nadie se entera hasta que
   un cliente reclama que no recibió su factura. Configura `URL_MONITOREO` en `run.sh` apuntando a
   un healthchecks.io (o Uptime Kuma en modo push) con el periodo esperado: ese servicio avisa
   cuando el ping **no llega**.

Alerta por correo: define `CORREO_ALERTA` en `run.sh`. Requiere `mailutils` o equivalente
instalado en el servidor.

Rotación de bitácoras, para que `logs/` no crezca sin límite:

```cron
0 3 1 * * find /opt/notificacion-clientes/logs -name 'envios-*.log' -mtime +90 -delete
```

---

## 8. Riesgo principal a vigilar

`Smtp__ModoPrueba` es un booleano en un archivo de texto que separa dos mundos completamente
distintos: *nadie recibe nada* y *todos los clientes reciben sus CFDI*.

La bitácora ya registra en qué modo corrió cada ejecución (`Modo prueba : SI/NO`). Vale la pena
revisarla después del primer despliegue en modo real, y tenerlo presente al diagnosticar un
"no llegaron los correos": lo primero que hay que descartar es que la corrida fue en modo prueba.
