# Fase 1.6 — Dockerfile del servicio

**Fecha:** 2026-08-20 · **Estado:** completado · **Roadmap:** [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md), punto 1.6

---

## Objetivo

Meter `Catalog.API` en un contenedor y enchufarlo al `docker compose` que existe desde 0.2. El roadmap titula el punto «Dockerfile del servicio», pero el objetivo declarado de la fase es *«tener un servicio funcional end-to-end (DB → API → Docker) antes de meter mensajería»*, y un `Dockerfile` que nadie arranca no cierra esa cadena: deja el último eslabón como una promesa. Por eso el entregable es que `docker compose up -d` levante el servicio contra `CatalogDb` **sin que nadie exporte una variable a mano**.

Hay además tres deudas anteriores que aterrizan exactamente aquí, y las tres estaban escritas:

- [fase_0_2.md](fase_0_2.md) cierra con *«Los servicios de aplicación entran en el compose en la Fase 1, cuando existan sus Dockerfiles»*.
- [fase_0_4.md](fase_0_4.md) tiene una sección titulada *«Consecuencias en fases posteriores → 1.6 y siguientes»* que prescribe el connection string `Server=sqlserver,1433` y la guarda `depends_on: db-init: condition: service_completed_successfully`.
- [fase_1_5.md](fase_1_5.md) quitó el `if (app.Environment.IsDevelopment())` de `MapOpenApi()` **porque la imagen de 1.6 arranca en `Production`**, y dejó anotado que el servicio necesitaría `ConnectionStrings__CatalogDb` como variable de entorno. Este punto es el que comprueba que aquella decisión era correcta o no lo era.

**Fuera de alcance deliberadamente:** el `healthcheck` y el endpoint `/health`, que son 8.4 (decisión 7); aplicar migraciones desde el contenedor, que se rechaza explícitamente (decisión 6); los otros cuatro servicios, que siguen siendo andamiaje vacío sin un solo controller; la publicación de la imagen a un registro y el build en CI, que son 8.3; y la terminación TLS, que pasa a ser trabajo del Gateway en la Fase 5. **No se añadió ningún paquete NuGet y no se tocó ningún `.csproj`.**

---

## Decisiones

### 1. El contexto de build es la raíz del repositorio, no la carpeta del proyecto

Es la decisión que condiciona todas las demás y la que más desconcierta al leer el `Dockerfile`: todas las rutas de `COPY` empiezan por `src/`, aunque el archivo viva en `src/Services/Catalog/Catalog.API/`.

*Descartado* poner el contexto en la carpeta del proyecto, que es lo natural (`docker build .` desde donde está el `Dockerfile`). **No es posible**: `Catalog.API.csproj` referencia `..\..\..\Shared\Shop133.Contracts\Shop133.Contracts.csproj`, y Docker no puede copiar nada que quede por encima del contexto. Un contexto estrecho no llega a `Shop133.Contracts` y el `restore` falla.

*Descartado* también **mover el `Dockerfile` a la raíz**, que haría coincidir contexto y ubicación. Se descarta porque el archivo es del *servicio*, no del repositorio: cuando la Fase 2 traiga `Orders.API` habrá un segundo, y una raíz con `Dockerfile.catalog`, `Dockerfile.orders`, `Dockerfile.payments`… reproduce en nombres de archivo la jerarquía de carpetas que ya existe. Es además la convención de las plantillas de .NET y de las herramientas de Visual Studio.

La consecuencia práctica es que **el `Dockerfile` no se puede construir a mano desde su propia carpeta**, solo desde la raíz o vía compose. Está dicho en un comentario en la cabecera del archivo porque es el primer error que va a cometer quien lo intente.

Y arrastra la decisión 2: con la raíz entera como contexto, el `.dockerignore` deja de ser higiene y pasa a ser obligatorio.

### 2. `.dockerignore` en la raíz, con `.env` dentro

Sin él, Docker empaqueta y envía al daemon **todo el repositorio**: los `bin/` y `obj/` de los once proyectos —el `obj/` de `Shop133.Web` por sí solo contiene cientos de ficheros `compressed/*.gz` de *static web assets*—, el `.git/` completo con toda la historia, y **el `.env` con las contraseñas de SQL Server**.

