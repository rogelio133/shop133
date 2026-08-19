# Fase 1.2 — EF Core + migraciones contra SQL Server (`CatalogDb`)

**Fecha:** 2026-08-18 · **Estado:** completado · **Roadmap:** [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md)

---

## Objetivo

`1.1` dejó la entidad [`Product`](../src/Services/Catalog/Catalog.Infrastructure/Entities/Product.cs) escrita y sin ninguna forma de persistirla: `Catalog.Infrastructure` no tenía un solo paquete NuGet, y `CatalogDb` existía en SQL Server desde `0.4` pero vacía. Este punto cierra ese hueco: monta EF Core 10 con el proveedor de SQL Server, traduce el modelo a esquema y genera y aplica la primera migración.

La razón de que vaya **antes** que los endpoints de `1.3` y no después es que el esquema decide cosas que la entidad no puede decidir sola. La más importante es el **índice único sobre `Sku`**: la decisión 9 de [fase_1_1.md](fase_1_1.md) dejó escrito que la entidad normaliza el código a mayúsculas pero no puede garantizar que sea único, porque eso es una pregunta sobre el conjunto entero de filas. Escribir un `POST /products` sin ese índice significaría descubrir el duplicado en producción en vez de en el `INSERT`.

Es también el primer punto en el que se usa de verdad lo que montó `0.4`: la conexión se abre con `catalog_user`, no con `sa`, y la migración funciona porque ese login tiene `db_owner` sobre su propia base y sobre ninguna otra.

**Fuera de alcance deliberadamente:** los endpoints (`1.3`), el seed con `HasData` (`1.4`), el Dockerfile y el connection string de contenedor (`1.6`) y los tests de componente con Testcontainers (`1.7`). La tabla `Products` queda creada y **vacía**.

---

## Decisiones

### 1. La configuración va en un `IEntityTypeConfiguration`, no en DataAnnotations ni en `OnModelCreating`

**Descartado — DataAnnotations sobre `Product`.** Habría sido lo más corto (`[MaxLength(50)]`, `[Required]`), pero mete `System.ComponentModel.DataAnnotations` y `Microsoft.EntityFrameworkCore` dentro de la entidad. Es el mismo argumento que la regla 4 de [CLAUDE.md](../CLAUDE.md) aplica a `Shop133.Contracts`, y el que la decisión 4 de `1.1` ya usó para rechazar las anotaciones de validación: la entidad no debe saber cómo se guarda. Además el índice único **no se puede expresar** con anotaciones sin el atributo `[Index]`, que es de EF Core y que iría, otra vez, sobre la clase.

**Descartado — todo dentro de `OnModelCreating`.** Funciona con una entidad. Con seis, el método se convierte en el archivo más largo del proyecto y cada cambio de una entidad toca el mismo sitio.

**Elegido:** [`ProductConfiguration`](../src/Services/Catalog/Catalog.Infrastructure/Persistence/Configurations/ProductConfiguration.cs) en su propio archivo. Si mañana el catálogo se persistiera de otra forma, lo que se tira es ese archivo; `Product.cs` no se toca.

### 2. `ApplyConfiguration` explícito, no `ApplyConfigurationsFromAssembly`

**Descartado — el escaneo por reflexión.** Es lo que casi todo el mundo escribe, y con muchas entidades se paga solo. Con una, lo único que hace es que para saber qué configuraciones están registradas haya que buscar implementaciones de una interfaz por todo el ensamblado.

**Elegido:** `modelBuilder.ApplyConfiguration(new ProductConfiguration())`. Una línea por entidad, y `OnModelCreating` es la lista de lo que hay. Se cambia el día que haya media docena, no antes — CLAUDE.md pide código explícito antes que abstracción.

### 3. El connection string entero en User Secrets

**Descartado — una plantilla sin contraseña en `appsettings.json`,** con User Secrets sobreescribiendo. Es más autodocumentado (se ve la forma de la cadena en el repo), pero deja la misma cadena en dos sitios y, sobre todo, deja un connection string *aparentemente válido* versionado. Cuando alguien lo copie a otro servicio, el `Database=CatalogDb` viajará con él y romperá la regla 1 sin que nadie lo note.

**Elegido:** `ConnectionStrings:CatalogDb` completo en User Secrets de `Catalog.API`, `appsettings.json` sin tocar. Es lo que la sección *Pendiente* de [fase_0_4.md](fase_0_4.md) dejó decidido. `Program.cs` documenta la forma de la cadena en el mensaje de error de la guarda, que es donde se necesita leerla.

### 4. Sin `Database.Migrate()` al arrancar

