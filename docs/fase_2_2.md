# Fase 2.2 — EF Core contra `OrdersDb`

**Fecha:** 2026-08-24 · **Estado:** completado · **Roadmap:** [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md)

---

## Objetivo

`2.1` dejó escritas [`Order`](../src/Services/Orders/Orders.Domain/Entities/Order.cs), [`OrderItem`](../src/Services/Orders/Orders.Domain/Entities/OrderItem.cs) y [`OrderStatus`](../src/Services/Orders/Orders.Domain/Entities/OrderStatus.cs), y **ninguna forma de persistirlas**: `Orders.Infrastructure` no tenía un solo `PackageReference`, y `OrdersDb` existía en SQL Server desde `0.4` pero vacía. Este punto es el espejo de lo que `1.2` hizo con `CatalogDb`.

Va **antes** de `2.3` (`POST /orders`) por el mismo motivo por el que `1.2` fue antes que `1.3`: el esquema decide cosas que la entidad no puede decidir sola. Aquí la principal es la que `2.1` dejó explícitamente aparcada, por escrito, en la nota de `Order.Items`:

> *Si en la base se mapea con clave sombra sobre una entidad normal o como `OwnsMany` lo decide 2.2, que es donde vive la persistencia; igual que 1.1 dejó el índice único del Sku para 1.2.*

Es también el primer punto en el que se usa de verdad el login `orders_user` que creó `0.4`.

**Fuera de alcance deliberadamente:** el endpoint y la llamada síncrona a Catalog (`2.3`), los tests con WireMock (`2.4`), Dockerfile y servicio de compose para Orders (no están en el roadmap de la Fase 2) y la persistencia del **estado de la saga** (`4.5`), que es otra tabla y otro tipo. Las dos tablas quedan creadas y **vacías**.

---

## Decisiones

### 1. `OrderItem` se mapea como tipo *owned* (`OwnsMany`), no como entidad con clave sombra

Es la decisión que da nombre a este punto. `OrderItem` no tiene `Id`, no tiene `OrderId` y no tiene navegación de vuelta al pedido — `2.1` lo justificó así: *una línea de pedido no tiene identidad fuera de su pedido; nadie la pide por id y ningún mensaje de `Shop133.Contracts` la referencia*.

**Descartado — entidad normal con clave sombra.** `HasMany(o => o.Items).WithOne().HasForeignKey("OrderId")`, más un `Property<int>("Id")` y un `HasKey("Id")` en la sombra. Produce casi la misma tabla y deja la puerta abierta a consultar líneas sueltas si algún día hace falta. Precisamente por eso se descarta: esa puerta abierta contradice la frase de la entidad, y el coste se paga en cada consulta — `Include(o => o.Items)` en todas, y un pedido cargado sin él aparece con cero líneas en vez de fallar.

**Elegido:** `OwnsMany`. Lo que se gana:

- EF **impide** consultar `OrderItem` por su cuenta. La garantía deja de ser una convención escrita en un comentario.
- Las líneas se cargan **siempre** con el pedido, sin `Include`. Un olvido menos en `2.3` y en `6.5`, y de los que no dan error.
- El borrado en cascada sale del propio mapeo, no de una llamada a `OnDelete` que alguien pueda cambiar.

Lo que se paga: la PK de `OrderItems` es compuesta, `(OrderId, Id)`, con un `Id` `IDENTITY` que **no existe en C#** y no se puede usar para nada. Es una consecuencia buscada, no un efecto colateral: es exactamente la afirmación "esta fila no tiene identidad propia" escrita en el esquema.

### 2. `ValueGeneratedNever()` sobre la PK — la línea más importante del archivo

El `Guid` lo acuña el constructor de `Order`, no la base. Es la decisión 4 de [fase_0_3.md](fase_0_3.md) y sigue viva: el `Id` es la clave de correlación de la saga, así que Orders.API tiene que poder publicar `OrderCreated` sin haber esperado a un `INSERT`.

**Descartado — dejar la convención.** Para una PK `Guid`, EF aplica `ValueGeneratedOnAdd` con un generador cliente. Hoy no cambiaría nada observable: EF solo genera cuando encuentra `Guid.Empty`, y el constructor nunca deja eso. Se descarta igual porque el modelo estaría **declarando lo contrario de lo que hace el código** — que el valor lo pone otro — y en `4.5` esa mentira se cobra: la saga correlaciona por un valor que el modelo declara ajeno.

