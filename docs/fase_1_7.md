# Fase 1.7 — `Catalog.Tests`: tests de componente con `WebApplicationFactory` + Testcontainers

**Fecha:** 2026-08-20 · **Estado:** completado · **Roadmap:** [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md), punto 1.7

---

## Objetivo

Cerrar la Fase 1 poniendo bajo test automático lo que hasta ahora solo se había comprobado a mano con `curl` y la interfaz de Scalar: los cinco endpoints de [1.3](fase_1_3.md), el seed de [1.4](fase_1_4.md) y los tres caminos de error del servicio (400, 404, 409).

El roadmap explica por qué este punto está aquí y no más tarde: *«es la primera infraestructura de test del proyecto (fixture del contenedor, reset de datos entre tests) y se monta sobre el servicio más simple, para reutilizarla después en los demás»*. Catalog.API es el sitio barato donde equivocarse con la fixture; en la Fase 3 ya habría una saga encima.

Hay un caso concreto que justifica todo el montaje, y conviene nombrarlo porque es el que decide entre Testcontainers y cualquier atajo. El `409` de Sku duplicado no lo decide el controller mirando la tabla: lo decide SQL Server rechazando el `INSERT` contra el índice único de [1.2](fase_1_2.md), y el controller se limita a traducir un `SqlException` con número 2601 o 2627 (`DbUpdateExceptionExtensions`). **Ese camino no existe si la base de datos no es real.** El provider InMemory no aplica índices únicos, así que el test pasaría en verde sin llegar a ejecutar la rama que dice probar — que es exactamente el fallo que la regla 1 de *Testing* en [CLAUDE.md](../CLAUDE.md) prohíbe.

Deudas anteriores que aterrizan aquí:

- [fase_1_6.md](fase_1_6.md) cierra con «**`Catalog.Tests` con `WebApplicationFactory` + Testcontainers** — es **1.7**, el punto que cierra la Fase 1. Su *fixture* levantará su propio SQL Server y llamará a `MigrateAsync()`, así que no reutiliza este contenedor».
- [fase_1_4.md](fase_1_4.md) anticipaba que «la *fixture* de Testcontainers de **1.7** obtenga estos datos con solo llamar a `MigrateAsync()`». Se cumple literalmente: la fixture no siembra nada.
- [fase_1_3.md](fase_1_3.md) dejó documentado el hueco de validación de `ImageUrl` (`"   "` pasa las DataAnnotations y lo para la entidad). Aquí pasa a ser un test.

**Fuera de alcance deliberadamente:** los tests de `/openapi/v1.json` y `/scalar` de [1.5](fase_1_5.md) —la superficie que se acordó cubrir es el CRUD—, el paquete `Respawn` (ver decisión 3), replicar el login `catalog_user` dentro del contenedor de test (decisión 5) y la ejecución en CI, que es **8.3**.

---

## Decisiones

### 1. Proyecto nuevo en `tests/Services/Catalog/Catalog.Tests`, con una sola referencia

La ruta es la que declaran el roadmap y CLAUDE.md. El `.csproj` está calcado del de `Shop133.ArchitectureTests`: `Microsoft.NET.Sdk`, `<OutputType>Exe</OutputType>`, `IsPackable=false`, y **ni `Microsoft.NET.Test.Sdk` ni `xunit.runner.visualstudio`**, que son infraestructura de VSTest y la regla 5b los prohíbe.

La única `ProjectReference` es `Catalog.API`. Arrastra transitivamente `Catalog.Infrastructure` y `Shop133.Contracts`, y con ellos los DTOs de `Catalog.API/Models` — que los tests usan **tal cual** en vez de objetos anónimos, para que un cambio de contrato rompa la compilación en lugar de un `Assert`.

*Descartado* el SDK `Microsoft.NET.Sdk.Web`, que es lo que recomiendan varias guías de `WebApplicationFactory` para que el proyecto de test vea el framework de ASP.NET Core. No hace falta: se probó primero con `Microsoft.NET.Sdk` y compiló a la primera, porque la referencia a `Catalog.API` ya trae el `FrameworkReference` de `Microsoft.AspNetCore.App`. Se dejó el SDK simple, que es el que corresponde a una biblioteca de tests.

### 2. `public partial class Program { }` al final de `Program.cs`

