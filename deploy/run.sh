#!/usr/bin/env bash
#
# Ejecuta una corrida de notificación de facturas dentro de un contenedor.
#
# Está pensado para que lo invoque un schedule (systemd timer o cron), no para uso interactivo,
# aunque corre igual a mano para una prueba. Escribe todo a stdout/stderr: bajo systemd eso queda
# en el journal, bajo cron redirige la salida a un archivo desde el crontab.
#
# Instalación: /opt/notificacion-clientes/run.sh (chmod 750)
#
set -uo pipefail

# --- Configuración -----------------------------------------------------------
# La versión de la imagen vive aquí, NO en el timer ni en el crontab: así un rollback es cambiar
# una línea de este archivo y el schedule nunca se toca.
IMAGEN="${IMAGEN:-tonovarela/notificacion-clientes:1.0.0}"

BASE="${BASE:-/opt/notificacion-clientes}"
ARCHIVO_ENV="${ARCHIVO_ENV:-$BASE/.env}"
DIRECTORIO_LOGS="${DIRECTORIO_LOGS:-$BASE/logs}"

NOMBRE_CONTENEDOR="notificacion-clientes"

# Correo de alerta cuando la corrida falla. Vacío = sin alerta por correo.
CORREO_ALERTA="${CORREO_ALERTA:-}"

# URL de ping para monitoreo externo (healthchecks.io, Uptime Kuma...). Vacío = sin ping.
# Se pinga /start al comenzar, la URL tal cual al terminar bien, y /fail al fallar. Esto es lo
# único que detecta que el servidor se apagó y la corrida nunca ocurrió.
URL_MONITOREO="${URL_MONITOREO:-}"

# Límites de recursos: el proceso descarga XML y PDF a memoria y puede compartir servidor con SQL.
LIMITE_MEMORIA="${LIMITE_MEMORIA:-512m}"
LIMITE_CPU="${LIMITE_CPU:-1}"

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
    alertar "FALLO notificación de facturas: falta configuración" \
            "No se encontró o no se puede leer $ARCHIVO_ENV en $(hostname)."
    exit 78  # EX_CONFIG
fi

if [ ! -d "$DIRECTORIO_LOGS" ]; then
    registrar "ERROR: no existe el directorio de bitácoras $DIRECTORIO_LOGS"
    exit 78
fi

# --- Corrida -----------------------------------------------------------------
# El --name fijo es el anti-solapamiento: si la corrida anterior sigue viva (API lenta, muchos
# CFDI), este docker run falla de inmediato con código 125 y no se duplican correos a clientes.
registrar "iniciando corrida con la imagen $IMAGEN"
pingear "$URL_MONITOREO/start"

docker run --rm \
    --name "$NOMBRE_CONTENEDOR" \
    --env-file "$ARCHIVO_ENV" \
    --volume "$DIRECTORIO_LOGS:/app/Logs" \
    --memory "$LIMITE_MEMORIA" \
    --cpus "$LIMITE_CPU" \
    "$IMAGEN"
CODIGO=$?

if [ $CODIGO -eq 0 ]; then
    registrar "corrida terminada correctamente"
    pingear "$URL_MONITOREO"
    exit 0
fi

# 125 es Docker diciendo que no pudo crear el contenedor. La causa habitual y esperada es que la
# corrida anterior siga en curso; se distingue para no confundirla con una falla del proceso.
if [ $CODIGO -eq 125 ] && docker ps --format '{{.Names}}' | grep -qx "$NOMBRE_CONTENEDOR"; then
    registrar "AVISO: la corrida anterior sigue en curso, se omite esta ejecución"
    alertar "OMITIDA notificación de facturas en $(hostname)" \
            "Se omitió la corrida porque el contenedor $NOMBRE_CONTENEDOR seguía en ejecución."
    pingear "$URL_MONITOREO/fail"
    exit $CODIGO
fi

registrar "ERROR: la corrida falló con código $CODIGO"

# La bitácora se escribe incluso cuando hay error fatal, así que la última suele traer el motivo.
ULTIMA_BITACORA="$(ls -1t "$DIRECTORIO_LOGS"/envios-*.log 2>/dev/null | head -n 1)"
DETALLE="La notificación de facturas falló en $(hostname) con código $CODIGO."
if [ -n "$ULTIMA_BITACORA" ]; then
    DETALLE="$DETALLE

Última bitácora: $ULTIMA_BITACORA

$(head -n 30 "$ULTIMA_BITACORA")"
fi

alertar "FALLO notificación de facturas en $(hostname)" "$DETALLE"
pingear "$URL_MONITOREO/fail"
exit $CODIGO