Que `.env` entre en el contexto no es teórico: cualquier `COPY . .` posterior lo metería en una capa de la imagen, y las capas de una imagen son legibles aunque un `RUN rm` posterior borre el archivo. Aquí no hay ningún `COPY . .` —los `COPY` son todos selectivos— así que el riesgo hoy es solo el tamaño del contexto; excluirlo es lo que hace que siga siendo solo eso mañana.

*Descartado* confiar en que los `COPY` selectivos bastan. Bastan **hoy**, con este `Dockerfile`. Un `.dockerignore` protege también al `Dockerfile` que alguien escriba en la Fase 2 con prisa.

Medido: con el archivo puesto, el contexto transferido son **201,92 kB**.

La regla al editarlo, escrita en el propio archivo: **nunca excluir `**/*.csproj` ni `global.json`**, que son justo lo que la capa de restore necesita copiar primero.

### 3. Multi-stage con los `.csproj` antes que el código

El `Dockerfile` copia `global.json` y los tres `.csproj`, ejecuta `dotnet restore`, y **solo entonces** copia el código.

*Descartado* el orden obvio —copiar todo y luego `dotnet publish`, que restaura por su cuenta—. Funciona y es una etapa menos, pero convierte cada cambio en un `.cs` en un `restore` completo contra nuget.org. La separación es lo que hace que la caché de Docker sirva de algo: los `.csproj` cambian poco, el código cambia constantemente.

Está medido, no supuesto. Tras cambiar una línea de `Program.cs` y reconstruir:

```
#10 [build  7/10] RUN dotnet restore src/Services/Catalog/Catalog.API/Catalog.API.csproj
#10 CACHED
...
#16 [build  9/10] COPY src/Services/Catalog/ src/Services/Catalog/
#16 DONE 0.0s
#17 [build 10/10] RUN dotnet publish ...
```

El `restore` (8,6 s en frío) sale de caché; solo se rehace el `publish`.

Son **exactamente tres** `.csproj` porque ese es el grafo completo de `Catalog.API`: referencia a `Catalog.Infrastructure` y a `Shop133.Contracts`, y ninguno de los dos referencia nada más.

*Descartado* restaurar `shop133.slnx`, que sería una línea en vez de tres `COPY`. Arrastraría los **diez** proyectos de la solución y sus paquetes —Gateway, Web, los cuatro `.API` vacíos— para construir una imagen que necesita tres.

`global.json` se copia el primero y en su propia capa. Fija el SDK (`10.0.100` con `rollForward: latestFeature`); sin él, `restore` y `publish` usarían el SDK de la imagen sin decir nada, y una divergencia con el del host no se notaría hasta que el comportamiento difiriera.

### 4. Imagen final `aspnet:10.0` estándar, no la *chiseled*

*Descartado* `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled`, que es notablemente más pequeña, no trae gestor de paquetes y **ya corre sin root** sin necesidad de declarar nada.

Se descarta porque **no trae shell ni coreutils**, y este proyecto existe para abrirse por dentro. Tres de las siete comprobaciones de la verificación de abajo son `docker compose exec catalog-api <algo>`: `id` para confirmar el usuario, `ls /app` para confirmar qué DLL acabaron en la imagen. En una chiseled ninguna de las tres se puede ejecutar, y la respuesta a «¿esto corre como root?» pasaría de una medición a una lectura de la documentación de la imagen.

El precio es tamaño: **370 MB en disco, 104 MB de contenido**. Es un intercambio consciente y el punto donde releerlo es 8.3, cuando haya un CI que empuje imágenes a un registro y el tamaño cueste ancho de banda en vez de disco local.

### 5. `USER $APP_UID` explícito — la imagen base **no** lo aplica

Este es el detalle que más fácil se da por supuesto. La imagen define la variable pero no cambia de usuario:

```
$ docker image inspect mcr.microsoft.com/dotnet/aspnet:10.0 --format "{{range .Config.Env}}{{println .}}{{end}}USER=[{{.Config.User}}]"
APP_UID=1654
ASPNETCORE_HTTP_PORTS=8080
DOTNET_RUNNING_IN_CONTAINER=true
DOTNET_VERSION=10.0.11
ASPNET_VERSION=10.0.11
USER=[]
```