Los top-level statements generan una clase `Program` **internal**, y `WebApplicationFactory<Program>` necesita que el tipo sea accesible desde fuera del ensamblado. Sin una de las dos soluciones posibles, el proyecto de test ni compila. Es el primer obstáculo real que aparece al montar esto.

*Descartado* `<InternalsVisibleTo Include="Catalog.Tests" />` en `Catalog.API.csproj`. Es la opción más estricta —`Program` seguiría siendo internal y el permiso quedaría acotado a un ensamblado con nombre— pero pone la razón en un archivo distinto del que la provoca: quien abriera `Program.cs` no vería por qué ese tipo es visible desde fuera. La línea con su comentario al pie de `Program.cs` explica el motivo donde se lee, que es lo que este repositorio prioriza sobre la mínima superficie pública.

### 3. Una base de datos nueva por **clase** de test, y ningún paquete de reset

El aislamiento es el diseño central del punto. Se eligió: **un contenedor por ensamblado, una base de datos por clase de test.** La clase crea `CatalogTests_NNN`, le aplica las tres migraciones —con lo que obtiene el seed de 1.4 íntegro— y la borra al terminar.

*Descartado* `Respawn`, que es la respuesta estándar a «reset entre tests» y estaba en la lista de paquetes previstos de CLAUDE.md. **Respawn borra filas, no las restaura**, y las 50 del catálogo no las pone un `Arrange`: viven dentro de la migración `SeedSouvenirCatalog`. Reponerlas después de cada reset exigiría o duplicar el seed dentro de los tests —50 filas que se desincronizarían del original al primer cambio— o borrar a mano la fila de esa migración en `__EFMigrationsHistory` para que `MigrateAsync()` la volviera a aplicar. Lo segundo funciona y es rápido, pero es un truco que hay que explicar cada vez que alguien lo lee. Una base por clase no necesita explicación y no añade dependencia.

*Descartado* también una base por **test**, que aísla del todo y permitiría afirmar «hay exactamente 50 productos». El `CREATE DATABASE` más las tres migraciones cuestan cerca de un segundo cada vez, y con 19 tests eso es la mayor parte del tiempo de la suite. El precio de la base por clase es una disciplina que estos tests sí pueden mantener, y que está escrita en el `<summary>` de `ProductsEndpointsTests`:

1. Ningún test modifica ni borra una fila del seed; el que necesita escribir crea su propio producto con su propio Sku (`TEST-0xx`).
2. Las lecturas del catálogo completo afirman que **contienen** lo que esperan, nunca que hay exactamente N filas.

Hay un tercer factor que hace segura esta decisión y que no es evidente: **xUnit no paraleliza dentro de una misma collection**. Como todas las clases cuelgan de `CatalogApiCollection`, se ejecutan en serie, así que el contenedor nunca recibe dos migraciones a la vez ni dos clases escriben en paralelo. El riesgo que queda es el *orden* entre tests de una clase, y las dos reglas de arriba lo neutralizan.

### 4. El connection string se inyecta con `UseSetting`, y no se sustituye el `DbContext`

Lo natural al leer casi cualquier guía es sobrescribir el registro de EF Core en `ConfigureTestServices`. **Aquí eso no funciona**, y el motivo está en `Program.cs`:

```csharp
var connectionString = builder.Configuration.GetConnectionString("CatalogDb")
    ?? throw new InvalidOperationException("Falta la configuración 'ConnectionStrings:CatalogDb'. …");
```

Esa línea se ejecuta **antes** de `app.Build()`. Si la clave no está, el host ni se construye y `ConfigureTestServices` no llega a correr nunca. La guarda que 1.2 puso para que el fallo fuera legible es justo la que obliga a resolverlo por configuración.

`builder.UseSetting("ConnectionStrings:CatalogDb", …)` lo resuelve y además tiene una ventaja: al darle el valor correcto no hace falta reregistrar nada, así que **lo que se prueba es el `AddDbContext` real del servicio**, no una versión de laboratorio.

### 5. Entorno `Testing`, y `sa` dentro del contenedor

`WebApplicationFactory` pone `Development` por defecto. Se fuerza a `Testing` por dos motivos medidos:

- `Development` **carga los User Secrets de Catalog.API**, que traen la contraseña real de `catalog_user` y el `CatalogDb` del compose. Si la línea de `UseSetting` fallara, los tests correrían contra la base de desarrollo y nada lo delataría — borrando productos de verdad, porque hay un test de `DELETE`.
- `Development` activa `UseHttpsRedirection()` (guardado así desde 1.6), que sobre el `TestServer` solo sirve para devolver `307` donde se esperaba un `200`.

De paso, `Testing` se parece al `Production` del contenedor de 1.6: se comprueba que el servicio arranca **sin secretos**, no que arranca desde el IDE.

Dentro del contenedor de test la aplicación se conecta como `sa`. *Descartado* replicar `db/init/01-create-databases.sql` para crear un `catalog_user` con sus permisos. La regla 1 de CLAUDE.md —un login por servicio, sin acceso a las bases de los demás— la garantiza `db-init` en compose ([fase_0_4.md](fase_0_4.md)), y **es una propiedad del despliegue, no del código de Catalog.API**. En un contenedor efímero con una sola base y un solo servicio dentro, ese montaje probaría el sistema de permisos de SQL Server, no el servicio.

### 6. La imagen del contenedor es la misma etiqueta que la de compose

`new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")`, y no la que Testcontainers trae por defecto, que es un CU concreto distinto. Así la primera ejecución en una máquina que ya tiene el stack levantado **no se descarga 1,5 GB de imagen** para correr los tests, y además se prueba contra exactamente el mismo SQL Server que el resto del proyecto.

La imagen va en el constructor y no en un `.WithImage(...)` encadenado porque desde Testcontainers 4.14 el constructor sin parámetros está marcado `[Obsolete]`, y este repositorio compila con 0 warnings.

Sin puerto fijo, a propósito: el `1433` del host ya lo ocupa el `sqlserver` de `docker-compose.override.yml`, así que fijarlo haría fallar la suite justo en la máquina que tiene el proyecto corriendo. El puerto aleatorio es el comportamiento por defecto.

### 7. Ninguna regla de arquitectura nueva, y la suite sigue en 12

CLAUDE.md pide considerar, en cada punto, si el nuevo trabajo se puede convertir en una regla ejecutable. Aquí la respuesta es que no hace falta ninguna, y hay algo que conviene dejar escrito porque parece un problema y no lo es:

`Catalog.Tests` acaba viendo `Microsoft.EntityFrameworkCore` por la vía transitiva, y existe una regla —`EfCorePackages_LiveOnlyIn_InfrastructureProjects`— que prohíbe exactamente eso fuera de la capa `.Infrastructure`. No salta porque `ProjectGraph.LoadProjects()` enumera **solo `<repo>/src`**: los proyectos de `tests/` son invisibles para las 12 reglas de [0.6](fase_0_6.md). La suite `Fast` sigue en **12 tests**.

La nota a futuro: si algún día se ampliara ese escaneo a la raíz del repositorio, la corrección correcta es añadir una exención `IsTest` a la regla, **no** dejar de escanear `tests/`. Un proyecto de test que referencia el `.API` de su servicio es legítimo; uno que referenciara el `.Infrastructure` de *otro* servicio no lo sería, y esa sí sería una regla que merece la pena escribir el día que haya más de un servicio con tests.

### 8. `Testcontainers.MsSql` en 4.14.0, pese a que la primera ejecución falló

La versión 4.14.0 provocó, en la primera ejecución de la suite en esta máquina, un fallo que no tiene nada que ver con el código:

```
System.IO.FileLoadException : Could not load file or assembly
'…\bin\Debug\net10.0\Testcontainers.MsSql.dll'.
An Application Control policy has blocked this file. (0x800711C7)
```

El diagnóstico completo está en la sección de abajo. La conclusión que afecta a la decisión: **el bloqueo es transitorio y le toca igual a cualquier versión**, así que bajar de versión no arregla nada. Y quedarse en 4.13.0 sí costaba algo concreto: arrastra `SSH.NET` 2025.1.0, con la vulnerabilidad alta `GHSA-q939-rpr3-3284`, y el build fallaba con `NU1903`. Subir a 4.14.0 es precisamente lo que lo arregla — es el **único** cambio de esa versión: mismo `Docker.DotNet.Enhanced` 4.3.3, `SSH.NET` 2026.0.0.