Se comprobó que la migración sale **sin** `DEFAULT NEWID()` ni `NEWSEQUENTIALID()`, y que el `Guid` acuñado por la entidad es literalmente el que vuelve de la base (ver *Verificación*, comprobación 5).

### 3. `Ignore()` sobre `Order.Total` y `OrderItem.Subtotal`

`2.1` ya lo dejó anotado: son propiedades calculadas, de solo lectura, sin campo de respaldo. Sin `Ignore()` EF no genera una columna de más — **no llega ni a construir el modelo**, porque no tiene forma de materializarlas. Es de los pocos sitios donde el fallo por omisión sería inmediato y ruidoso; se declara igual, porque el motivo (*una sola fuente de verdad, imposible de desincronizar*) merece estar escrito donde se lee el esquema.

Se verificó que las tablas no tienen columna `Total` ni `Subtotal` (comprobación 4).

### 4. La colección se escribe por el campo `_items`, declarado a mano

`Order.Items` devuelve `_items.AsReadOnly()`, o sea un `ReadOnlyCollection` **nuevo en cada lectura**, cuyo `Add` lanza `NotSupportedException` — el hallazgo medido en `2.1`. Si EF materializara las líneas a través de la propiedad, cargar un pedido reventaría.

**Descartado — confiar en la convención.** EF ya prefiere el campo de respaldo cuando encuentra uno que se llame `_items` frente a la navegación `Items`, y de hecho funciona. Se descarta porque esa preferencia depende de una **coincidencia de nombres que nada vigila**: renombrar el campo privado es un refactor que ningún compilador discute, y el fallo aparecería en la primera lectura de un pedido, en tiempo de ejecución.

**Elegido:** `Navigation(o => o.Items).HasField("_items").UsePropertyAccessMode(PropertyAccessMode.Field)`. Mismo criterio que el resto del archivo — todo declarado a mano aunque coincida con la convención, porque la configuración es el sitio donde se lee el esquema.

### 5. Sin índices más allá de las dos PKs

**Descartado — índice sobre `CustomerEmail`** ("algún día habrá un *mis pedidos*"), y sobre `Status`/`CreatedAt` ("algún día habrá un listado"). Hoy no existe ninguna de las dos consultas: `2.3` solo inserta y `6.5` lee por id. Un índice cuesta escrituras y espacio desde el primer `INSERT`, y este proyecto ya tiene por norma no inventar una firma antes que su caso de uso (`1.1` con `Update()`, `2.1` con `Confirm()`).

El contraste con `1.2` es la parte instructiva: allí el índice único sobre `Sku` **sí** entró antes que su endpoint, porque no era una optimización sino una **invariante** que la entidad no podía sostener sola. Un índice de rendimiento y un índice de corrección no se deciden con el mismo criterio.

### 6. La PK sobre el `Guid` se queda *clustered* — y con la nota puesta

SQL Server pone la PK como índice clustered por defecto, y sobre un `uniqueidentifier` aleatorio eso fragmenta.

**Descartado — `IsClustered(false)` más un clustered sobre `CreatedAt`,** que es la receta habitual. Es optimizar sin haber medido nada sobre una tabla que hoy tiene cero filas. Y conviene recordar por qué el remedio *popular* no sirve: la sonda de [fase_1_1.md](fase_1_1.md) midió que SQL Server compara `uniqueidentifier` empezando por los **últimos 6 bytes**, justo donde un UUID v7 pone su parte aleatoria — así que "usa v7 y el clustered deja de fragmentarse" es falso en este motor.

La pregunta la dejó abierta la sección *Pendiente* de [fase_1_2.md](fase_1_2.md) para `4.5`, que es cuando `OrdersDb` reciba escrituras de verdad. Aquí solo se deja el motivo escrito en `OrderConfiguration`, para que en `4.5` no haya que redescubrirlo.

### 7. Lo mismo que en Catalog, por los mismos motivos

Tres decisiones que se heredan de `1.2` sin volver a discutirlas, y que se listan para que se vea que fueron deliberadas y no copiadas por inercia: **configuración en un `IEntityTypeConfiguration` aparte** (aquí no es solo estilo — Orders.Domain no *puede* referenciar EF Core, lo prohíbe la regla 5 y lo comprueba un test), **`ApplyConfiguration` explícito** en vez del escaneo por reflexión, y **el connection string entero en User Secrets**, sin plantilla en `appsettings.json`.

### 8. EF Core **10.0.8**, la misma que Catalog

