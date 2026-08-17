# Fase 0.2 — Configurar `docker-compose.yml` con SQL Server, RabbitMQ y Jaeger

**Fecha:** 2026-08-17 · **Estado:** completado · **Roadmap:** [plan-desarrollo-ishop.md](../plan-desarrollo-ishop.md)

---

## Objetivo

Levantar la infraestructura local de la que dependen todas las fases siguientes:

- **SQL Server** — sin él no hay contra qué correr las migraciones de EF Core de la Fase 1.
- **RabbitMQ** — el transporte que usará MassTransit desde la Fase 3.
- **Jaeger** — el receptor OTLP al que exportarán trazas los servicios en la Fase 7.

Los dos últimos se dejan listos aunque no se usen todavía: es más barato configurarlos ahora, mientras el compose se está escribiendo de cero, que volver a tocarlo dentro de un mes.

**Fuera de alcance deliberadamente:** la creación de `CatalogDb`/`OrdersDb`/`InventoryDb`/`PaymentsDb` (eso es 0.4) y los servicios de aplicación con sus Dockerfiles (Fase 1). Este punto entrega solo infraestructura.

---

## Decisiones

### 1. Credenciales en `.env`, no hardcodeadas

El propio roadmap traía el snippet con `MSSQL_SA_PASSWORD=YourStrong@Passw0rd` escrito directamente en el YAML.

**Descartado** porque contradice la convención de secretos del proyecto ("Secrets go in User Secrets or environment variables, never in `appsettings.json`") — la regla es sobre no versionar credenciales, y un `docker-compose.yml` está tan versionado como un `appsettings.json`.

**Elegido:** el compose referencia `${MSSQL_SA_PASSWORD}`, el `.env` real queda fuera de git (ya cubierto por `.gitignore:7`) y se versiona `.env.example` como plantilla.

El coste es un paso manual antes del primer arranque (`Copy-Item .env.example .env`). Se aceptó a cambio de que el patrón sea el correcto desde el principio, en un repo que va a crecer.

### 2. El split base/override es por publicación de puertos

Esta es la decisión que más conviene entender del punto.

Hay dos formas de alcanzar estos contenedores, y confundirlas es —según las notas del proyecto— el fallo más común de esta fase:

| Desde | Cómo | Ejemplo |
|---|---|---|
| Otro contenedor | nombre del servicio en la red `shop133-net` | `Server=sqlserver,1433` |
| El host (Visual Studio) | `localhost` + puerto publicado | `Server=localhost,1433` |

**Descartado:** poner los `ports:` en `docker-compose.yml` y dejar el override para retoques cosméticos (límites de memoria, logging verboso). Es lo habitual, pero deja los dos modos de conexión mezclados en el mismo archivo, sin nada que los distinga.

**Elegido:** `docker-compose.yml` **no publica ni un solo puerto** — es exclusivamente la vista contenedor-a-contenedor. `docker-compose.override.yml` contiene **todos** los mapeos al host. Compose fusiona el override automáticamente, así que `docker compose up -d` sigue funcionando sin flags.

Así los dos modos de conexión viven en archivos distintos, y la distinción es visible al abrir el repo en vez de tener que explicarla en un comentario.

Comprobable:

```powershell
docker compose -f docker-compose.yml config   # sin ningún "published:"
docker compose config                          # con los seis puertos
```

### 3. Jaeger sin volumen ni healthcheck

Ambas ausencias son intencionales, no olvidos:

- **Sin healthcheck** — la imagen `all-in-one` no incluye shell ni `wget`, así que cualquier `test:` fallaría siempre y dejaría el contenedor marcado como `unhealthy` de forma permanente.
- **Sin volumen** — el storage por defecto es en memoria. Las trazas se pierden al reiniciar el contenedor. Aceptable en desarrollo; si en la Fase 7 estorba, se cambia el backend de almacenamiento.

Ambas están comentadas dentro del propio YAML para que nadie las "arregle" por costumbre.

---

## Cambios

| Archivo | Rol |
|---|---|
| [docker-compose.yml](../docker-compose.yml) | Los 3 servicios de infra, la red `shop133-net` y los volúmenes nombrados. Sin `ports:`. |
| [docker-compose.override.yml](../docker-compose.override.yml) | Solo publicación de puertos al host. Se fusiona automáticamente. |
| [.env.example](../.env.example) | Plantilla versionada de credenciales. |
| `.env` | Copia local con los valores reales. **No versionado.** |

Sin `version:` en la cabecera: es una clave obsoleta en Compose v2 y genera warning.

**Imágenes elegidas** (todas pinneadas, ninguna en `latest` salvo la de SQL Server, que solo publica ese tag para 2022):

| Servicio | Imagen |
|---|---|
| `sqlserver` | `mcr.microsoft.com/mssql/server:2022-latest` |
| `rabbitmq` | `rabbitmq:4-management-alpine` |
| `jaeger` | `jaegertracing/all-in-one:1.62.0` |

**Puertos publicados** (todos definidos en el override):

| Puerto | Servicio | Uso |
|---|---|---|
| 1433 | sqlserver | EF Core / SSMS desde el host |
| 5672 | rabbitmq | AMQP — el que usará MassTransit |
| 15672 | rabbitmq | UI de management (guest/guest) |
| 16686 | jaeger | UI de Jaeger |
| 4317 | jaeger | OTLP gRPC |
| 4318 | jaeger | OTLP HTTP |