`Config.User` vacío significa **root**. `APP_UID=1654` está ahí para que el `Dockerfile` lo use, no porque la imagen ya lo haya usado. Sin la línea `USER $APP_UID`, la API correría como root dentro del contenedor.

De ahí sale también por qué no hace falta `ASPNETCORE_URLS`: la imagen ya trae `ASPNETCORE_HTTP_PORTS=8080`. Y por qué el puerto es 8080 y no 80 — un proceso sin privilegios no puede abrir puertos por debajo de 1024, así que el puerto por defecto de estas imágenes es consecuencia directa de que estén pensadas para correr sin root.

`EXPOSE 8080` es documentación y nada más: no publica nada. El mapeo al host vive en el override (decisión 9).

### 6. La imagen no migra nada

`Program.cs` no llama a `Database.Migrate()` desde 1.2, y aquí se confirma en vez de revisarse.

*Descartado* migrar al arrancar, que es la tentación evidente ahora que hay un contenedor: haría que `docker compose up -d` dejara el sistema entero funcionando desde cero. Se descarta por lo que ya decía 1.2 —esconde el paso y no sobrevive a más de una instancia del servicio, porque dos réplicas arrancando a la vez ejecutan la misma migración en paralelo— y porque en la Fase 3 habrá cuatro servicios haciendo lo mismo contra cuatro bases.

**La consecuencia hay que decirla en voz alta: el esquema de `CatalogDb` es un requisito previo del contenedor, no algo que el contenedor resuelva.** `dotnet ef database update` desde el host sigue siendo el único camino. Está escrito como comentario junto al `ENTRYPOINT`, que es donde lo va a leer quien depure un arranque fallido.

Lo que `db-init` sí garantiza es que la **base y el login existen** — eso es 0.4, y es distinto de que existan las tablas.

### 7. Sin `healthcheck`, y no por olvido

*Descartado* añadir un `healthcheck` al servicio, que es lo que llevan `sqlserver` y `rabbitmq` y lo que haría que `docker compose ps` dijera algo más útil que `Up`.

Dos motivos, y el segundo es el que lo cierra:

1. **No hay endpoint que sondear.** `/health` es el punto 8.4. Sondear `/products` sería usar una consulta a la base como latido.
2. **La imagen `aspnet` de .NET 8+ ya no incluye `curl` ni `wget`.** Cualquier `test:` basado en HTTP fallaría siempre y dejaría el contenedor permanentemente `unhealthy` — que es peor que no tener healthcheck, porque un estado que siempre miente entrena a ignorarlo. Es el mismo razonamiento por el que 0.2 dejó `jaeger` sin healthcheck.

### 8. El connection string se deriva de `${CATALOG_DB_PASSWORD}`

En `docker-compose.yml`:

```yaml
- "ConnectionStrings__CatalogDb=Server=sqlserver,1433;Database=CatalogDb;User Id=catalog_user;Password=${CATALOG_DB_PASSWORD};TrustServerCertificate=True"
```

*Descartado* meter un `CONNECTIONSTRINGS__CATALOGDB` completo como variable nueva en `.env` y referenciarlo entero. Es más limpio de leer en el YAML, pero deja **la contraseña en dos sitios**: en `CATALOG_DB_PASSWORD`, que es la que `db-init` usa para *crear* el login, y dentro del connection string, que es la que la API usa para *autenticarse*. El día que una cambie sin la otra, el síntoma es un `Login failed for user 'catalog_user'` que no dice dónde está la copia vieja. Derivándolo, no pueden divergir.

*Descartado* también dejarlo en `appsettings.json`: la convención del proyecto es que los secretos nunca viven ahí, y de todas formas no funcionaría — la contraseña saldría versionada.

`TrustServerCertificate=True` es obligatorio, no opcional: `Microsoft.Data.SqlClient` cifra por defecto desde la v4.0 y el certificado de SQL Server es autofirmado. Es el mismo motivo por el que `sqlcmd` necesita `-C` en 0.2.

El **doble guion bajo** es cómo .NET traduce jerarquía de configuración a variables de entorno: `ConnectionStrings__CatalogDb` equivale a la clave `ConnectionStrings:CatalogDb` que lee `Program.cs`. Hace falta porque `ASPNETCORE_ENVIRONMENT=Production` implica que **los User Secrets no se cargan** — lo dejó apuntado 1.5 y aquí se cobra.

