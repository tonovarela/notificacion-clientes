# --- Compilación -------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# El .csproj primero para que la capa de restore se reutilice mientras no cambien los paquetes.
COPY notificacion-clientes.csproj ./
RUN dotnet restore notificacion-clientes.csproj

COPY . .
RUN dotnet publish notificacion-clientes.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

# --- Ejecución ---------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app

# tzdata para que la variable TZ tenga efecto en las fechas de los correos.
RUN apt-get update \
    && apt-get install -y --no-install-recommends tzdata \
    && rm -rf /var/lib/apt/lists/*

ENV TZ=America/Mexico_City \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# Por omisión la imagen es de producción: los correos salen a clientes y vendedores reales.
# El --env-file del host pisa estos valores, así que el .env del servidor no debe traer
# ModoPrueba=true si no se quiere una corrida de prueba.
#
# Para armar una imagen de prueba —todos los correos redirigidos al buzón de pruebas, ningún
# cliente recibe nada— se construye con:
#
#   docker buildx build --build-arg MODO_PRUEBA=true ...
#
# Es un argumento y no una edición de este archivo a propósito: el default del repositorio se
# queda en 'false', así que una construcción distraída nunca produce una imagen que calla los
# correos sin que nadie lo haya pedido.
#
# Aquí se hornea SÓLO Smtp__ModoPrueba, nunca Smtp__ModoPruebaVendedores. La aplicación hereda
# la segunda de la primera cuando la variable NO está definida, y hornearla la definía siempre:
# un .env con Smtp__ModoPrueba=true dejaba a los clientes en prueba y mandaba la cartera a los
# vendedores reales, porque la herencia nunca llegaba a ocurrir. Dejarla sin definir es lo que
# hace que el interruptor de arriba valga para los dos procesos.
ARG MODO_PRUEBA=false
ENV Smtp__ModoPrueba=${MODO_PRUEBA}

COPY --from=build /app/publish .

# Directorio de la bitácora. Se monta como volumen para que la evidencia sobreviva al contenedor.
RUN mkdir -p /app/Logs && chown -R $APP_UID:$APP_UID /app/Logs
VOLUME ["/app/Logs"]

# Usuario sin privilegios que ya viene en la imagen oficial.
USER $APP_UID

ENTRYPOINT ["dotnet", "notificacion-clientes.dll"]