*Descartado* quedarse en 4.13.0 con una `PackageReference` directa a `SSH.NET` 2026.0.0 para ganar a la transitiva. Funciona y deja el grafo resuelto idéntico, pero mete en el `.csproj` una dependencia directa que el proyecto no usa —`SSH.NET` solo lo emplea Testcontainers para hablar con un host Docker remoto— y habría que acordarse de quitarla después.

### 9. Dos clases de test, y la fixture se construye a mano

`ProductsEndpointsTests` (18 tests) y `CategoriesEndpointsTests` (1). Ambas reciben el `SqlServerContainerFixture` por constructor —inyección de collection fixture, que xUnit soporta sin discusión— e implementan `IAsyncLifetime` para construir y destruir su propio `CatalogApiFactory`.

*Descartado* declarar `IClassFixture<CatalogApiFactory>` y dejar que xUnit inyecte el collection fixture dentro del class fixture. Es más declarativo, pero depende de un comportamiento de anidamiento de fixtures que habría que verificar; con dos clases, ocho líneas explícitas y repetidas cuestan menos que esa dependencia.

*Descartado* una clase base común `CatalogApiTestBase`. Ahorraría esas ocho líneas una vez, pero no sería reutilizable desde `Orders.Tests` ni `Inventory.Tests` —cada una tendrá su propia factory— así que sería una abstracción que solo sirve dos veces y hay que saltar para leer un test.

---

## Cambios

| Archivo | Rol |
|---|---|
| [../tests/Services/Catalog/Catalog.Tests/Catalog.Tests.csproj](../tests/Services/Catalog/Catalog.Tests/Catalog.Tests.csproj) | **Nuevo.** `Exe` sobre Microsoft.Testing.Platform, tres paquetes, una `ProjectReference` a `Catalog.API`. |
| [../tests/Services/Catalog/Catalog.Tests/Infrastructure/SqlServerContainerFixture.cs](../tests/Services/Catalog/Catalog.Tests/Infrastructure/SqlServerContainerFixture.cs) | **Nuevo.** El contenedor único del ensamblado; crea y borra bases dentro de él. |
| [../tests/Services/Catalog/Catalog.Tests/Infrastructure/CatalogApiCollection.cs](../tests/Services/Catalog/Catalog.Tests/Infrastructure/CatalogApiCollection.cs) | **Nuevo.** La collection que comparte el contenedor y, de paso, serializa las clases. |
| [../tests/Services/Catalog/Catalog.Tests/Infrastructure/CatalogApiFactory.cs](../tests/Services/Catalog/Catalog.Tests/Infrastructure/CatalogApiFactory.cs) | **Nuevo.** `WebApplicationFactory<Program>` con base propia: `CREATE DATABASE`, `MigrateAsync()`, `DROP DATABASE`. |
| [../tests/Services/Catalog/Catalog.Tests/ProductsEndpointsTests.cs](../tests/Services/Catalog/Catalog.Tests/ProductsEndpointsTests.cs) | **Nuevo.** 18 tests: el CRUD de 1.3 con sus caminos de error. |
| [../tests/Services/Catalog/Catalog.Tests/CategoriesEndpointsTests.cs](../tests/Services/Catalog/Catalog.Tests/CategoriesEndpointsTests.cs) | **Nuevo.** 1 test: `GET /categories` ordenado por nombre. |
| [../src/Services/Catalog/Catalog.API/Program.cs](../src/Services/Catalog/Catalog.API/Program.cs) | **Modificado.** Una línea al final: `public partial class Program { }`, con el comentario que dice por qué. |
| [../shop133.slnx](../shop133.slnx) | **Modificado.** Carpetas `/tests/Services/` y `/tests/Services/Catalog/` con el proyecto nuevo. |

**Lo que no se tocó:** ningún `.csproj` de `src/`, ninguna entidad, ningún controller, ninguna migración y ningún archivo de compose. La única línea que entra en `src/` es la de `Program.cs`, y no añade comportamiento.

Paquetes NuGet nuevos, los tres de la lista previamente aprobada en CLAUDE.md:

| Paquete | Versión |
|---|---|
| `xunit.v3` | 4.0.0 (la misma que `Shop133.ArchitectureTests`) |
| `Microsoft.AspNetCore.Mvc.Testing` | 10.0.11 |
| `Testcontainers.MsSql` | 4.14.0 |

