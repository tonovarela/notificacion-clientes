# Datos de prueba

Sustituyen las consultas SQL por archivos JSON. Se activan apuntando `DatosPrueba:Ruta` a una
carpeta; con la variable de entorno:

```bash
DatosPrueba__Ruta="$PWD/Datos" dotnet run -- --cobranza --primer-aviso --previsualizar
```

| Archivo | Qué representa |
|---|---|
| `facturas.json` | La consulta de facturas del día → `--clientes` |
| `revision-vendedores.json` | La vista de cartera por vendedor → `--vendedores` |
| `cobranza-vencida.json` | **La vista de antigüedad completa**: todo lo vencido |
| `envios.json` | `CorreosCXC.notif.Envio` + `notif.EnvioFactura` |

## Las dos poblaciones de cobranza se calculan, no se declaran

`cobranza-vencida.json` **no** es la lista de un día: es todo lo vencido, igual que
`v_AntiguedadCxC`. Las dos poblaciones salen de cruzarla contra `envios.json`, replicando los dos
CTE de `FacturaDAO`:

| Comando | Criterio | Equivale a |
|---|---|---|
| `--primer-aviso` | MovID **no** aparece en ningún envío | CTE `FacturasNotificaficadas` |
| `--recordatorio` | MovID **sí** aparece, y su envío no está `Contestado` | CTE `EnviosNoContestados` |

Esto es lo que hace que **volver a correr el primer aviso no re-notifique a nadie**: en cuanto el
envío queda registrado en `envios.json`, sus facturas dejan de ser primer aviso y pasan al
recordatorio.

**El recordatorio no registra nada.** Manda su correo y no toca `envios.json`, así que correrlo
diez veces deja un solo renglón: el del primer aviso. Si registrara, cada viernes agregaría otro
renglón con el mismo MovID y el estado quedaría partido entre varios envíos.

El ciclo completo se puede recorrer sin tocar la base:

```bash
DatosPrueba__Ruta="$PWD/Datos" dotnet run -- --cobranza --primer-aviso   # 2 clientes, +2 renglones
DatosPrueba__Ruta="$PWD/Datos" dotnet run -- --cobranza --primer-aviso   # 0 clientes, +0 renglones
DatosPrueba__Ruta="$PWD/Datos" dotnet run -- --cobranza --recordatorio   # aparecen aquí, +0 renglones
DatosPrueba__Ruta="$PWD/Datos" dotnet run -- --cobranza --recordatorio   # igual, +0 renglones
```

> La segunda corrida requiere que la primera **haya enviado** (sin `--previsualizar`), porque el
> registro ocurre después del envío. Con `Smtp__ModoPrueba=true` todo va al buzón de pruebas; para
> no mandar nada en absoluto se puede apuntar `Smtp__Host` a un SMTP local. Para volver al punto
> de partida, quita de `envios.json` los renglones que se agregaron.

## Estado inicial

Con los archivos como vienen, sin haber corrido nada:

| Cliente | Facturas | En `envios.json` | Población | Caso que cubre |
|---|---|---|---|---|
| **2001** | 2 | — | primer aviso | Varios contactos: 2 facturas × 2 contactos = 4 filas, **un** correo a los dos, saludo genérico, facturas sin duplicar |
| **2002** | 1 | — | primer aviso | Una sola factura (texto en singular), dólares (`USD`, formato en-US), 369 días (los días en rojo) |
| **2003** | 2 | `Enviado` | recordatorio | Dos monedas: `MXN` y `USD` separados, nunca sumados. La moneda va como `"Pesos   "` con relleno, igual que la vista |
| **2004** | 1 | `Enviado`, modo prueba | recordatorio | Sin agente en el CRM: el cierre omite la mención del asesor |
| **2006** | 1 | `Contestado` | **ninguna** | Contestó: no recibe nada, ni primer aviso ni recordatorio |
| **2007** | 1 | `Fallido` | recordatorio | ⚠️ Ver abajo: un envío que rebotó **sí** entra al recordatorio |

`envios.json` trae además dos renglones que no están en la vista de vencidas:

- **2008**, `Enviado` del 15/06 — su factura ya se pagó, pero el envío sigue abierto porque nada
  lo cierra. Es el renglón que `DiasVentanaMaxima` tiene que dejar fuera al leer el buzón.
- **1001**, proceso `Clientes` — otro proceso, mismo buzón; `--respuestas` también lo concilia.

Con eso, `--respuestas` toma **4 abiertos** de 6: quedan fuera el `Contestado` y el del 15/06.

## El sello del recordatorio

El recordatorio no agrega envíos, pero sí anota su `Message-Id` en cada envío cuyas facturas iban
en ese correo. En el JSON eso vive en `RecordatorioMessageIds`, una lista que **se acumula**:

```
  id=1  [CFDI-A1]  Enviado   RecordatorioMessageIds = [abc@lito, def@lito, ghi@lito]
  id=2  [CFDI-A2]  Enviado   RecordatorioMessageIds = [ghi@lito]
```

`id=1` lleva tres recordatorios encima; `id=2` entró después y sólo alcanzó el último. Los dos
comparten `ghi@lito`, así que una respuesta a ese correo los cierra **a los dos**. Y una respuesta
a `abc@lito`, de hace tres semanas, todavía cierra `id=1`.

Para probarlo a mano, agrega el mismo id a la lista de varios envíos y contesta desde el buzón de
pruebas. En SQL Server esto vive en la tabla `notif.EnvioRecordatorio`.

## ⚠️ Un envío `FALLIDO` vuelve al recordatorio

El CTE `EnviosNoContestados` filtra por `Estado NOT IN ('CONTESTADO')`, así que un `FALLIDO`
cuenta como "pendiente de respuesta". El cliente **2007** lo demuestra: su correo rebotó y aun así
aparece cada viernes, a la misma dirección que no existe. Y como su factura ya figura en
`EnvioFactura`, tampoco puede volver al primer aviso. Se corrige agregando `'FALLIDO'` a ese
`NOT IN`.

## Escenarios aislados

```bash
DatosPrueba__Ruta="$PWD/Datos/escenarios/sin-vencidos" dotnet run -- --cobranza --previsualizar
```

| Carpeta | Para qué |
|---|---|
| `sin-vencidos/` | No hay nada que notificar: 0 clientes, sin correos, con bitácora |
| `sin-contacto-cxp/` | Cliente con `Email: null`. Sin `envios.json`, así que sale como primer aviso |
| `sin-envios-abiertos/` | Todo `Contestado`: `--respuestas` corta antes de abrir el buzón |

## Lo que estos archivos NO pueden cubrir

- **El cruce de respuestas** (`In-Reply-To`, `References`, remitente + asunto, autorrespuestas,
  rebotes). El JSON controla *qué envíos están abiertos*, pero los correos tienen que existir de
  verdad en el buzón. Se prueba contestando desde el buzón de pruebas.
- **Infraestructura**: script SQL idempotente, corridas encimadas, timeout, permisos.
- **La descarga de XML y PDF**: con datos de prueba se omite (`omitirDescargaArchivos`), por eso
  `--clientes` avisa que el IVA quedó en cero.

> Las fechas están calculadas contra el **2026-08-20**. Los días vencidos se recorren solos
> conforme pase el tiempo; lo que no cambia son los umbrales que cruzan (>60 días, una sola
> factura, dos monedas).
