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

COPY --from=build /app/publish .

# Directorio de la bitácora. Se monta como volumen para que la evidencia sobreviva al contenedor.
RUN mkdir -p /app/Logs && chown -R $APP_UID:$APP_UID /app/Logs
VOLUME ["/app/Logs"]

# Usuario sin privilegios que ya viene en la imagen oficial.
USER $APP_UID

ENTRYPOINT ["dotnet", "notificacion-clientes.dll"]