Mantiene la decisión 5 de `1.2`: es la versión de la herramienta global `dotnet-ef` instalada. Alinear Orders con `10.0.11` habría dejado los dos servicios en versiones distintas y habría hecho que `dotnet ef` avisara en cada comando. La asimetría con el `10.0.11` de `Microsoft.AspNetCore.OpenApi` sigue siendo deliberada.

---

## Cambios

| Archivo | Rol |
|---|---|
| [Orders.Infrastructure.csproj](../src/Services/Orders/Orders.Infrastructure/Orders.Infrastructure.csproj) | Primer paquete del proyecto: `Microsoft.EntityFrameworkCore.SqlServer` 10.0.8. |
| [Orders.API.csproj](../src/Services/Orders/Orders.API/Orders.API.csproj) | `Microsoft.EntityFrameworkCore.Design` 10.0.8 con `PrivateAssets="all"`, y el `<UserSecretsId>` que añadió `dotnet user-secrets init`. |
| [Persistence/OrdersDbContext.cs](../src/Services/Orders/Orders.Infrastructure/Persistence/OrdersDbContext.cs) | El `DbContext` de `OrdersDb`. Un solo `DbSet<Order>` y una sola llamada a `ApplyConfiguration`. |
| [Persistence/Configurations/OrderConfiguration.cs](../src/Services/Orders/Orders.Infrastructure/Persistence/Configurations/OrderConfiguration.cs) | El mapeo de `Order` y, dentro, el bloque `OwnsMany` de `OrderItem`. |
| `Migrations/20260824194425_InitialCreate.cs` (+ `.Designer.cs`) | La primera migración de Orders. Generada. |
| `Migrations/OrdersDbContextModelSnapshot.cs` | El snapshot contra el que EF calcula la siguiente migración. Generado. |
| [Orders.API/Program.cs](../src/Services/Orders/Orders.API/Program.cs) | Guarda sobre `ConnectionStrings:OrdersDb` + `AddDbContext<OrdersDbContext>` con `UseSqlServer`. |
| [CLAUDE.md](../CLAUDE.md), [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md), [docs/README.md](README.md) | Estado de la fase, checkbox del roadmap e índice. |

**Sin tocar:** las tres entidades de `Orders.Domain` — `2.1` las dejó listas para EF y no hizo falta ni un cambio, que era la apuesta de sus constructores privados sin parámetros. Tampoco `Orders.Domain.csproj` (sigue con cero paquetes y una sola `ProjectReference`), `Shop133.Contracts`, `appsettings*.json`, `docker-compose*.yml`, `db/init/`, `.env.example` ni los tests. **No se añadió ninguna regla de arquitectura**, así que la suite sigue en **12**.

---

## Detalles que cuestan tiempo

### El `Id` `IDENTITY` de `OrderItems` es de EF, no una decisión propia

El SQL de un `OwnsMany` sorprende la primera vez:

```sql
CREATE TABLE [OrderItems] (
    [OrderId] uniqueidentifier NOT NULL,
    [Id] int NOT NULL IDENTITY,
    ...
    CONSTRAINT [PK_OrderItems] PRIMARY KEY ([OrderId], [Id]),
    CONSTRAINT [FK_OrderItems_Orders_OrderId] FOREIGN KEY ([OrderId])
        REFERENCES [Orders] ([Id]) ON DELETE CASCADE
);
```

Ese `[Id]` no lo pidió nadie: EF construye la clave de una colección owned como `(FK al dueño, propiedad Id generada)`. No es accesible desde C# ni aparece en `OrderItem`. Conviene saberlo antes de mirar la tabla y pensar que la configuración tiene un error — y conviene no "arreglarlo".

### `docker exec` con `sqlcmd` desde Git Bash: la ruta se traduce sola

Lanzando la comprobación del esquema desde la herramienta Bash:

```
OCI runtime exec failed: exec: "C:/Program Files/Git/opt/mssql-tools18/bin/sqlcmd":
stat C:/Program Files/Git/opt/mssql-tools18/bin/sqlcmd: no such file or directory
```

No es un problema de Docker ni de la imagen. Git Bash convierte cualquier argumento que empiece por `/` en una ruta de Windows *antes* de que Docker lo vea, así que `/opt/mssql-tools18/...` llega ya destrozado. Se resuelve con `MSYS_NO_PATHCONV=1` delante del comando, o ejecutando desde PowerShell, donde no ocurre. Merece la pena recordarlo porque el mensaje de error apunta a la imagen, que es el sitio equivocado donde mirar.