---

## Detalles que cuestan tiempo

Seis cosas que no son evidentes y que costaría volver a descubrir:

**`MSSQL_SA_PASSWORD`, no `SA_PASSWORD`.** La segunda quedó deprecada en la imagen de SQL Server 2022. Con la variable antigua el contenedor arranca y muere sin un mensaje claro.

**El `$$` del healthcheck.** En:

```yaml
test: ["CMD-SHELL", "/opt/mssql-tools18/bin/sqlcmd ... -P \"$$MSSQL_SA_PASSWORD\" ..."]
```

el doble `$` escapa la interpolación de Compose, para que la variable la resuelva el shell **dentro** del contenedor. Con un solo `$`, Compose la sustituye al parsear el archivo y la contraseña acaba en texto plano en la definición del healthcheck.

**El flag `-C` de `sqlcmd` es obligatorio.** La imagen trae `mssql-tools18`, que por defecto exige un certificado TLS válido. Contra el certificado autofirmado que genera SQL Server, sin `-C` (trust server certificate) el healthcheck falla siempre y el contenedor nunca llega a `healthy`.

**RabbitMQ necesita `hostname:` fijo.** El nombre del nodo deriva del hostname del contenedor, que Docker genera aleatoriamente si no se fija. Sin `hostname: shop133-rabbitmq`, cada recreación del contenedor arranca un nodo con nombre distinto que **ignora los datos del volumen anterior** — colas y exchanges desaparecen sin error visible. Verificado que funciona: el nodo se llama `rabbit@shop133-rabbitmq`.

**Jaeger necesita `COLLECTOR_OTLP_ENABLED=true`.** Sin esa variable, la imagen `all-in-one` no expone los receptores OTLP (4317/4318) y el SDK de OpenTelemetry no tiene dónde exportar.

**`start_period: 30s` en SQL Server.** El primer arranque tarda ~20-30s en aceptar conexiones. Sin `start_period`, los intentos fallidos de ese arranque cuentan como `retries` y el contenedor se marca `unhealthy` antes de estar listo.

---

## Verificación

Ejecutado el 2026-08-17. Salidas reales:

| Check | Resultado |
|---|---|
| `docker compose config` | válido, variables resueltas desde `.env` |
| `docker compose ps` | sqlserver y rabbitmq `(healthy)`, jaeger `Up` |
| `sqlcmd -Q "SELECT @@VERSION"` | SQL Server 2022 (RTM-CU26) 16.0.4265.3, Developer Edition on Ubuntu 22.04 |
| `rabbitmq-diagnostics check_running` | `RabbitMQ on node rabbit@shop133-rabbitmq is fully booted and running` |
| `rabbitmq-diagnostics listeners` | AMQP en 5672, HTTP API en 15672 |
| UI RabbitMQ `http://localhost:15672` | 200 |
| UI Jaeger `http://localhost:16686` | 200 |
| `POST http://localhost:4318/v1/traces` | **415** Unsupported Media Type |
| TCP a 1433, 5672, 4317 desde el host | los tres accesibles |
| `docker compose -f docker-compose.yml config` | ningún `published:` — el split base/override es real |
| `docker compose restart sqlserver` | vuelve a `(healthy)` en 35s, volúmenes intactos |
| `docker volume ls` | `shop133_sqlserver-data`, `shop133_rabbitmq-data` |
| `git check-ignore .env` / `.env.example` | `.env` ignorado, `.env.example` versionado |
| `dotnet build` | Build succeeded, 0 warnings, 0 errors |

**Sobre el 415 de OTLP:** es el resultado correcto, no un fallo. Confirma que el receptor está escuchando y rechazando una petición sin el content-type que espera. El fallo real sería un *connection refused*.

### Problema encontrado

El primer `docker compose up -d` falló:

```
failed to connect to the docker API at npipe:////./pipe/dockerDesktopLinuxEngine;
check if the path is correct and if the daemon is running
```

Docker Desktop no estaba arrancado. Lo confuso es que `docker compose config` había funcionado justo antes — ese comando solo parsea el YAML y no necesita el daemon, así que **valida el archivo pero no prueba nada sobre Docker**. Es el primer sitio donde mirar si `up` falla y el `config` pasaba.

---

## Uso diario

```powershell
Copy-Item .env.example .env    # solo la primera vez
docker compose up -d
docker compose ps
docker compose down            # conserva los datos
docker compose down -v         # borra también los volúmenes
```

Los contenedores llevan `restart: unless-stopped`, así que vuelven a arrancar solos con Docker Desktop.

UIs: RabbitMQ `http://localhost:15672` (guest/guest) · Jaeger `http://localhost:16686`

---

## Pendiente

De la Fase 0 quedan:

- **0.3** — `Shop133.Contracts` con los eventos base.
- **0.4** — crear `CatalogDb`, `OrdersDb`, `InventoryDb`, `PaymentsDb`. El healthcheck de `sqlserver` ya está preparado para que un servicio de init pueda depender de él con `condition: service_healthy`.
- **0.5** — convención de branches.

Los servicios de aplicación entran en el compose en la Fase 1, cuando existan sus Dockerfiles.