**Descartado — migrar en el arranque cuando el entorno es Development.** Es cómodo: `F5` y la base está al día. Pero esconde el paso justo en el proyecto que existe para entenderlo, y no sobrevive a la Fase 1.6 en adelante, donde varias instancias del mismo servicio arrancarían a la vez y competirían por aplicar la misma migración.

**Elegido:** `dotnet ef database update` a mano, que es el comando que CLAUDE.md ya tenía documentado. En `1.7` el fixture de Testcontainers llamará a `Database.MigrateAsync()` sobre un contenedor recién creado, que es otro caso: ahí no hay concurrencia y la alternativa sería mantener un script SQL aparte.

### 5. EF Core **10.0.8**, no la última (10.0.11)

La herramienta global `dotnet-ef` instalada es la **10.0.8**. Con paquetes más nuevos que la herramienta, `dotnet ef` avisa en cada comando de que la versión de las herramientas es anterior a la del runtime.

**Descartado — 10.0.11,** que es lo que alinearía con `Microsoft.AspNetCore.OpenApi 10.0.11`, ya pineado en las cinco APIs. Obliga a `dotnet tool update --global dotnet-ef` como paso previo, y eso es estado de la máquina, no del repositorio: quien clone el proyecto se encuentra el aviso sin saber de dónde sale.

**Elegido:** 10.0.8 en los dos paquetes. La asimetría con el `10.0.11` de OpenAPI es deliberada y tiene un motivo escrito; la alineación se hará cuando se actualice la herramienta.

### 6. `Microsoft.EntityFrameworkCore.Design` va en `Catalog.API`, no en `Catalog.Infrastructure`

Parece al revés: el `DbContext` está en Infrastructure. Pero `dotnet ef` construye el **host del startup project** para sacar de ahí la configuración, y es en ese proyecto donde busca las herramientas de diseño. Va con `PrivateAssets="all"` para que no se propague a quien referencie `Catalog.API` ni acabe en la imagen de `1.6`.

Esto crea una excepción real a la regla "EF Core solo en la capa de persistencia", y por eso el test de arquitectura de la decisión 8 la codifica de forma explícita en vez de dejarla como caso tolerado por descuido.

### 7. El índice único sobre `Sku` cierra la decisión 9 de `1.1`

`Product` normaliza el `Sku` con `ToUpperInvariant()` para que `lap-14` y `LAP-14` no sean dos productos distintos, pero una instancia no puede mirar a las demás. `HasIndex(...).IsUnique()` es la mitad que faltaba, y se comprobó de verdad (ver *Verificación*, comprobación 6) en vez de darla por buena porque aparezca en el modelo.

### 8. Un test de arquitectura nuevo: EF Core solo en `.Infrastructure`

`1.2` no es un punto de test, pero es el punto en el que entra el primer paquete de EF Core al repositorio — y por tanto el primero en el que la regla 5 de CLAUDE.md puede romperse por paquetes en vez de por referencias de proyecto. Un `DbSet` inyectado en un controller compila perfectamente; lo único que lo impide es que `Catalog.API` no tenga el proveedor.

`EfCorePackages_LiveOnlyIn_InfrastructureProjects` lo fija, con la excepción de la decisión 6 escrita en el propio mensaje de fallo. CLAUDE.md dice que una regla que solo vive en prosa se rompe en silencio; esta ya no vive solo en prosa. El proyecto pasa de 11 a **12** tests, todos `Category=Fast`.

---

## Cambios