Otros archivos: la casilla de 1.7 en [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md), la fila en [README.md](README.md), y en [CLAUDE.md](../CLAUDE.md) el párrafo de estado de la Fase 1, la tabla de fases, el recuento de tests, la lista de paquetes en uso y la sección de comandos.

---

## Detalles que cuestan tiempo

### Smart App Control bloquea la primera carga de un ensamblado sin firmar

La primera ejecución de la suite falló así, y el mensaje no menciona a Testcontainers ni a Docker:

```
Collection fixture type 'Catalog.Tests.Infrastructure.SqlServerContainerFixture' threw in its constructor
---- System.IO.FileLoadException : Could not load file or assembly
     '…\bin\Debug\net10.0\Testcontainers.MsSql.dll'.
     An Application Control policy has blocked this file. (0x800711C7)
```

Es **Smart App Control**, activo en esta máquina:

```
$ Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy'
VerifiedAndReputablePolicyState : 1     # 1 = activado en modo enforcement
```

De los 59 ensamblados del directorio de salida, 42 están firmados con Authenticode y 17 no. Los sin firmar incluyen los del propio proyecto, los de Testcontainers y `Docker.DotNet.*`:

```
$ Get-AuthenticodeSignature …\Testcontainers.MsSql.dll
Status : NotSigned
```

**La pista falsa:** el primer instinto es «SAC bloquea lo que no está firmado», y no es eso — `Catalog.Tests.dll` tampoco está firmado y carga sin problema. El segundo instinto es «este binario concreto no tiene reputación», que llevó a probar cinco versiones anteriores del paquete; todas cargaban, así que parecía confirmado. **También era falso**, y por un error de método: la prueba solo cambiaba `Testcontainers.MsSql.dll` mientras en `bin/` seguía el `Testcontainers.dll` de la versión original. Al bajar de verdad a 4.13.0, el bloqueado pasó a ser `Testcontainers.dll`.

Lo que ocurre en realidad: **SAC rechaza la primera carga de un ensamblado que no conoce mientras consulta al Intelligent Security Graph, y la deja pasar en cuanto vuelve el veredicto.** El mismo archivo, con el mismo hash y en la misma ruta, pasó de `BLOCKED` a `OK` sin que nada cambiara:

```
OK       3021A2644303A176  …\bin\Debug\net10.0\Testcontainers.dll
OK       3021A2644303A176  …\.nuget\packages\testcontainers\4.13.0\lib\net10.0\Testcontainers.dll
```

**La regla práctica:** si la suite falla con `0x800711C7` justo después de restaurar un paquete nuevo, hay que volver a ejecutarla. No hay que bajar de versión, ni desactivar Smart App Control — que además es **irreversible**: una vez apagado no se puede volver a encender sin reinstalar Windows.

### `dotnet test` está roto en esta máquina, y no es por 1.7

Los comandos de verificación que documenta CLAUDE.md **no funcionan hoy**, y el problema es anterior a este punto y afecta también a `Shop133.ArchitectureTests`:

```
$ dotnet test tests/Shop133.ArchitectureTests --nologo
…\Shop133.ArchitectureTests.dll (net10.0) Zero tests ran
Test run summary: Zero tests ran
  error: 1
  total: 0
  duration: 142ms
```

Los tests están perfectamente: el mismo proyecto, ejecutado como el ejecutable que es, da 12 de 12. La diferencia es cómo lo lanza `dotnet test`. Con las trazas de la plataforma activadas se ve que el proceso hijo arranca, recibe sus argumentos y muere en el acto:

```
$ $env:TESTINGPLATFORM_DIAGNOSTIC = '1'; $env:TESTINGPLATFORM_DIAGNOSTIC_VERBOSITY = 'Trace'
Command line arguments: '--nologo --server dotnettestcli --dotnet-test-pipe testingplatform.pipe.4be1877b…'
…
Setting PlatformExitProcessOnUnhandledException: 'False'
        ← el log termina aquí
```

Es el *handshake* del modo servidor entre el CLI y el host de test lo que falla. La causa más probable es el SDK: **CLAUDE.md dice 10.0.303 y la máquina tiene hoy 10.0.400**, con `global.json` en `rollForward: latestFeature`, que rueda sola hasta la última banda. No se pudo comparar con la anterior porque el instalador ya no la deja:

```
$ dotnet --list-sdks
9.0.306 [C:\Program Files\dotnet\sdk]
10.0.400 [C:\Program Files\dotnet\sdk]
```

