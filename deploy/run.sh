#!/usr/bin/env bash
#
# Ejecuta una corrida de notificación de facturas dentro de un contenedor.
#
#   run.sh clientes            facturas del día a cada cliente      (lunes a viernes, 18:00)
#   run.sh vendedores          cartera sin ingresar a revisión      (martes y viernes, 09:00)
#   run.sh cobranza            estado de cuenta vencido             (martes y viernes, 09:00)
#   run.sh respuestas          lee el buzon y marca quien contesto  (lunes a viernes, 10:00)
#
# Lo que venga después del proceso se le pasa tal cual al contenedor, para poder hacer
#   run.sh cobranza --previsualizar
#
# El proceso es obligatorio: el ejecutable no hace nada sin --clientes o --vendedores, así que
# invocarlo sin argumento terminaría en 0 sin mandar un solo correo. Aquí se rechaza de entrada.
#
# Está pensado para que lo invoque el crontab, no para uso interactivo, aunque corre igual a mano
# para una prueba. Escribe todo a stdout/stderr, así que el crontab redirige la salida a un
# archivo: sin esa redirección cron intenta mandarla por correo local y normalmente se pierde.
#
# Instalación: /home/docker/notificacion-clientes/run.sh (chmod 750)
#
set -uo pipefail

# --- Proceso a ejecutar ------------------------------------------------------
PROCESO="${1:-}"
shift 2>/dev/null || true

# Todo lo demás se le pasa al contenedor sin interpretarlo: --previsualizar y lo que venga.
ARGUMENTOS_EXTRA=("$@")

case "$PROCESO" in
    clientes)
        # Prefijo del archivo que escribe BitacoraService para este proceso; se usa al alertar.
        PREFIJO_BITACORA="envios"
        DESCRIPCION="notificación de facturas a clientes"
        ;;
    vendedores)
        PREFIJO_BITACORA="revision-vendedores"
        DESCRIPCION="aviso de cartera a vendedores"
        ;;
    cobranza)
        PREFIJO_BITACORA="cobranza"
        DESCRIPCION="estado de cuenta vencido a clientes"
        ;;
    respuestas)
        PREFIJO_BITACORA="respuestas"
        DESCRIPCION="lectura del buzon y marcado de estados"
        ;;
    *)
        printf 'uso: %s {clientes|vendedores|cobranza|respuestas} [args...]\n' \
               "$(basename "$0")" >&2
        exit 64  # EX_USAGE
        ;;
esac

# --- Configuración -----------------------------------------------------------
# La versión de la imagen vive aquí, NO en el crontab: así un rollback es cambiar una línea de
# este archivo y el schedule nunca se toca.
IMAGEN="${IMAGEN:-tonovarela/notificacion-clientes:1.0.3}"

# El despliegue vive en /home/docker/<nombre-del-contenedor>, la convención del servidor: una
# carpeta por aplicación con su .env, este script y sus bitácoras. BASE se deduce de dónde está
# instalado run.sh, así que renombrar o mover la carpeta no obliga a editar el script.
BASE="${BASE:-$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)}"
ARCHIVO_ENV="${ARCHIVO_ENV:-$BASE/.env}"

# Las bitácoras quedan aquí, junto al despliegue: es el directorio que se monta en /app/Logs.
DIRECTORIO_LOGS="${DIRECTORIO_LOGS:-$BASE/logs}"

# Un nombre por proceso: el --name es el candado anti-solapamiento, y si los procesos
# compartieran nombre la corrida de cobranza de las 09:00 bloquearía —o sería bloqueada por—
# la de clientes o la de vendedores, que son independientes y sí pueden convivir.
NOMBRE_CONTENEDOR="notificacion-clientes-$PROCESO"

# Correo de alerta cuando la corrida falla. Vacío = sin alerta por correo.
CORREO_ALERTA="${CORREO_ALERTA:-}"

# URL de ping para monitoreo externo (healthchecks.io, Uptime Kuma...). Vacío = sin ping.
# Se pinga /start al comenzar, la URL tal cual al terminar bien, y /fail al fallar. Esto es lo
# único que detecta que el servidor se apagó y la corrida nunca ocurrió.
URL_MONITOREO="${URL_MONITOREO:-}"

# Límites de recursos: el proceso descarga XML y PDF a memoria y puede compartir servidor con SQL.
LIMITE_MEMORIA="${LIMITE_MEMORIA:-512m}"
LIMITE_CPU="${LIMITE_CPU:-1}"

# Tope de duración de la corrida. Cron no sabe matar un trabajo colgado: sin esto, una corrida
# atorada en el SMTP seguiría viva a la hora de la siguiente y su --name la bloquearía en cadena.
# Descargar los CFDI de todo un día y mandarlos por correo tarda; 30 minutos deja margen.
TIMEOUT_CORRIDA="${TIMEOUT_CORRIDA:-30m}"

# --- Utilidades --------------------------------------------------------------
registrar() {
    printf '%s [run.sh] %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*"
}

pingear() {
    [ -n "$URL_MONITOREO" ] || return 0
    curl -fsS -m 10 --retry 3 "$1" >/dev/null 2>&1 || registrar "AVISO: falló el ping de monitoreo a $1"
}