| Archivo | Rol |
|---|---|
| [Catalog.Infrastructure.csproj](../src/Services/Catalog/Catalog.Infrastructure/Catalog.Infrastructure.csproj) | Primer paquete del proyecto: `Microsoft.EntityFrameworkCore.SqlServer` 10.0.8. Arrastra `EntityFrameworkCore` y `.Relational`, que no se declaran aparte. |
| [Catalog.API.csproj](../src/Services/Catalog/Catalog.API/Catalog.API.csproj) | `Microsoft.EntityFrameworkCore.Design` 10.0.8 con `PrivateAssets="all"`, y el `<UserSecretsId>` que añadió `dotnet user-secrets init`. |
| [Persistence/CatalogDbContext.cs](../src/Services/Catalog/Catalog.Infrastructure/Persistence/CatalogDbContext.cs) | El `DbContext` de `CatalogDb`. `DbSet<Product> Products` y una sola llamada a `ApplyConfiguration`. |
| [Persistence/Configurations/ProductConfiguration.cs](../src/Services/Catalog/Catalog.Infrastructure/Persistence/Configurations/ProductConfiguration.cs) | El mapeo: tabla, PK `IDENTITY`, las cuatro longitudes leídas de las constantes de la entidad, `decimal(18,2)` y el índice único sobre `Sku`. |
| `Migrations/20260819001038_InitialCreate.cs` (+ `.Designer.cs`) | La primera migración del proyecto, generada. |
| `Migrations/CatalogDbContextModelSnapshot.cs` | El snapshot del modelo contra el que EF calcula la siguiente migración. Generado. |
| [Program.cs](../src/Services/Catalog/Catalog.API/Program.cs) | Guarda sobre `ConnectionStrings:CatalogDb` + `AddDbContext<CatalogDbContext>` con `UseSqlServer`. |
| [LayeringRulesTests.cs](../tests/Shop133.ArchitectureTests/LayeringRulesTests.cs) | Test nuevo `EfCorePackages_LiveOnlyIn_InfrastructureProjects` y sus dos constantes. |
| [CLAUDE.md](../CLAUDE.md), [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md), [docs/README.md](README.md) | Estado de la fase, checkbox del roadmap e índice. |

**Sin tocar:** `Product.cs` — `1.1` la dejó lista para EF y no hizo falta ni un cambio, que era justo la apuesta del constructor privado sin parámetros (decisión 5 de aquel punto). Tampoco `appsettings*.json`, `docker-compose*.yml`, `db/init/` ni `shop133.slnx`.

---

## Detalles que cuestan tiempo

### `dotnet ef` **sí** usa el entorno `Development` por defecto — pero conviene saber por qué importa

`WebApplication.CreateBuilder` solo carga User Secrets cuando el entorno es `Development`, y `dotnet ef` no lee `launchSettings.json`. La conclusión intuitiva es que los comandos de EF no verían el connection string. Se ejecutaron **sin** ninguna opción de entorno y funcionaron: las herramientas de EF Core ponen `ASPNETCORE_ENVIRONMENT=Development` por su cuenta cuando nadie dice lo contrario.

Lo que cuesta tiempo es el caso en que sí hay algo dicho. Forzando el entorno:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Production"
dotnet ef dbcontext info --project ... --startup-project ...
```

```
An error occurred while accessing the Microsoft.Extensions.Hosting services. Continuing without
the application service provider. Error: Falta la configuración 'ConnectionStrings:CatalogDb'. En
local vive en User Secrets: dotnet user-secrets set "ConnectionStrings:CatalogDb" ...
Unable to create a 'DbContext' of type 'CatalogDbContext'.
```

Ese mensaje legible es exactamente para lo que está la guarda de `Program.cs`. Sin ella, `UseSqlServer(null)` lanza un `ArgumentNullException` que no menciona ni la clave que falta ni User Secrets, y el mensaje de EF por encima (*"Unable to create a 'DbContext'"*) manda a buscar un `IDesignTimeDbContextFactory` que no es el problema. Si algún día aparece un entorno que no sea Development, la salida es `--environment Development` como opción de `dotnet ef`.

### `--no-build` justo después de `migrations add` lee un ensamblado sin la migración

Al generar el script con `dotnet ef migrations script --no-build` inmediatamente después de crear la migración, la salida fue **solo** la creación de `__EFMigrationsHistory`, sin `CREATE TABLE Products`:

```
IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (...);
END;
GO
```

No es un fallo del modelo. `migrations add` compila el proyecto *antes* de escribir los archivos de la migración, así que el `.dll` que queda en `bin/` no la contiene; `--no-build` lo reutiliza y EF concluye, correctamente, que no hay ninguna migración que aplicar. El síntoma es peligroso porque no hay ni error ni aviso: el script sale vacío y parece que la configuración no ha hecho nada. Quitar `--no-build` lo resolvió.

### El índice único rechaza los duplicados por colación, y aun así la normalización de la entidad hace falta

`CatalogDb` hereda la colación por defecto del servidor, `SQL_Latin1_General_CP1_CI_AS` — *case insensitive*. Eso significa que el índice único rechaza `lap-14` frente a `LAP-14` **aunque la entidad no normalizara nada** (comprobado, ver comprobación 6).

Sería fácil deducir de ahí que el `ToUpperInvariant()` de `Product` sobra. No sobra, y por dos motivos: lo que se *lee* de la base tiene que ser el valor canónico (si no, un producto dado de alta como `lap-14` se muestra así en el catálogo y viaja así en las respuestas de la API), y la colación es una propiedad de la base que alguien puede cambiar sin tocar una línea de C#. La entidad garantiza la forma; el índice garantiza la unicidad. Son dos cosas distintas que aquí se solapan por casualidad.

### `HasPrecision(18, 2)` es redundante hoy, y se declara igual

`decimal(18,2)` es lo que el proveedor de SQL Server genera por defecto para un `decimal`. Declararlo no cambia el SQL de esta migración. Se hace porque dejarlo implícito significa que el tipo de una columna de dinero depende de una convención del proveedor, y un cambio de proveedor la movería sin que nadie lo notara. `fase_1_1.md` ya lo pedía por escrito en su sección *Pendiente*.

### Dónde puede vivir el `DbContext`

`ServiceBoundaryRulesTests.DbContextFiles_LiveOnlyIn_OwningServiceInfrastructure` existía desde `0.6` pasando en vacío — no había ningún `DbContext`. Con `1.2` empieza a comprobar algo de verdad. Exige que todo `*DbContext.cs` bajo `src/` esté en `src/Services/<X>/<X>.Infrastructure/`, a cualquier profundidad de subcarpeta: `Persistence/CatalogDbContext.cs` vale. `CatalogDbContextModelSnapshot.cs` **no** matchea el glob `*DbContext.cs`, así que los archivos generados no lo afectan.

---

## Verificación

### 1. Compila y los archivos nuevos los recoge el glob implícito

```powershell
dotnet build src/Services/Catalog/Catalog.API/Catalog.API.csproj
```

```
  Shop133.Contracts -> ...\Shop133.Contracts.dll
  Catalog.Infrastructure -> ...\Catalog.Infrastructure.dll
  Catalog.API -> ...\Catalog.API.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### 2. La migración generada