Y el `depends_on` va sobre `db-init`, no sobre `sqlserver`:

```yaml
depends_on:
  db-init:
    condition: service_completed_successfully
```

Es literalmente lo que prescribió 0.4 tras medirlo: `docker compose up -d` vuelve cuando `db-init` **arranca**, no cuando termina, y el script tarda unos 5 s en crear las cuatro bases y sus logins. Con la guarda puesta sobre `sqlserver`, la API arrancaría contra una `CatalogDb` que aún no existe. Esto es también lo que justifica el `-b` de `sqlcmd` en `db-init`: sin él el script saldría con `0` aunque fallara, y `service_completed_successfully` sería mentira.

### 9. Puerto `5125:8080`, y el `ports:` solo en el override

La regla del split de 0.2 se mantiene sin excepción: `docker-compose.yml` no gana ni un `ports:`, y el mapeo al host vive en `docker-compose.override.yml`. El archivo base sigue siendo la vista contenedor↔contenedor (`Server=sqlserver`), el override la vista host→contenedor.

Sobre el número: 5124 (http) y 7024 (https) los ocupa el perfil de `launchSettings.json`.

*Descartado* **5124**, que daría una sola URL que recordar. Se descarta porque impide tener las dos formas de ejecución arriba a la vez, y comparar «lo mismo desde el IDE» con «lo mismo en Docker» es justo lo que hace útil este punto; el segundo en arrancar moriría con *port is already allocated*.

*Descartado* **8080:8080**, mismo número dentro y fuera. 8080 es el puerto más disputado de cualquier máquina de desarrollo.

Comprobado que conviven: con el contenedor sirviendo, un `dotnet run` en 5124 arranca sin conflicto y ambos devuelven `200`.

### 10. `UseHttpsRedirection()` guardado con `IsDevelopment()`

Único cambio en código de este punto, y va en dirección contraria a la decisión 3 de 1.5 — que quitó una guarda `IsDevelopment()`. No es incoherencia: son dos middlewares con necesidades opuestas. La documentación **debe** verse en el contenedor; la redirección a HTTPS **no puede** funcionar allí.

El contenedor solo escucha HTTP (`ASPNETCORE_HTTP_PORTS=8080`, sin puerto https configurado). Sin guarda, el middleware no encuentra a dónde redirigir, se convierte en un *no-op* y loguea `Failed to determine the https port for redirect` **en cada petición**.

*Descartado* dejarlo sin guarda y asumir el ruido. Un warning por request convierte `docker compose logs` en algo que hay que filtrar antes de leer, y un log que siempre grita se deja de leer.

*Descartado* borrar la línea del todo, que sería más simple. El perfil `https` de `launchSettings.json` sigue existiendo y ahí la redirección sí tiene sentido.

Desde la Fase 5 la terminación TLS es trabajo del Gateway, no de cada servicio — así que la guarda no es un parche temporal, es la forma final.

### 11. Ningún test de arquitectura nuevo, ni paquete, ni `.csproj` tocado

Como en 1.5, `CLAUDE.md` obliga a plantearse si la suite de 0.6 puede hacer ejecutable alguna regla nueva. Aquí lo que se podría querer vigilar —«el `Dockerfile` no copia `.env`», «la imagen no corre como root»— **no son propiedades del código compilado**, que es lo único que `NetArchTest` y la reflexión ven. Serían tests sobre archivos de texto y sobre una imagen construida, es decir, categoría `Docker` y no `Fast`, y su sitio natural sería 8.3 junto al resto de verificación de la imagen en CI.

La suite se queda en **12 tests**, coherente con que no se haya tocado ningún `.csproj`.

---

## Cambios