alertar() {
    local asunto="$1" cuerpo="$2"
    [ -n "$CORREO_ALERTA" ] || return 0
    if command -v mail >/dev/null 2>&1; then
        printf '%s\n' "$cuerpo" | mail -s "$asunto" "$CORREO_ALERTA"
    else
        registrar "AVISO: no hay comando 'mail' instalado, no se envió la alerta"
    fi
}

# --- Verificaciones previas --------------------------------------------------
# Fallar aquí con un mensaje claro es mucho mejor que fallar adentro del contenedor: estos errores
# son de instalación y no tiene caso levantar nada para descubrirlos.
if [ ! -r "$ARCHIVO_ENV" ]; then
    registrar "ERROR: no se puede leer el archivo de configuración $ARCHIVO_ENV"
    alertar "FALLO $DESCRIPCION: falta configuración" \
            "No se encontró o no se puede leer $ARCHIVO_ENV en $(hostname)."
    exit 78  # EX_CONFIG
fi

if [ ! -d "$DIRECTORIO_LOGS" ]; then
    registrar "ERROR: no existe el directorio de bitácoras $DIRECTORIO_LOGS"
    exit 78
fi

# --- Corrida -----------------------------------------------------------------
# El --name fijo es el anti-solapamiento: si la corrida anterior del mismo proceso sigue viva
# (API lenta, muchos CFDI), este docker run falla de inmediato con código 125 y no se duplican
# correos. El argumento del final es el que decide qué proceso corre.
registrar "iniciando $DESCRIPCION con la imagen $IMAGEN"
pingear "$URL_MONITOREO/start"

# 'timeout' viene en coreutils y está en cualquier Linux; en macOS (pruebas a mano) se llama
# gtimeout o no está. Si no hay ninguno se corre sin tope: quedarse sin la corrida del día por un
# binario ausente es peor que arriesgar una colgada, y el aviso queda en el log.
# La variable va SIN comillas a propósito: tiene que partirse en dos palabras, o en ninguna.
if command -v timeout >/dev/null 2>&1; then
    LIMITADOR="timeout $TIMEOUT_CORRIDA"
elif command -v gtimeout >/dev/null 2>&1; then
    LIMITADOR="gtimeout $TIMEOUT_CORRIDA"
else
    registrar "AVISO: no hay comando 'timeout', la corrida se ejecuta sin tope de duración"
    LIMITADOR=""
fi

$LIMITADOR docker run --rm \
    --name "$NOMBRE_CONTENEDOR" \
    --env-file "$ARCHIVO_ENV" \
    --volume "$DIRECTORIO_LOGS:/app/Logs" \
    --memory "$LIMITE_MEMORIA" \
    --cpus "$LIMITE_CPU" \
    "$IMAGEN" "--$PROCESO" ${ARGUMENTOS_EXTRA[@]+"${ARGUMENTOS_EXTRA[@]}"}
CODIGO=$?

# 124 es 'timeout' avisando que cortó la corrida. Matar al cliente de docker NO detiene el
# contenedor: seguiría vivo y su --name bloquearía todas las corridas siguientes de este proceso.
# Hay que quitarlo aquí; después se cae al manejo normal de error y sale la alerta.
if [ $CODIGO -eq 124 ]; then
    registrar "ERROR: la corrida excedió $TIMEOUT_CORRIDA y fue interrumpida"
    docker rm -f "$NOMBRE_CONTENEDOR" >/dev/null 2>&1
fi

if [ $CODIGO -eq 0 ]; then
    registrar "corrida terminada correctamente"
    pingear "$URL_MONITOREO"
    exit 0
fi

# 125 es Docker diciendo que no pudo crear el contenedor. La causa habitual y esperada es que la
# corrida anterior siga en curso; se distingue para no confundirla con una falla del proceso.
if [ $CODIGO -eq 125 ] && docker ps --format '{{.Names}}' | grep -qx "$NOMBRE_CONTENEDOR"; then
    registrar "AVISO: la corrida anterior sigue en curso, se omite esta ejecución"
    alertar "OMITIDA $DESCRIPCION en $(hostname)" \
            "Se omitió la corrida porque el contenedor $NOMBRE_CONTENEDOR seguía en ejecución."
    pingear "$URL_MONITOREO/fail"
    exit $CODIGO
fi

registrar "ERROR: la corrida falló con código $CODIGO"

# La bitácora se escribe incluso cuando hay error fatal, así que la última suele traer el motivo.
# Cada proceso escribe su propio archivo, por eso el prefijo depende del que se está corriendo.
ULTIMA_BITACORA="$(ls -1t "$DIRECTORIO_LOGS/$PREFIJO_BITACORA"-*.log 2>/dev/null | head -n 1)"
DETALLE="La corrida de $DESCRIPCION falló en $(hostname) con código $CODIGO."
if [ -n "$ULTIMA_BITACORA" ]; then
    DETALLE="$DETALLE

Última bitácora: $ULTIMA_BITACORA

$(head -n 30 "$ULTIMA_BITACORA")"
fi

alertar "FALLO $DESCRIPCION en $(hostname)" "$DETALLE"
pingear "$URL_MONITOREO/fail"
exit $CODIGO