Se descartó que fuera una versión desalineada de la plataforma de test: `Microsoft.Testing.Platform` 2.3.3 es la que trae `xunit.v3` 4.0.0 y **es la última publicada**, así que no hay nada a lo que subir.

**Cómo se ejecutan los tests mientras tanto** — cada proyecto de test es un ejecutable, que es justo lo que la regla 5b implica:

```powershell
tests\Shop133.ArchitectureTests\bin\Debug\net10.0\Shop133.ArchitectureTests.exe
tests\Services\Catalog\Catalog.Tests\bin\Debug\net10.0\Catalog.Tests.exe
```

Ojo con el filtro: la opción **`--filter-trait` es de `dotnet test`, no del runner**. Ejecutando el `.exe` la opción se llama `-trait`, y `--filter-trait` sale como `error: unknown option`.

```powershell
Shop133.ArchitectureTests.exe -trait "Category=Fast"
```

### El analizador `xUnit1051` obliga a pasar el `CancellationToken`

`client.GetAsync("/products")` compila, pero levanta `xUnit1051` — y este repositorio va a 0 warnings. Todas las llamadas HTTP y los `ReadFromJsonAsync` llevan `TestContext.Current.CancellationToken`, recogido en una propiedad privada para no repetir la expresión veinte veces. No es burocracia: es lo que permite cancelar un test colgado contra el contenedor en vez de agotar el timeout de la suite entera.

### Un cuerpo al que le falta un campo no se puede construir con el DTO

`Create_MissingRequiredField_Returns400` manda JSON crudo con `StringContent` y no un `CreateProductRequest`. No es pereza: los miembros del DTO son `required`, así que **en C# no se puede construir una instancia a la que le falte un campo**, que es exactamente lo que hay que enviar. Los demás tests de error sí usan el DTO, porque un precio negativo o un `ImageUrl` en blanco son valores válidos para el compilador.

### Las trazas de `DbUpdateException` en la salida son las esperadas

La ejecución escupe varios bloques como este, y no son fallos:

```
Microsoft.EntityFrameworkCore.DbUpdateException: An error occurred while saving the entity changes.
   at Microsoft.Data.SqlClient.SqlConnection.OnError(…)
Error Number:2601,State:1,Class:14
```

Es EF Core registrando la excepción que los dos tests de `409` provocan a propósito. **`Error Number:2601` es la prueba de que el índice único de 1.2 está haciendo su trabajo** — es la línea que un test contra InMemory nunca produciría.

### `MigrateAsync()` es el seed, y tarda

Las tres migraciones se aplican en cada base nueva, y `SeedSouvenirCatalog` son 20 KB de `INSERT`. En la traza se ve entrar el catálogo entero, categorías incluidas, sin que la fixture siembre una sola fila. Es lo que hace que un test pueda leer `TAZA-001` y otro provocar un `409` contra ese mismo Sku sin ponerse de acuerdo.

El contenedor tarda unos 13 s en pasar sus *readiness checks* —Testcontainers los hace con `/opt/mssql-tools18/bin/sqlcmd`, la ruta correcta para la imagen de 2022— y esos segundos se pagan una sola vez para todo el ensamblado.

---

## Verificación

### Build de la solución completa

```
$ dotnet build --nologo
  Catalog.Tests -> C:\personalprojects\shop133\tests\Services\Catalog\Catalog.Tests\bin\Debug\net10.0\Catalog.Tests.dll
  …
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:12.52
```

### Los 19 tests de componente

```
$ tests\Services\Catalog\Catalog.Tests\bin\Debug\net10.0\Catalog.Tests.exe
=== TEST EXECUTION SUMMARY ===
   Catalog.Tests  Total: 19, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 78.255s
```

### La suite de arquitectura sigue intacta

```
$ tests\Shop133.ArchitectureTests\bin\Debug\net10.0\Shop133.ArchitectureTests.exe -trait "Category=Fast"
=== TEST EXECUTION SUMMARY ===
   Shop133.ArchitectureTests  Total: 12, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.200s
```

### La suite es repetible, y limpia lo que levanta

Segunda ejecución seguida, sin tocar nada. Que dé lo mismo es lo que demuestra que el `DROP DATABASE` del `DisposeAsync` se está ejecutando; si no, la segunda pasada chocaría con los `TEST-0xx` de la primera.