| Archivo | Rol |
|---|---|
| [../src/Services/Catalog/Catalog.API/Dockerfile](../src/Services/Catalog/Catalog.API/Dockerfile) | **Nuevo.** Multi-stage: `sdk:10.0` restaura y publica, `aspnet:10.0` ejecuta como `app` (uid 1654). Contexto = raíz del repo. |
| [../.dockerignore](../.dockerignore) | **Nuevo.** Recorta el contexto a 201,92 kB y deja fuera `bin/`, `obj/`, `.git/`, `tests/` y **`.env`**. |
| [../docker-compose.yml](../docker-compose.yml) | **Modificado.** Servicio `catalog-api`: `build`, `depends_on` sobre `db-init`, `ASPNETCORE_ENVIRONMENT` y `ConnectionStrings__CatalogDb`. Sin `ports:`, sin `healthcheck:`. |
| [../docker-compose.override.yml](../docker-compose.override.yml) | **Modificado.** El único `ports:` del servicio: `5125:8080`. |
| [../src/Services/Catalog/Catalog.API/Program.cs](../src/Services/Catalog/Catalog.API/Program.cs) | **Modificado.** `UseHttpsRedirection()` envuelto en `if (app.Environment.IsDevelopment())`. |

`.env.example` **no se tocó**: la decisión 8 hace que no haga falta ninguna variable nueva — `CATALOG_DB_PASSWORD` ya existía desde 0.4.

Otros archivos: la casilla de 1.6 en [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md), la fila en [README.md](README.md), y en [CLAUDE.md](../CLAUDE.md) el párrafo de estado de la Fase 1, la fila de la tabla de fases, la línea de *Local UIs* (dos URLs ahora: 5124 desde el IDE, 5125 desde el contenedor) y la sección *Commands*.

---

## Detalles que cuestan tiempo

### `APP_UID` está definido pero no aplicado

Ya está arriba (decisión 5) porque es una decisión, pero se repite aquí porque es lo que más se da por hecho: `Config.User` de `aspnet:10.0` está **vacío**. La imagen trae la variable para que la uses, no porque ya la haya usado. Copiar un `Dockerfile` de una plantilla y borrar la línea `USER $APP_UID` «porque la imagen ya es rootless» produce un contenedor que corre como root sin ningún aviso.

### `docker compose up -d` **no** reconstruye la imagen

Es la trampa más probable al iterar sobre este punto. Si la imagen ya existe, `up -d` la usa tal cual aunque el código haya cambiado; hace falta `--build`. El síntoma es un cambio que «no hace efecto» y media hora buscándolo en el sitio equivocado.

### `docker compose config` parte las líneas largas

Verificar que `${CATALOG_DB_PASSWORD}` interpola bien parece trivial hasta que la salida aparece cortada:

```
      ConnectionStrings__CatalogDb: Server=sqlserver,1433;Database=CatalogDb;User
        Id=catalog_user;Password=***;TrustServerCertificate=True
```

No está truncada ni mal formada: es plegado de YAML, y el valor real es una sola línea. Un `Select-String` sobre esa salida solo captura el primer trozo, así que hay que leer también la línea siguiente.

### `${...}` con un `$` en la contraseña

Compose interpola `${...}` en el YAML. Si la contraseña real del `.env` contuviera un `$`, se lo comería. El síntoma sería `Login failed for user 'catalog_user'` sin ninguna pista de por qué; el arreglo, escapar como `$$`. No pasa con la contraseña de ejemplo, pero es exactamente el tipo de fallo que cuesta una tarde.

### `/scalar` sin barra final devuelve 302

Ya medido en 1.5 y sigue igual dentro del contenedor: `curl` sin `-L` contra `/scalar` recibe un `302` hacia `/scalar/`, no un `200`. Verificar con la URL sin barra y leer «302» como fallo es un falso negativo.

### El orden del arranque se puede leer en la salida de `up`

No hace falta instrumentar nada para comprobar que la guarda de la decisión 8 funciona; Compose lo narra:

```
 Container shop133-sqlserver Waiting
 Container shop133-sqlserver Healthy
 Container shop133-db-init Starting
 Container shop133-db-init Started
 Container shop133-db-init Waiting
 Container shop133-db-init Exited
 Container shop133-catalog-api Starting
 Container shop133-catalog-api Started
```

`db-init Exited` **antes** de `catalog-api Starting` es la comprobación entera.

---

## Verificación

### Build de la imagen