```powershell
dotnet ef migrations add InitialCreate `
  --project src/Services/Catalog/Catalog.Infrastructure `
  --startup-project src/Services/Catalog/Catalog.API
```

```
Build started...
Build succeeded.
Done. To undo this action, use 'ef migrations remove'
```

### 3. El SQL, revisado antes de aplicarlo

```powershell
dotnet ef migrations script `
  --project src/Services/Catalog/Catalog.Infrastructure `
  --startup-project src/Services/Catalog/Catalog.API
```

```sql
CREATE TABLE [Products] (
    [Id] int NOT NULL IDENTITY,
    [Sku] nvarchar(50) NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [Description] nvarchar(2000) NOT NULL,
    [Price] decimal(18,2) NOT NULL,
    [Stock] int NOT NULL,
    [ImageUrl] nvarchar(500) NULL,
    CONSTRAINT [PK_Products] PRIMARY KEY ([Id])
);

CREATE UNIQUE INDEX [IX_Products_Sku] ON [Products] ([Sku]);

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260819001038_InitialCreate', N'10.0.8');
```

(Ver *Detalles* para el intento previo con `--no-build`, que salió incompleto.)

### 4. Aplicada contra `CatalogDb`

```powershell
dotnet ef database update `
  --project src/Services/Catalog/Catalog.Infrastructure `
  --startup-project src/Services/Catalog/Catalog.API
```

```
info: Microsoft.EntityFrameworkCore.Migrations[20402]
      Applying migration '20260819001038_InitialCreate'.
info: Microsoft.EntityFrameworkCore.Database.Command[20101]
      Executed DbCommand (3ms) ... CREATE TABLE [Products] (...)
info: Microsoft.EntityFrameworkCore.Database.Command[20101]
      Executed DbCommand (3ms) ... CREATE UNIQUE INDEX [IX_Products_Sku] ON [Products] ([Sku]);
Done.
```

### 5. El esquema real, consultado **con `catalog_user`** (no con `sa`)

```powershell
docker exec shop133-sqlserver /opt/mssql-tools18/bin/sqlcmd `
  -S localhost -U catalog_user -P "<CATALOG_DB_PASSWORD>" -C -d CatalogDb -Q "..."
```

```
col            type       len         prec scale       nullable
-------------- ---------- ----------- ---- ----------- --------
Id             int               NULL   10           0 NO
Sku            nvarchar            50 NULL        NULL NO
Name           nvarchar           200 NULL        NULL NO
Description    nvarchar          2000 NULL        NULL NO
Price          decimal           NULL   18           2 NO
Stock          int               NULL   10           0 NO
ImageUrl       nvarchar           500 NULL        NULL YES

idx                  kind         is_unique is_primary_key
-------------------- ------------ --------- --------------
PK_Products          CLUSTERED            1              1
IX_Products_Sku      NONCLUSTERED         1              0

migration                                ef
---------------------------------------- ----------
20260819001038_InitialCreate             10.0.8