```
$ tests\Services\Catalog\Catalog.Tests\bin\Debug\net10.0\Catalog.Tests.exe
=== TEST EXECUTION SUMMARY ===
   Catalog.Tests  Total: 19, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 79.713s

$ docker ps -a --filter "ancestor=mcr.microsoft.com/mssql/server:2022-latest" --format "{{.Names}}  {{.Status}}"
shop133-db-init  Exited (0) 5 hours ago
shop133-sqlserver  Up 29 hours (healthy)
```

Del contenedor de test no queda rastro: los dos que aparecen son los del compose. El *reaper* de Testcontainers sigue vivo unos segundos y luego también se va:

```
$ docker ps -a --filter "name=testcontainers" --format "{{.Names}}  {{.Status}}"
testcontainers-ryuk-c88a5dbd-…  Up About a minute      ← justo al terminar
                                                       ← un minuto después, nada
```

### El stack de compose no se entera

```
$ docker compose ps -a --format "{{.Name}}  {{.Status}}"
shop133-catalog-api  Up 5 hours
shop133-db-init  Exited (0) 5 hours ago
shop133-jaeger  Up 29 hours
shop133-rabbitmq  Up 29 hours (healthy)
shop133-sqlserver  Up 29 hours (healthy)
```

### El host de test arranca como debe

De la traza de la ejecución, las tres líneas que confirman las decisiones 4 y 5 — y que el *content root* se resuelve solo, sin ayuda:

```
info: Microsoft.Hosting.Lifetime[0]
      Hosting environment: Testing
info: Microsoft.Hosting.Lifetime[0]
      Content root path: C:\personalprojects\shop133\src\Services\Catalog\Catalog.API
info: Microsoft.EntityFrameworkCore.Migrations[20402]
      Applying migration '20260819234304_SeedSouvenirCatalog'.
```

### Resumen

| # | Comprobación | Resultado |
|---|---|---|
| 1 | `dotnet build` de la solución, con el proyecto nuevo dentro | ✓ 0 warnings, 0 errores |
| 2 | Los 19 tests de componente pasan contra SQL Server real | ✓ |
| 3 | El `409` de Sku duplicado se produce por un `SqlException` 2601 | ✓ visible en la traza |
| 4 | La suite `Fast` de arquitectura sigue en 12 y en verde | ✓ |
| 5 | Dos ejecuciones seguidas dan el mismo resultado | ✓ 78,3 s y 79,7 s |
| 6 | El contenedor y su base de datos se destruyen al terminar | ✓ |
| 7 | El `sqlserver` del compose y su `1433` no se ven afectados | ✓ |
| 8 | El host de test arranca en `Testing`, sin User Secrets | ✓ |
| 9 | `dotnet test` como vía de ejecución | ✗ roto por el SDK 10.0.400 — ver arriba |

---

## Pendiente

- **`dotnet test` no funciona con el SDK 10.0.400.** Es lo único que queda abierto de este punto y no depende del código del repositorio. Las opciones son esperar a un SDK que lo arregle o fijar `global.json` a una banda que funcione — que hoy no se puede probar porque 10.0.303 ya no está instalada. Mientras tanto, los comandos de CLAUDE.md llevan la variante con el `.exe`. Hay que resolverlo antes de **8.3**, donde CI necesita que `dotnet test` corra las dos categorías.
- **Los tests de `/openapi/v1.json` y `/scalar`** de 1.5 se quedaron fuera del alcance acordado. Son baratos de añadir sobre esta fixture: el contenedor ya está levantado.
- **`Respawn` sigue sin entrar**, y no porque no valga: entrará el día que un servicio tenga tantos tests que una base por clase salga cara. `Orders.Tests` en 2.4 es el primer candidato.
- **La fixture es de Catalog, no del proyecto.** `SqlServerContainerFixture` es reutilizable tal cual, pero vive dentro de `Catalog.Tests`. Cuando **2.4** necesite lo mismo para `OrdersDb`, habrá que decidir si se copia o si sube a un proyecto compartido de utilidades de test — y esa decisión es mejor tomarla con el segundo caso delante que ahora.
- **La regla de arquitectura sobre proyectos de test** (decisión 7) no se escribió porque hoy no tiene a quién vigilar. El momento de reconsiderarla es cuando haya dos servicios con tests, en **3.7**.