```
$ docker compose build catalog-api
#5 [internal] load .dockerignore
#5 transferring context: 1.88kB done
#7 [internal] load build context
#7 transferring context: 201.92kB 0.0s done
...
#15 [build  7/10] RUN dotnet restore src/Services/Catalog/Catalog.API/Catalog.API.csproj
#15   Restored /src/src/Shared/Shop133.Contracts/Shop133.Contracts.csproj (in 90 ms).
#15   Restored /src/src/Services/Catalog/Catalog.Infrastructure/Catalog.Infrastructure.csproj (in 6.83 sec).
#15   Restored /src/src/Services/Catalog/Catalog.API/Catalog.API.csproj (in 6.85 sec).
#15 DONE 8.6s
#18 [build 10/10] RUN dotnet publish ... --configuration Release --no-restore --output /app/publish
#18   Catalog.API -> /app/publish/
#18 DONE 5.3s
#20 naming to docker.io/shop133/catalog-api:latest done
 Image shop133/catalog-api:latest Built
```

Tres proyectos restaurados, ni uno más — la decisión 3 en la salida.

### Arranque

```
$ docker compose up -d
 Container shop133-catalog-api Created
 Container shop133-sqlserver Waiting
 Container shop133-sqlserver Healthy
 Container shop133-db-init Starting
 Container shop133-db-init Started
 Container shop133-db-init Waiting
 Container shop133-db-init Exited
 Container shop133-catalog-api Starting
 Container shop133-catalog-api Started

$ docker compose ps -a
NAME                  IMAGE                        SERVICE       STATUS                    PORTS
shop133-catalog-api   shop133/catalog-api:latest   catalog-api   Up Less than a second     0.0.0.0:5125->8080/tcp
shop133-db-init       mcr.../mssql/server:2022     db-init       Exited (0)
shop133-jaeger        jaegertracing/all-in-one     jaeger        Up 24 hours               0.0.0.0:16686->16686/tcp, ...
shop133-rabbitmq      rabbitmq:4-management        rabbitmq      Up 24 hours (healthy)     0.0.0.0:5672->5672/tcp, ...
shop133-sqlserver     mcr.../mssql/server:2022     sqlserver     Up 24 hours (healthy)     0.0.0.0:1433->1433/tcp
```

### Endpoints

```
$ curl.exe -s -o NUL -w "%{http_code}" http://localhost:5125/<ruta>
products      200
categories    200
products/1    200
openapi       200
scalar        302     <- redirige a /scalar/
scalar/       200
```

Y los datos salen de verdad de `CatalogDb` a través de la red de compose:

```
$ curl.exe -s http://localhost:5125/products | ConvertFrom-Json
productos: 50
categorias: 5 -> Libretas, Llaveros, Pines, Playeras, Tazas
primero: TAZA-001 / Taza Talavera Puebla / Tazas
```

Los 50 productos del seed de 1.4, es decir: `Server=sqlserver` resuelve, `catalog_user` autentica y el esquema está aplicado.

### Logs — entorno, puerto y ausencia del warning de https

```
$ docker compose logs catalog-api
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://[::]:8080
info: Microsoft.Hosting.Lifetime[0]
      Application started. Press Ctrl+C to shut down.
info: Microsoft.Hosting.Lifetime[0]
      Hosting environment: Production
info: Microsoft.Hosting.Lifetime[0]
      Content root path: /app
info: Microsoft.EntityFrameworkCore.Database.Command[20101]
      Executed DbCommand (27ms) [Parameters=[], CommandType='Text', CommandTimeout='30']
      SELECT [p].[Id], [p].[CategoryId], [p].[Description], [p].[ImageUrl], [p].[Name], [p].[Price], [p].[Sku], [p].[Stock], [c].[Id], [c].[Name]
      FROM [Products] AS [p]
      INNER JOIN [Categories] AS [c] ON [p].[CategoryId] = [c].[Id]
      ORDER BY [p].[Id]
```

`Hosting environment: Production` con `/scalar/` respondiendo `200` es lo que valida a posteriori la decisión 3 de 1.5. Y no aparece ningún `Failed to determine the https port for redirect`: la decisión 10 funciona.

### Contenido de la imagen y usuario