### La protección de `AsReadOnly()` sobrevive a EF, y eso había que comprobarlo

`2.1` midió que castear `Order.Items` a `ICollection<OrderItem>` y hacer `Add` es posible si el getter devuelve el `List` directamente, e imposible con `AsReadOnly()`. Lo que ese punto no podía comprobar es qué pasa **después de que EF materialice el pedido**: EF rellena `_items` por el campo, y quedaba por ver si la propiedad seguía envolviendo la lista o si EF la reemplazaba por algo suyo. Sobre un pedido leído de la base, el `Add` por la espalda sigue lanzando `NotSupportedException` (comprobación 5).

### Verificar el mapeo sin endpoint: un arnés fuera del repositorio

`2.3` es quien traerá el `POST /orders`, así que en este punto no hay ninguna vía por la que la aplicación escriba un pedido. Y las comprobaciones por `sqlcmd` prueban el **esquema**, no el **mapeo**: no dicen nada sobre si EF materializa las líneas por el campo, ni si el `Guid` sobrevive al viaje.

Se resolvió con un proyecto de consola desechable **en el directorio temporal, fuera del repositorio**, referenciando `Orders.Infrastructure.csproj` por ruta absoluta, y se borró al terminar. No se añadió al `.slnx` ni a `src/`; los tests de arquitectura enumeran `<repo>/src`, así que nunca llegó a verlo. Es el mismo hueco que `1.2` tuvo entre la migración y los endpoints, resuelto allí con `INSERT` a mano — aquí no bastaba, porque lo que había que probar era precisamente el código C# de mapeo.

Su contenido queda transcrito en la comprobación 5 por si hay que repetirlo; en `2.4` esto lo cubrirán tests de verdad.

---

## Verificación

### 1. Compila y los archivos nuevos los recoge el glob implícito

```powershell
dotnet build src/Services/Orders/Orders.API/Orders.API.csproj
```

```
  Shop133.Contracts -> ...\Shop133.Contracts.dll
  Orders.Domain -> ...\Orders.Domain.dll
  Orders.Infrastructure -> ...\Orders.Infrastructure.dll
  Orders.API -> ...\Orders.API.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### 2. La migración, y el SQL revisado **antes** de aplicarlo

```powershell
dotnet ef migrations add InitialCreate `
  --project src/Services/Orders/Orders.Infrastructure `
  --startup-project src/Services/Orders/Orders.API

dotnet ef migrations script `
  --project src/Services/Orders/Orders.Infrastructure `
  --startup-project src/Services/Orders/Orders.API
```

Sin `--no-build`, por el gotcha que midió `1.2`. Salida (recortada la cabecera de `__EFMigrationsHistory`):

```sql
CREATE TABLE [Orders] (
    [Id] uniqueidentifier NOT NULL,
    [CustomerEmail] nvarchar(320) NOT NULL,
    [Status] int NOT NULL,
    [CreatedAt] datetimeoffset NOT NULL,
    CONSTRAINT [PK_Orders] PRIMARY KEY ([Id])
);