collation
----------------------------------------
SQL_Latin1_General_CP1_CI_AS
```

Que la consulta funcione con `catalog_user` es la mitad de esta comprobación: la migración se aplicó con el login del servicio, con `db_owner` sobre `CatalogDb` y sin acceso a las otras tres bases. La PK salió *clustered* sobre un `IDENTITY` creciente, que es lo que `fase_1_1.md` daba por resuelto para `Product`.

### 6. El índice único, probado de verdad

Tres `INSERT` seguidos y limpieza en el mismo batch:

```
--- segundo INSERT, mismo Sku ---
Msg 2601, Level 14, State 1, Line 4
Cannot insert duplicate key row in object 'dbo.Products' with unique index 'IX_Products_Sku'.
The duplicate key value is (LAP-14).

--- tercer INSERT, mismo Sku en minusculas ---
Msg 2601, Level 14, State 1, Line 6
Cannot insert duplicate key row in object 'dbo.Products' with unique index 'IX_Products_Sku'.
The duplicate key value is (lap-14).

--- limpieza ---
filas_restantes
---------------
              0
```

El tercer `INSERT` es el que descubrió lo de la colación (ver *Detalles*). La tabla queda vacía: los datos los mete `1.4`.

### 7. Los tests de arquitectura

```powershell
dotnet test tests/Shop133.ArchitectureTests -- --filter-trait "Category=Fast"
```

```
Test run summary: Passed!
  total: 12
  failed: 0
  succeeded: 12
  skipped: 0
```

**Y el test nuevo, comprobado en rojo.** Un test de arquitectura que solo se ha visto pasar no ha demostrado nada — puede estar comprobando el vacío. Se añadió temporalmente `Microsoft.EntityFrameworkCore.SqlServer` a `Catalog.API.csproj`:

```
  EF Core solo se declara en la capa .Infrastructure. La única excepción es
  Microsoft.EntityFrameworkCore.Design en un proyecto .API: las herramientas dotnet-ef lo buscan
  en el startup project, no en el que contiene el DbContext.
  Paquetes fuera de sitio: Catalog.API → Microsoft.EntityFrameworkCore.SqlServer

  total: 1  failed: 1  succeeded: 0
```

El `.csproj` se restauró acto seguido y los 12 tests volvieron a verde.

### 8. El host arranca con el `DbContext` registrado

```powershell
dotnet run --project src/Services/Catalog/Catalog.API --launch-profile http
```

```
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://localhost:5124
info: Microsoft.Hosting.Lifetime[0]
      Hosting environment: Development
warn: Microsoft.AspNetCore.HttpsPolicy.HttpsRedirectionMiddleware[3]
      Failed to determine the https port for redirect.
```

`GET http://localhost:5124/openapi/v1.json` devolvió **200**. No hay más que probar hasta `1.3`: todavía no existe ningún controller. El `warn` de HTTPS es previo a este punto y solo aparece con el perfil `http`, que no publica puerto HTTPS.

---

## Pendiente

- **1.3** — los endpoints. Necesitan inyectar `CatalogDbContext` en el controller y una vía de mutación en la entidad para el `PUT` (`1.1` la dejó abierta a propósito). El `POST` tendrá que mapear la `DbUpdateException` con `Msg 2601` a un `409 Conflict`: el índice único de este punto es lo que la provoca, y dejarla salir como `500` desperdiciaría la mitad del trabajo.
- **1.4** — el seed con `HasData` y sus ids fijos. Como `Products` tiene `IDENTITY`, `HasData` obliga a pasar los ids explícitamente y genera `SET IDENTITY_INSERT`. Los `Sku` del seed son los primeros códigos reales del proyecto.
- **1.6** — el connection string de contenedor: `Server=sqlserver,1433`, mismo login y misma contraseña, y `depends_on: db-init: condition: service_completed_successfully` para que el servicio no arranque antes de que exista `CatalogDb`.
- **1.7** — el fixture de Testcontainers aplica la migración con `Database.MigrateAsync()` sobre un SQL Server efímero. Es también donde el índice único deja de comprobarse a mano con `sqlcmd` y pasa a tener un test.
- **Alinear la versión de EF Core** con el resto de paquetes (10.0.11) el día que se actualice la herramienta global `dotnet-ef`. Hoy la asimetría es deliberada — decisión 5.
- **4.5** — cuando `OrdersDb` persista el estado de la saga, vuelve la pregunta que `1.1` dejó abierta: si la PK de un `uniqueidentifier` debe ir *clustered*. Aquí no se planteó porque un `int IDENTITY` la responde solo.