```
$ docker compose exec catalog-api id
uid=1654(app) gid=1654(app) groups=1654(app)

$ docker compose exec catalog-api sh -c "ls /app | grep -iE 'scalar|design'"
Scalar.AspNetCore.dll

$ docker image ls shop133/catalog-api
IMAGE                        ID             DISK USAGE   CONTENT SIZE
shop133/catalog-api:latest   01515caca53b        370MB          104MB
```

`Scalar.AspNetCore.dll` está y ningún `*Design*`: los `PrivateAssets` de 1.2 y 1.5 hacen exactamente lo que decían que harían.

### Convivencia con la ejecución desde el IDE

Con el contenedor arriba, arrancando además el servicio en el host:

```
$ dotnet run --project src/Services/Catalog/Catalog.API --no-launch-profile   # ASPNETCORE_URLS=http://localhost:5124
--- host (IDE, 5124) ---      products 200
--- contenedor (5125) ---     products 200
```

### Nada roto en el lado del IDE

```
$ dotnet build
Build succeeded.
    0 Warning(s)
    0 Error(s)

$ dotnet test -- --filter-trait "Category=Fast"
Test run summary: Passed!
  total: 12
  failed: 0
  succeeded: 12
  skipped: 0
  duration: 1s 809ms
```

### Resumen

| # | Comprobación | Resultado |
|---|---|---|
| 1 | `docker compose build catalog-api` construye sin error, restaurando solo los 3 proyectos del grafo | ✓ |
| 2 | El `.dockerignore` recorta el contexto a 201,92 kB y excluye `.env` | ✓ |
| 3 | Un cambio en un `.cs` deja `dotnet restore` en `CACHED` | ✓ |
| 4 | `db-init` sale con `Exited (0)` **antes** de que `catalog-api` arranque | ✓ |
| 5 | `/products`, `/categories`, `/products/1`, `/openapi/v1.json` y `/scalar/` responden `200` (`/scalar`, `302`) | ✓ |
| 6 | `GET /products` devuelve los 50 productos del seed → `Server=sqlserver` + `catalog_user` funcionan desde la red de compose | ✓ |
| 7 | `Hosting environment: Production` y **ningún** warning de https en los logs | ✓ |
| 8 | El proceso corre como `uid=1654(app)`, no root | ✓ |
| 9 | `Scalar.AspNetCore.dll` en la imagen; ningún `*Design*` | ✓ |
| 10 | Contenedor (5125) e IDE (5124) sirven a la vez | ✓ |
| 11 | `dotnet build` con `0 Warning(s)` y la suite de arquitectura en 12 tests | ✓ |

---

## Pendiente

- **`Catalog.Tests` con `WebApplicationFactory` + Testcontainers** — es **1.7**, el punto que cierra la Fase 1. Su *fixture* levantará su propio SQL Server y llamará a `MigrateAsync()`, así que no reutiliza este contenedor; lo que sí hereda es la certeza de que el servicio arranca en `Production` sin User Secrets.
- **Aplicar migraciones sigue siendo manual desde el host** (decisión 6). El esquema de `CatalogDb` es un requisito previo del contenedor. Cuando la Fase 3 tenga cuatro servicios con cuatro bases, esto va a necesitar una respuesta mejor que «acuérdate»; el sitio natural para plantearla es **8.3**, junto al pipeline.
- **`healthcheck` y endpoint `/health`** — **8.4**. Hasta entonces `docker compose ps` solo puede decir `Up`, que no distingue «sirviendo» de «arrancado pero sin base de datos».
- **Tamaño de la imagen (370 MB)** — el intercambio de la decisión 4 se relee en **8.3**, cuando empujar la imagen a un registro haga que el tamaño cueste algo.
- **La superficie del API sigue expuesta a quien alcance el puerto**, ahora también desde el contenedor. Es la deuda que 1.5 dejó anotada y que se salda en la **Fase 5** (el Gateway decide qué expone) y en **8.1** (autenticación).
- **Los otros cuatro servicios no tienen `Dockerfile`.** El de `Orders.API` entra en la Fase 2; será una copia casi literal de este, y ese es el momento de plantearse si merece la pena factorizarlo o si duplicar veinte líneas es más barato que una abstracción.
- **Terminación TLS** — la decisión 10 la delega en el Gateway (**Fase 5**). Hasta entonces el contenedor solo habla HTTP.