CREATE TABLE [OrderItems] (
    [OrderId] uniqueidentifier NOT NULL,
    [Id] int NOT NULL IDENTITY,
    [ProductId] int NOT NULL,
    [ProductSku] nvarchar(50) NOT NULL,
    [ProductName] nvarchar(200) NOT NULL,
    [Quantity] int NOT NULL,
    [UnitPrice] decimal(18,2) NOT NULL,
    CONSTRAINT [PK_OrderItems] PRIMARY KEY ([OrderId], [Id]),
    CONSTRAINT [FK_OrderItems_Orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [Orders] ([Id]) ON DELETE CASCADE
);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260824194425_InitialCreate', N'10.0.8');
```

Las tres cosas que había que mirar, y salieron: `[Id] uniqueidentifier NOT NULL` **sin `DEFAULT`** (decisión 2), `ON DELETE CASCADE` sobre la FK al dueño (decisión 1) y **ninguna columna** `Total` ni `Subtotal` (decisión 3). Tampoco hubo aviso de versión de herramienta, que es lo que confirma la decisión 8.

### 3. Aplicada contra `OrdersDb`

```powershell
dotnet ef database update `
  --project src/Services/Orders/Orders.Infrastructure `
  --startup-project src/Services/Orders/Orders.API
```

```
info: Microsoft.EntityFrameworkCore.Migrations[20402]
      Applying migration '20260824194425_InitialCreate'.
info: Microsoft.EntityFrameworkCore.Database.Command[20101]
      Executed DbCommand (5ms) ... CREATE TABLE [Orders] (...)
info: Microsoft.EntityFrameworkCore.Database.Command[20101]
      Executed DbCommand (6ms) ... CREATE TABLE [OrderItems] (...)
Done.
```

### 4. El esquema real, consultado **con `orders_user`** (no con `sa`)

```powershell
docker exec shop133-sqlserver /opt/mssql-tools18/bin/sqlcmd `
  -S localhost -U orders_user -P "<ORDERS_DB_PASSWORD>" -C -d OrdersDb -Q "..."
```

```
table        col              type               len         prec scale nullable identity_
------------ ---------------- ------------------ ----------- ---- ----- -------- ---------
Orders       Id               uniqueidentifier             8    0     0 NO       no
Orders       CustomerEmail    nvarchar                   320    0     0 NO       no
Orders       Status           int                          2   10     0 NO       no
Orders       CreatedAt        datetimeoffset               5   34     7 NO       no
OrderItems   OrderId          uniqueidentifier             8    0     0 NO       no
OrderItems   Id               int                          2   10     0 NO       YES
OrderItems   ProductId        int                          2   10     0 NO       no
OrderItems   ProductSku       nvarchar                    50    0     0 NO       no
OrderItems   ProductName      nvarchar                   200    0     0 NO       no
OrderItems   Quantity         int                          2   10     0 NO       no
OrderItems   UnitPrice        decimal                      4   18     2 NO       no

table        idx                          kind         is_unique is_primary_key
------------ ---------------------------- ------------ --------- --------------
Orders       PK_Orders                    CLUSTERED            1              1
OrderItems   PK_OrderItems                CLUSTERED            1              1

fk                               on_delete
-------------------------------- ------------
FK_OrderItems_Orders_OrderId     CASCADE

migration                                ef
---------------------------------------- ----------
20260824194425_InitialCreate             10.0.8
```

Que la consulta funcione con `orders_user` es media comprobación: la migración se aplicó con el login del servicio, con `db_owner` sobre `OrdersDb` y sin acceso a las otras tres bases. Once columnas y ni una de más — sin `Total` ni `Subtotal`. Dos índices y ninguno de rendimiento (decisión 5).

### 5. El *round-trip* de verdad, por EF

Con el arnés desechable descrito en *Detalles*. Escribe un pedido de dos líneas con un contexto, lo lee con **otro contexto nuevo** (sin `ChangeTracker` caliente, que es lo que haría pasar el test por accidente) y lo borra:

```
Id acunado por la entidad : a4f091ee-4739-420c-b669-fccf317b2939
Total calculado           : 476.50
SaveChangesAsync          : OK
Id leido == Id acunado    : True
CustomerEmail             : 'ana@example.com' (trim del constructor)
Status                    : Pending
CreatedAt (Kind/offset)   : 2026-08-24T19:49:17.7809067+00:00
Lineas materializadas     : 2 (sin Include)
Total recalculado         : 476.50
  TAZA-001   x2 @ 149.00 = 298.00
  LLAV-001   x3 @ 59.50 = 178.50
Items mutable por la espalda: no (NotSupportedException)
Tras el DELETE -> pedidos : 0
Tras el DELETE -> lineas  : 0
```

Cada línea responde a una decisión: el `Guid` vuelve **idéntico** al que acuñó el constructor (decisión 2), las dos líneas se materializan **sin `Include`** (decisión 1), `Total` y `Subtotal` se recalculan y no vienen de ninguna columna (decisión 3), y el `Add` por la espalda sigue lanzando `NotSupportedException` sobre un pedido **leído de la base** (decisión 4). El email entró como `"  ana@example.com  "` y volvió recortado, lo que confirma que el `Trim()` del constructor es lo que se persistió.

### 6. El `CASCADE` real, a mano

El punto 5 borra por EF, que emite sus propios `DELETE`. Para comprobar que la restricción está de verdad en el motor, un `DELETE` solo sobre `Orders` por `sqlcmd`:

```
--- el pedido con sus lineas ---
email                Status      sku        linea       qty         UnitPrice   subtotal
-------------------- ----------- ---------- ----------- ----------- ----------- -----------
ana@example.com                1 TAZA-001             1           2      149.00      298.00
ana@example.com                1 LLAV-001             2           3       59.50      178.50

--- DELETE solo del pedido ---
pedidos     lineas
----------- -----------
          0           0
```

Las dos líneas desaparecieron sin que nadie las nombrara. Las tablas quedan **vacías**: los datos los mete `2.3`.

### 7. Los tests de arquitectura

```powershell
tests\Shop133.ArchitectureTests\bin\Debug\net10.0\Shop133.ArchitectureTests.exe
```

```
=== TEST EXECUTION SUMMARY ===
   Shop133.ArchitectureTests  Total: 12, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.916s
```

Se usa el ejecutable porque `dotnet test` sigue roto en esta máquina desde el SDK 10.0.400 — no es cosa de este punto, está descrito en CLAUDE.md. Los dos tests que este punto pone a prueba de verdad:

- `EfCorePackages_LiveOnlyIn_InfrastructureProjects` — `.SqlServer` en `Orders.Infrastructure` pasa; `.Design` en `Orders.API` es la excepción codificada en el propio test.
- `OrdersDomain_ProjectReferences_ContainOnlyContracts` — sigue en verde porque `Orders.Domain` no se tocó. Es lo que obliga a que la configuración de EF viva en `Orders.Infrastructure` y no como anotaciones sobre las entidades (decisión 7).

### 8. El host arranca con el `DbContext` registrado

```powershell
dotnet run --project src/Services/Orders/Orders.API --launch-profile http
```

```
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://localhost:5189
info: Microsoft.Hosting.Lifetime[0]
      Application started. Press Ctrl+C to shut down.
info: Microsoft.Hosting.Lifetime[0]
      Hosting environment: Development
info: Microsoft.Hosting.Lifetime[0]
      Content root path: C:\personalprojects\shop133\src\Services\Orders\Orders.API
```

Que arranque es la comprobación: la guarda de `Program.cs` lanza **antes** de `app.Build()`, así que un connection string ausente habría matado el host aquí y no en la primera petición.

`GET http://localhost:5189/openapi/v1.json` devolvió **200** (182 bytes — un documento vacío, sin un solo path: todavía no existe ningún controller). Y solo **después** de esa petición apareció en el log:

```
warn: Microsoft.AspNetCore.HttpsPolicy.HttpsRedirectionMiddleware[3]
      Failed to determine the https port for redirect.
```

Detalle que conviene no confundir: ese `warn` **no sale al arrancar**, sale una vez **por petición** — es middleware, no arranque. Es el mismo que tuvo Catalog hasta que `1.6` puso `UseHttpsRedirection()` detrás de un `IsDevelopment()`. Aquí se deja tal cual: el perfil `http` no publica puerto HTTPS y Orders todavía no tiene contenedor, así que no hay nada que la guarda arregle hoy.

---

## Pendiente

- **2.3** — el `POST /orders`. Inyectará `OrdersDbContext`, y tendrá dos trabajos que este punto le deja: **agrupar las líneas por `ProductId` antes de construir el `Order`** (el agregado solo afirma la invariante, no la arregla) y **rellenar los tres campos congelados** — `ProductSku`, `ProductName`, `UnitPrice` — con lo que devuelva la llamada síncrona a Catalog. También el `[EmailAddress]` sobre el DTO de entrada, que `2.1` dejó fuera de la entidad a propósito.
- **2.4** — los tests con WireMock. Es donde el arnés desechable de la comprobación 5 se convierte en algo versionado, y donde el mapeo `OwnsMany` pasa a tener una red debajo.
- **Índices**, cuando exista la consulta que los pida — probablemente `6.5` (estado del pedido) y un eventual "mis pedidos" por `CustomerEmail`. Decisión 5.
- **4.5** — dos cosas distintas caen aquí. La primera, si la PK `uniqueidentifier` debe seguir siendo *clustered* (decisión 6). La segunda, la tabla de estado de la **saga**, que no es esta: MassTransit persiste su propia instancia, con su token de concurrencia optimista, y habrá que decidir si comparte `OrdersDbContext` o tiene el suyo.
- **Un contenedor para Orders.API**, con su `ConnectionStrings__OrdersDb` inline desde `${ORDERS_DB_PASSWORD}` y su `depends_on: db-init`, igual que hizo `1.6` con Catalog. No está numerado en el roadmap; entra cuando la Fase 3 necesite a Orders hablando con RabbitMQ dentro de la red de compose.
- **Alinear la versión de EF Core** con `10.0.11` el día que se actualice la herramienta global `dotnet-ef`. Ahora afecta a dos servicios en vez de a uno.
