# Fase 0.4 — Configurar SQL Server con una base de datos por servicio

**Fecha:** 2026-08-17 · **Estado:** completado · **Roadmap:** [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md)

---

## Objetivo

Dejar creadas `CatalogDb`, `OrdersDb`, `InventoryDb` y `PaymentsDb` en el SQL Server que levantó [0.2](fase_0_2.md), de forma reproducible: `docker compose up -d` desde un repo recién clonado tiene que dejarlas listas, y `docker compose down -v` seguido de otro `up` tiene que volver a dejarlas igual.

Pero el objetivo real de este punto no es que existan cuatro bases — EF Core las crearía solo en la Fase 1.2 al aplicar la primera migración. Es que la **regla de arquitectura 1** de [CLAUDE.md](../CLAUDE.md) (*"No service opens a connection to another service's database"*) hoy solo vive en prosa:

- Los tests de arquitectura de 0.6 pueden comprobar que ningún proyecto referencia el `DbContext` de otro.
- Lo que **no** pueden comprobar es que alguien pegue un connection string ajeno en un `appsettings.json` y lea `CatalogDb` desde Orders. Eso compila, pasa los tests y funciona.

Así que 0.4 crea además **un login SQL por servicio**, con permisos únicamente sobre su propia base. La regla deja de depender de la disciplina de quien escribe el código y pasa a estar aplicada por el motor: `orders_user` no puede abrir `CatalogDb` aunque quiera.

**Fuera de alcance deliberadamente:** cablear los connection strings en los proyectos. Los cinco `.API` son todavía plantilla vacía, sin EF Core ni `DbContext`, así que un connection string ahí no podría verificarse contra nada. Eso entra en **1.2**, cuando Catalog monta EF Core de verdad. Este punto entrega las bases, los logins y la convención de connection string documentada (abajo).

---

## Decisiones

### 1. Un login por servicio, no `sa` para todos

Lo cómodo es que los cuatro servicios usen `sa`: una sola contraseña, la que ya está en `.env`, cero configuración extra.

**Descartado** porque deja la regla 1 sin ninguna barrera. Con `sa`, "cada servicio usa solo su base" es una convención que se rompe en silencio el día que alguien necesita "un join rápido" para un informe — que es exactamente el fallo que este proyecto existe para enseñar a evitar.

**Elegido:** cuatro logins (`catalog_user`, `orders_user`, `inventory_user`, `payments_user`), cada uno con un `USER` creado **solo dentro de su base** y `db_owner` ahí. Un login sin usuario en una base ajena ni siquiera puede conectarse a ella.

El coste son cuatro contraseñas más en `.env`/`.env.example` y un connection string por servicio en vez de uno compartido. A cambio, el fallo es inmediato y explícito:

```
Msg 916, Level 14, State 2
The server principal "catalog_user" is not able to access the database "OrdersDb"
under the current security context.
```

Ese mensaje es la regla 1 en forma ejecutable, igual que 0.6 lo será para las reglas 4 y 5.

**`db_owner` y no `db_datareader`/`db_datawriter`:** EF Core necesita crear tablas y escribir `__EFMigrationsHistory` desde 1.2. Un rol más estrecho obligaría a aplicar migraciones con `sa` y a mantener dos credenciales por servicio, lo que complica la Fase 1 sin aportar nada en local.

### 2. Servicio `db-init` en Compose, no un script manual

**Descartado — script de PowerShell que se ejecuta a mano.** Funciona la primera vez y se olvida en la segunda. El punto de partida del proyecto es "clonar y `docker compose up -d`"; un paso manual extra rompe eso y no queda registrado en ningún sitio.

**Descartado — dejar que EF Core cree las bases en 1.2.** `dotnet ef database update` crea la base si no existe, así que técnicamente sobraría este punto. Pero entonces las bases nacerían con `sa` (no habría logins que crear antes) y solo existirían las de los servicios que ya tienen EF Core: Inventory y Payments no tendrían base hasta la Fase 3. La separación de datos dejaría de ser un hecho de la infraestructura para ser un efecto secundario de las migraciones.

**Elegido:** un servicio `db-init` en `docker-compose.yml` con `depends_on: sqlserver: condition: service_healthy`, que ejecuta un script `.sql` idempotente y sale. Es exactamente el diseño que [0.2 dejó anticipado](fase_0_2.md#pendiente) al poner el healthcheck en `sqlserver` — sin él haría falta el clásico `wait-for-it.sh`, porque el contenedor de SQL Server acepta conexiones ~25s después de arrancar.

Va en `docker-compose.yml` y no en el override porque habla con SQL Server **por nombre de servicio** (`-S sqlserver`) y no publica ningún puerto: es comunicación contenedor-a-contenedor, la vista que define el archivo base según el split de 0.2.

### 3. Reutilizar la imagen de SQL Server, no `mssql-tools`

Lo estándar para un contenedor que solo ejecuta `sqlcmd` es `mcr.microsoft.com/mssql-tools`.

**Descartado:** esa imagen se quedó en `mssql-tools` 17 sobre Ubuntu 18.04 y arrastra el mismo problema de certificados que ya apareció en 0.2, con flags distintos.

**Elegido:** `mcr.microsoft.com/mssql/server:2022-latest`, la misma imagen que ya usa el servicio `sqlserver`. Trae `/opt/mssql-tools18/bin/sqlcmd` — el mismo binario que el healthcheck de 0.2 — así que no hay una segunda imagen que descargar, versionar ni mantener alineada.

El precio es que hay que **sobrescribir su `entrypoint`**: por defecto arranca el motor de SQL Server, no `sqlcmd`. Ver *Detalles*.

### 4. Sin `READ_COMMITTED_SNAPSHOT`, sin tocar collation

Se consideró activar RCSI en `OrdersDb`, donde en la Fase 4.5 vivirá el estado de la saga y habrá concurrencia real entre consumers.

**Descartado por ahora:** RCSI cambia el comportamiento de bloqueo de forma sutil, y activarlo *antes* de haber visto un problema de concurrencia convierte en invisible justo lo que la Fase 4 quiere hacer visible. El sitio donde esa decisión se toma con datos es **8.2**, cuya descripción ya menciona explícitamente "persistencia de la saga en SQL Server con concurrencia optimista".

Las bases se crean con la configuración por defecto del servidor (collation `SQL_Latin1_General_CP1_CI_AS`, recovery `FULL`). Nada de eso importa en desarrollo y cambiarlo ahora sería configuración sin justificación observada.

### 5. `db/init/` en la raíz

El script necesita una carpeta que no estaba en la estructura documentada del proyecto.

**Descartado:** `docker/sqlserver/init/`. Es más explícito sobre a quién pertenece el asset, pero crea un árbol de tres niveles para un archivo y anticipa una carpeta `docker/` que hoy no tendría ningún otro contenido — los dos `docker-compose*.yml` viven en la raíz.

**Elegido:** `db/init/01-create-databases.sql`, junto a los compose. El prefijo numérico deja sitio a scripts posteriores (seed, datos de prueba) manteniendo el orden de ejecución evidente. La carpeta queda añadida al árbol de estructura de [CLAUDE.md](../CLAUDE.md) y del roadmap.

---

## Cambios

| Archivo | Rol |
|---|---|
| [db/init/01-create-databases.sql](../db/init/01-create-databases.sql) | Script idempotente: 4 bases + 4 logins + 4 usuarios con `db_owner` en su base. |
| [docker-compose.yml](../docker-compose.yml) | Nuevo servicio `db-init`. Sin `ports:`, coherente con el split de 0.2. |
| [.env.example](../.env.example) | Bloque nuevo con las 4 contraseñas de servicio. |
| `.env` | Las mismas 4 variables, con valores locales. **No versionado.** |
| [CLAUDE.md](../CLAUDE.md) | Estado de fase, `db/init/` en el árbol, comando de arranque corregido y nota en la regla 1. |
| [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md) | 0.4 marcado, `db/` en el árbol de estructura. |

**Lo que quedó creado:**

| Base | Login | Permisos |
|---|---|---|
| `CatalogDb` | `catalog_user` | `db_owner` en `CatalogDb` |
| `OrdersDb` | `orders_user` | `db_owner` en `OrdersDb` |
| `InventoryDb` | `inventory_user` | `db_owner` en `InventoryDb` |
| `PaymentsDb` | `payments_user` | `db_owner` en `PaymentsDb` |

Ninguno tiene permisos a nivel servidor. `sa` sigue existiendo y es quien ejecuta el script de init.

**Convención de connection string** (referencia para 1.2 — no está cableada todavía en ningún proyecto). Mantiene la distinción host/contenedor que estableció 0.2:

| Desde | Connection string |
|---|---|
| Host (Visual Studio, `dotnet ef`) | `Server=localhost,1433;Database=CatalogDb;User Id=catalog_user;Password=…;TrustServerCertificate=True` |
| Contenedor (Fase 1+) | `Server=sqlserver,1433;Database=CatalogDb;User Id=catalog_user;Password=…;TrustServerCertificate=True` |

`TrustServerCertificate=True` es obligatorio: `Microsoft.Data.SqlClient` cifra la conexión por defecto desde la versión 4.0 y el certificado de SQL Server es autofirmado. Es el mismo motivo por el que `sqlcmd` necesita `-C`. Las credenciales irán en User Secrets, nunca en `appsettings.json`.

---

## Detalles que cuestan tiempo

**El `entrypoint` de la imagen hay que sobrescribirlo.** `mcr.microsoft.com/mssql/server` arranca el motor de SQL Server por defecto. Sin `entrypoint:`, el contenedor `db-init` levantaría un segundo SQL Server dentro de la misma red en lugar de ejecutar el script — y sin error visible, porque arrancar es justo lo que esa imagen sabe hacer.

**`$$` otra vez, y por el mismo motivo que en 0.2.** `-P "$$MSSQL_SA_PASSWORD"` hace que la variable la resuelva el shell del contenedor. La alternativa (`-P "${MSSQL_SA_PASSWORD}"`, interpolada por Compose) funciona, pero deja la contraseña en texto plano en la salida de `docker compose config` — que es justo lo que se evitó al sacarla del YAML.

Consecuencia: hace falta un shell **de verdad**, de ahí `entrypoint: ["/bin/bash", "-c"]`. Un `command:` a secas no expande nada.

**El flag `-b` de `sqlcmd` no es opcional.** Sin él, `sqlcmd` sale con código 0 aunque el script falle, el contenedor queda `Exited (0)` y el fallo pasa completamente desapercibido. Comprobado:

```powershell
sqlcmd ... -b -Q "SELECT 1/0"   # -> exit code 1
sqlcmd ...    -Q "SELECT 1/0"   # -> exit code 0   <- el mensaje de error sale igual
```

En ambos casos se imprime `Msg 8134 ... Divide by zero`. Lo único que cambia es si Docker se entera.

**`USE` necesita su propio batch.** El contexto de base se resuelve al *compilar* el batch, no al ejecutarlo. Si `USE CatalogDb;` y el `CREATE USER` van en el mismo batch, el usuario se crea en `master`. De ahí que el script alterne `USE <base>` / `GO` / `CREATE USER` y vuelva a `USE master` antes de cada bloque siguiente.

**Qué necesita guarda de idempotencia y qué no.** `CREATE DATABASE`, `CREATE LOGIN` y `CREATE USER` fallan si el objeto existe, así que van detrás de `IF DB_ID(...) IS NULL` / `IF NOT EXISTS (SELECT 1 FROM sys.server_principals ...)`. `ALTER ROLE ... ADD MEMBER` **ya es idempotente** y no necesita ninguna.

**`CHECK_POLICY = ON` en los logins.** Obliga a que las cuatro contraseñas de servicio cumplan la política de Windows/SQL Server, igual que la de `sa`. Si una no la cumple, `db-init` sale con error y el mensaje **no menciona la política** — es el primer sitio donde mirar si el script falla creando un login.

**`.env` es local: hay que actualizarlo a mano.** `.env.example` está versionado y trae las cuatro variables nuevas, pero `.env` no. Sin ellas, Compose avisa con `variable is not set` y `sqlcmd` corta con `'CATALOG_DB_PASSWORD' scripting variable not defined`.

**`docker compose up -d sqlserver rabbitmq jaeger` se salta `db-init`.** Con lista explícita de servicios, Compose arranca solo los nombrados. El comando de arranque en [CLAUDE.md](../CLAUDE.md) llevaba esa lista y se corrigió a `docker compose up -d` a secas.

**`docker compose ps` no muestra `db-init`.** Una vez terminado no es un contenedor en ejecución; hace falta `docker compose ps -a` para ver el `Exited (0)`.

**`docker compose up -d` vuelve cuando `db-init` *arranca*, no cuando *termina*.** Encontrado en carne propia: un `sqlcmd` lanzado justo después del `up` falló con `Login failed for user 'inventory_user'`, porque el script iba por `OrdersDb` y todavía no había creado ese login. No es un fallo del script — es que `up -d` no espera a que un contenedor de tarea acabe. El script tarda ~5s.

Esto importa en la **Fase 1**: cuando los servicios de aplicación entren en el compose, no basta con `depends_on: sqlserver`. Necesitan esperar a que las bases existan:

```yaml
depends_on:
  db-init:
    condition: service_completed_successfully
```

Ese `condition` es justo el que existe para contenedores de init, y es la razón de que `db-init` lleve `-b` (sin él saldría 0 aunque fallara, y los servicios arrancarían contra bases inexistentes creyendo que todo fue bien).

---

## Verificación

Ejecutado el 2026-08-17. Salidas reales:

| Check | Resultado |
|---|---|
| `docker compose config` | válido, 4 servicios: sqlserver, db-init, jaeger, rabbitmq |
| `docker compose -f docker-compose.yml config` | ningún `published:` — el split de 0.2 sigue intacto |
| `docker compose down -v` + `up -d` | `sqlserver Healthy` → `db-init Started` (el `depends_on` funciona) |
| `docker compose ps -a` | `shop133-db-init  Exited (0)`, el resto `Up` / `(healthy)` |
| `docker inspect --format "{{.State.ExitCode}}"` | `0` |
| `SELECT name FROM sys.databases WHERE database_id > 4` | `CatalogDb`, `InventoryDb`, `OrdersDb`, `PaymentsDb` |
| `orders_user` → `SELECT DB_NAME(), IS_ROLEMEMBER('db_owner')` | `OrdersDb`, `1` |
| `orders_user` → `-d CatalogDb` | **rechazado**: `Cannot open database "CatalogDb" requested by the login` |
| `catalog_user` → `USE OrdersDb` | **rechazado**: `Msg 916 ... not able to access the database "OrdersDb"` |
| `docker compose up -d --force-recreate db-init` | segunda pasada, `Exited (0)`, mismas 4 bases |
| Conexión desde el **host** con `catalog_user` | `CatalogDb / catalog_user / db_owner=1` |
| Conexión desde el **host**, `catalog_user` → `OrdersDb` | **rechazada**, mismo error |
| `-b` con `SELECT 1/0` | exit 1 con el flag, exit 0 sin él |
| `dotnet build` | Build succeeded, 0 warnings, 0 errors |
| `git check-ignore .env` / `.env.example` | `.env` ignorado (`.gitignore:7`), `.env.example` versionado |

Las dos filas que dan sentido al punto son las de rechazo. Comprobadas desde dentro del contenedor y desde el host, y por las dos vías por las que un servicio puede intentar llegar a una base ajena: en el connection string (`-d CatalogDb` → `Msg 4060` al conectar) y una vez conectado (`USE OrdersDb` → `Msg 916`).

### Problema encontrado

El primer `docker compose config` reveló que `command` estaba mal escrito. Se había puesto como string multilínea:

```yaml
command: >
  /opt/mssql-tools18/bin/sqlcmd -S sqlserver -U sa -P "$$MSSQL_SA_PASSWORD"
  -C -b -i /db-init/01-create-databases.sql
```

Compose **parte un `command` en formato string usando reglas tipo shell y lo convierte en `argv`**:

```yaml
command:
  - /opt/mssql-tools18/bin/sqlcmd
  - -S
  - sqlserver
  ...
```

Con `entrypoint: ["/bin/bash", "-c"]` eso es fatal: `bash -c` toma **solo su primer argumento** como el comando a ejecutar y el resto los asigna a `$0`, `$1`… Es decir, se habría ejecutado `sqlcmd` sin un solo argumento, y el resto se habría descartado en silencio.

La solución es escribir `command` como una **lista de un único elemento**, para que todo el comando llegue junto:

```yaml
command:
  - >
    /opt/mssql-tools18/bin/sqlcmd -S sqlserver -U sa -P "$$MSSQL_SA_PASSWORD"
    -C -b -i /db-init/01-create-databases.sql
```

Confirmado con `docker compose config`, que ahora muestra un solo elemento. Merece la pena revisar ahí cualquier `command` combinado con `entrypoint: [..., "-c"]`: el YAML se ve idéntico y el comportamiento no lo es.

---

## Pendiente

De la Fase 0 quedan:

- **0.5** — convención de branches (`main`, `develop`, `feature/*`). La rama actual es `feature_fase_0`, que no sigue todavía ninguna convención acordada.
- **0.6** — `tests/Shop133.ArchitectureTests` con NetArchTest.

**Consecuencias de este punto en fases posteriores:**

- **1.2** — Catalog.API monta EF Core contra `CatalogDb` **con `catalog_user`**, no con `sa`. El connection string va a User Secrets. Si una migración falla por permisos, el rol es `db_owner` y debería bastar; el sospechoso sería la base equivocada en el connection string.
- **1.6 y siguientes** — cuando los servicios corran en contenedores, el connection string cambia a `Server=sqlserver,1433` (mismo login, mismo password, distinta forma de alcanzar el servidor) y cada uno necesita `depends_on: db-init: condition: service_completed_successfully`. Ver *Detalles*.
- **4.5** — el estado de la saga se persiste en `OrdersDb` con `orders_user`. MassTransit crea su propia tabla, para lo que `db_owner` es suficiente.
- **8.2** — los tests con Testcontainers levantan **su propio** SQL Server, no este. Este script no aplica ahí; si esos tests necesitan los mismos logins, habrá que reutilizar `db/init/01-create-databases.sql` desde el fixture. Es también el punto donde se revisará si `OrdersDb` necesita `READ_COMMITTED_SNAPSHOT`.
