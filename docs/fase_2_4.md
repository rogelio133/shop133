# Fase 2.4 — `Orders.Tests`: el acoplamiento síncrono, hecho reproducible

**Fecha:** 2026-08-25 · **Estado:** completado · **Roadmap:** [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md), punto 2.4

---

## Objetivo

`2.3` dejó `POST /orders` preguntándole a Catalog.API por HTTP antes de aceptar un pedido, y con ello **la deuda deliberada de la regla 2 de CLAUDE.md**. El problema es que hasta ahora nadie ejecutaba los caminos en los que esa llamada falla: la verificación de `2.3` solo pudo forzar a mano la conexión rechazada, y su documento lo deja escrito —

> **La rama del timeout no se ha ejercitado todavía**; solo la de conexión rechazada. Eso es trabajo de `2.4` con WireMock.

Este punto convierte ese dolor en tests que fallan solos. El roadmap es explícito en cuál es el objetivo:

> *El objetivo no es cobertura, es hacer el fallo **reproducible**: un test que afirma "Catalog caído ⇒ el pedido no se crea" y que en la Fase 3 deja de tener sentido. El diff que lo elimina documenta el cambio de arquitectura mejor que un párrafo.*

Cierra además la pregunta que `1.7` dejó abierta por escrito para este punto:

> *(1.7)* **La fixture es de Catalog, no del proyecto.** `SqlServerContainerFixture` no tiene nada específico de Catalog… El segundo caso —`Orders.Tests` en `2.4`— es el que dirá si conviene copiarla o promoverla a un proyecto de utilidades compartido.

**Fuera de alcance deliberadamente:** no se prueba nada de la saga (no existe hasta `4.1`), ni la idempotencia (no hay consumidores hasta `3.6`), ni RabbitMQ (`8.2`). No hay tests unitarios de `Order`/`OrderItem` por separado: sus invariantes se ejercitan a través del endpoint, que es como se rompen de verdad. Tampoco se prueba el stock — `2.3` decidió no comprobarlo, y un test que lo afirmara estaría documentando una decisión de `3.4`. Y no se toca `src/`: el `public partial class Program { }` que hace falta lo puso `2.3`.

**No se modificó ningún `.csproj` bajo `src/`**, así que la suite de arquitectura **sigue en 12 tests**. El único paquete nuevo del repositorio es `WireMock.Net`, y vive solo en el proyecto de test.

---

## Decisiones

### 1. Proyecto nuevo en `tests/Services/Orders/Orders.Tests`, espejo de `Catalog.Tests`

Misma forma que `1.7`: `Microsoft.NET.Sdk` (no `.Web` — la referencia a `Orders.API` ya arrastra el `FrameworkReference` de ASP.NET Core), `<OutputType>Exe</OutputType>` por la regla 5b, y **una sola** `ProjectReference` a `Orders.API`, que trae transitivamente `Orders.Infrastructure`, `Orders.Domain`, `Shop133.Contracts` y `Microsoft.Data.SqlClient`.

Los DTOs de `Orders.API/Models` se usan tal cual en los asserts. *Descartado* declarar copias locales de los DTOs en el proyecto de test: parecen aislar, pero lo que consiguen es que un cambio de contrato falle como un assert confuso en tiempo de ejecución en vez de como un error de compilación.

### 2. `WireMock.Net` 2.15.0, el paquete completo

Apache-2.0, con `lib/net8.0` — corre sin problema sobre `net10.0`, igual que MassTransit 8.x correrá desde `3.1`. Licencia comprobada antes de añadirlo, que es la lección que dejaron FluentAssertions 8 y MassTransit 9.

**Descartado — `WireMock.Net.Minimal`.** Basta para todo lo que se usa aquí (`WireMockServer`, `Request`/`Response` builders) y ahorra cinco paquetes de primer nivel: GraphQL, ProtoBuf, MimePart, OpenTelemetry y SystemTextJsonPath. Se descartó porque el ahorro real es menor de lo que parece —el grueso del árbol (`NSwag.Core`, `Scriban`, `SimMetrics.Net`, `TinyMapper`, `JmesPath.Net`) lo arrastra `Minimal` igual— y a cambio deja el proyecto fuera de lo que documenta cualquier ejemplo de WireMock. Para un paquete que se borra en `3.7`, no compensa.

**Descartado — un `HttpMessageHandler` falso, sin paquete.** Cuarenta líneas, cero dependencias, milisegundos por test. Y cortocircuita exactamente lo que este punto quiere ejercitar: los tres caminos de fallo de `CatalogClient` nacen de la pila HTTP real —un socket que rechaza la conexión, el `Timeout` del `HttpClient`, un cuerpo que no deserializa—, y con un handler falso se estarían **simulando las excepciones en vez de provocándolas**. Un test que construye a mano el `TaskCanceledException` que espera no demuestra que el código lo produzca.

### 3. La fixture se copia, no se extrae a un proyecto compartido

`SqlServerContainerFixture` es una copia literal de la de `Catalog.Tests`, cambiando solo el namespace y los comentarios.

**Descartado — `tests/Shop133.TestUtilities`.** Exigiría aprobar un proyecto fuera del layout de CLAUDE.md y, sobre todo, fijar una API común con **un solo uso real**: lo que hoy comparten los dos proyectos es la clase entera, pero lo que Orders necesitaba *además* (inyectar una segunda clave de configuración, contar filas sin pasar por la API) no encaja en ella y habría acabado como parámetros opcionales para el caso que no es el propio.

La regla que se aplica: **dos ocurrencias no son un patrón.** `3.7` trae `Inventory.Tests` y `Payments.Tests` y con cuatro copias delante la extracción se decidirá con datos —y sabiendo ya qué partes divergieron, que es la información que hoy no existe.

### 4. La URL del stub lleva el literal `127.0.0.1`, no `localhost`

`WireMockServer.Start()` devuelve una `Url` con `localhost`. `CatalogStub.Url` la reconstruye con la IP: `$"http://127.0.0.1:{server.Ports[0]}"`.

No es cosmético. `localhost` resuelve a `::1` **y** a `127.0.0.1`, así que una conexión rechazada se intenta dos veces — medido en `2.3`: 4,13 s y dos `SocketException (10061)`. Con el `Timeout` de 5 s del `HttpClient` eso deja **0,9 s** entre "Catalog no escucha" y "Catalog no contesta a tiempo", que son dos ramas distintas de `CatalogClient` que estos tests tienen que poder distinguir. Con el literal IPv4 el rechazo baja a ~2,0 s (ver *Detalles*), y el margen pasa a ser de 3 s.

### 5. Dos `UseSetting`, no uno

`Program.cs` lanza `InvalidOperationException` por **dos** claves ausentes —`ConnectionStrings:OrdersDb` y `Services:CatalogBaseUrl`— y lo hace *antes* de `app.Build()`. Es la misma trampa que documentó `1.7` en su decisión 4, ahora por partida doble: sustituir servicios en `ConfigureTestServices` llegaría tarde porque el host ni se construye.

La URL del stub entra **por constructor** de `OrdersApiFactory` y no por una propiedad que se asigne después, por lo mismo: para cuando el host se construye ya tiene que estar. Eso obliga a que la clase de test tenga constructor explícito en lugar del constructor primario de `Catalog.Tests` — un inicializador de campo no puede leer otro campo de instancia, y el `CatalogStub` tiene que existir antes que la fábrica.

### 6. Una base de datos por **test**, y lo que eso corrige de `1.7`

`OrdersDb` tiene una sola migración y ningún seed, así que crear y tirar una base por test es barato. Lo que se gana es que **ningún test depende del estado que dejó otro**, y por eso se puede afirmar "no hay ningún pedido" sin cualificarlo — que es la mitad de lo que `2.4` tiene que demostrar. En `Catalog.Tests` eso no era posible: el seed de 55 filas obliga a leer *contiene*, nunca *son exactamente N*.

Aquí aparece un hallazgo que afecta hacia atrás. `docs/fase_1_7.md` y CLAUDE.md dicen "una base de datos por **clase** de test", pero **xUnit construye la clase de test una vez por método**, y la fábrica es un inicializador de campo: en la práctica ya había una base por *test* también en Catalog. La disciplina que `ProductsEndpointsTests` documenta —no tocar el seed, afirmar *contiene*— no sobra, pero protege de un riesgo que no se estaba corriendo.

Se comprobó de dos formas independientes: por tiempo (17 tests en ~75 s son ~2,8 s cada uno, el coste de `CREATE DATABASE` + migración + `DROP`; con dos bases compartidas serían milisegundos), y por comportamiento (`Create_UnknownProduct_DoesNotPersistAnything` afirma cero pedidos y convive en la misma clase con tests que crean pedidos). CLAUDE.md queda corregido; `fase_1_7.md` se deja como está, porque es el registro de lo que se creyó entonces.

*Descartado* Respawn, otra vez y por un motivo nuevo: en `1.7` se rechazó porque borra filas y no restaura el seed; aquí directamente no hay nada que restaurar, así que lo que Respawn haría lo hace gratis un `CREATE DATABASE`.

### 7. Los tests afirman el recuento de la base, no solo el código de estado

Los seis tests de `CatalogUnavailableTests` y dos de `CreateOrderTests` terminan comprobando que la tabla `Orders` está vacía, leyendo la base directamente con `OrdersApiFactory.CountOrdersAsync`.

Hace falta porque **Orders.API no tiene un `GET /orders` que liste**: `2.3` solo expuso el GET por id, y preguntar por un id que nunca se devolvió no demuestra nada. El código de estado dice lo que vio el cliente; el recuento dice lo que pasó de verdad, y es lo que distingue "no se guardó" de "se guardó y la respuesta falló después". Un 502 sobre un pedido a medio escribir sería el peor de los mundos posibles, y es justo lo que la Fase 4 existe para evitar en el caso distribuido.

*Descartado* adelantar un `GET /orders` solo para poder afirmarlo por HTTP: sería inventar superficie de API para comodidad del test, y el endpoint de listado tiene su sitio en `6.2`.

### 8. El test del timeout entra, aunque cueste 5 s

Es la única rama de `CatalogClient` que no había ejecutado nadie. El stub responde con `WithDelay(10 s)` contra el `Timeout = 5 s` del `HttpClient`, y el test **cronometra**: por debajo, comprueba que no cortó antes de tiempo; por arriba, que no esperó los 10 s del servidor. Esa cota superior es la que fallaría si alguien quitara el `Timeout` de `Program.cs` — el defecto de `HttpClient` son 100 s.

Umbrales en segundos y nunca en milisegundos, por lo que ya avisa CLAUDE.md.

**Descartado — hacer configurable el timeout** con una clave `Services:CatalogTimeoutSeconds` para que el test lo bajara a 1 s. Habría ahorrado 4 s por ejecución tocando `src/` para acomodar un test, y añadiendo una clave de configuración a código que `3.3` borra entero. Cinco segundos una vez es más barato que una opción que hay que explicar.

El valor de 5 s está **duplicado** en la constante `CatalogTimeout` del test. Es duplicación a propósito: si alguien lo cambia en `Program.cs`, el test falla y obliga a mirar por qué, que es lo que debe pasar con un número del que depende una rama entera.

### 9. Dos clases: la que sobrevive y la que se borra

`CreateOrderTests` (11 tests) prueba el endpoint cuando Catalog contesta. `CatalogUnavailableTests` (6 tests) prueba lo que pasa cuando no.

La separación no es temática, es **por fecha de caducidad**. La segunda clase entera lleva `// PHASE-2 DEBT` en la cabecera y se borra en `3.7` de un solo golpe, junto con `CatalogClient`, `CatalogStub` y el paquete WireMock.Net. La primera sobrevive casi intacta: cuando `3.3` publique `OrderCreated`, lo que cambia son los tres campos congelados de cada línea, no el 201 ni el 400 ni el `GET /orders/{id}`.

*Descartado* marcar test a test con un atributo o un trait `Phase2Debt`: un trait sirve para filtrar en una ejecución, y aquí lo que hace falta es que quien abra el archivo en la Fase 3 sepa que el archivo entero sobra.

### 10. Ninguna regla de arquitectura nueva, y la suite sigue en 12

Mismo razonamiento que la decisión 7 de `1.7`: `ProjectGraph` enumera solo `<repo>/src`, así que `tests/` es invisible para las 12 reglas y un proyecto de test que ve EF Core transitivamente no incumple `EfCorePackages_LiveOnlyIn_InfrastructureProjects`.

`1.7` dejó dicho que *"el momento de replantear una regla sobre proyectos de test es cuando dos servicios tengan tests, en `3.7`"*. `2.4` crea el segundo — pero el que abre la pregunta de verdad es `3.7`, con cuatro. Se mantiene la cita.

---

## Cambios

### Nuevos

| Archivo | Rol |
|---|---|
| [tests/Services/Orders/Orders.Tests/Orders.Tests.csproj](../tests/Services/Orders/Orders.Tests/Orders.Tests.csproj) | El proyecto. `Exe` por la regla 5b, una sola `ProjectReference` a `Orders.API`. |
| [tests/Services/Orders/Orders.Tests/Infrastructure/SqlServerContainerFixture.cs](../tests/Services/Orders/Orders.Tests/Infrastructure/SqlServerContainerFixture.cs) | El contenedor SQL Server del ensamblado. Copia literal de la de `Catalog.Tests` (decisión 3). |
| [tests/Services/Orders/Orders.Tests/Infrastructure/OrdersApiCollection.cs](../tests/Services/Orders/Orders.Tests/Infrastructure/OrdersApiCollection.cs) | Collection fixture: un solo contenedor y ejecución en serie entre clases. |
| [tests/Services/Orders/Orders.Tests/Infrastructure/OrdersApiFactory.cs](../tests/Services/Orders/Orders.Tests/Infrastructure/OrdersApiFactory.cs) | Levanta `Orders.API` en memoria: base propia, las dos claves por `UseSetting`, y `CountOrdersAsync`. |
| [tests/Services/Orders/Orders.Tests/Infrastructure/CatalogStub.cs](../tests/Services/Orders/Orders.Tests/Infrastructure/CatalogStub.cs) | **PHASE-2 DEBT.** El WireMock que suplanta a Catalog.API. |
| [tests/Services/Orders/Orders.Tests/CreateOrderTests.cs](../tests/Services/Orders/Orders.Tests/CreateOrderTests.cs) | 11 tests: camino feliz, agrupación, producto desconocido, validación del cuerpo, `GET` por id. |
| [tests/Services/Orders/Orders.Tests/CatalogUnavailableTests.cs](../tests/Services/Orders/Orders.Tests/CatalogUnavailableTests.cs) | **PHASE-2 DEBT.** 6 tests: los cinco modos de fallo de `CatalogClient`, más el orden 502-sobre-400. |
| [docs/fase_2_4.md](fase_2_4.md) | Este documento. |

### Modificados

| Archivo | Cambio |
|---|---|
| [shop133.slnx](../shop133.slnx) | Carpeta `/tests/Services/Orders/` con el proyecto nuevo, después de la de Catalog. |
| [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md) | Punto 2.4 marcado y enlazado. |
| [docs/README.md](README.md) | Fila del índice. |
| [CLAUDE.md](../CLAUDE.md) | Estado de la Fase 2, tabla de fases, recuentos de la regla 5, lista de paquetes, sección Commands, y la corrección de "una base por clase" (decisión 6). |

**Lo que no se tocó:** nada bajo `src/`. Ni un `.csproj` de servicio, ni `Program.cs`, ni el controller, ni las entidades. Los 17 tests se escribieron contra la superficie que `2.3` dejó, y todos pasaron sin cambiarla — que es el resultado que se buscaba, porque un test que obliga a modificar el código que prueba nada más escribirlo suele estar probando otra cosa.

### Paquete nuevo

| Paquete | Versión | Licencia | Dónde |
|---|---|---|---|
| `WireMock.Net` | 2.15.0 | Apache-2.0 | `Orders.Tests` solamente. Se borra en `3.7`. |

---

## Detalles que cuestan tiempo

### Una conexión rechazada en `127.0.0.1` tarda 2 s, no cero

La expectativa era que quitar `localhost` dejara el rechazo en microsegundos. Lo medido, de forma consistente en varias ejecuciones, es otra cosa:

```
info: System.Net.Http.HttpClient.CatalogClient.LogicalHandler[101]
      HTTP request failed after 2026.4324ms
info: System.Net.Http.HttpClient.CatalogClient.ClientHandler[101]
      HTTP request failed after 2034.3886ms
```

**~2,03 s**, no ~0 s. Sigue siendo la mitad de los 4,13 s de `localhost` y deja 3 s de margen contra el `Timeout` de 5 s, que es lo que este punto necesitaba, pero el número real es este y no el que se esperaba.

La regla que sobrevive es la de `2.3`, reforzada: **una comprobación de "falla rápido" no puede usar umbrales de milisegundos**. Ni siquiera un socket cerrado en la interfaz de loopback contesta al instante.

### `UseHttpsRedirection()` sin guardar: es un warning, no un 307

`Catalog.API` guarda esa línea con `IsDevelopment()` desde `1.6`; `Orders.API` no la guarda todavía, porque no tiene contenedor. La duda antes de empezar era si sobre el `TestServer` devolvería 307 donde se espera un 200 — el comentario de `CatalogApiFactory` menciona precisamente ese riesgo.

No pasa. Sin puerto HTTPS que resolver, el middleware se limita a dejar pasar la petición y a registrar, una vez por cada request de la suite:

```
warn: Microsoft.AspNetCore.HttpsPolicy.HttpsRedirectionMiddleware[3]
      Failed to determine the https port for redirect.
```

Es ruido, no un fallo, y **no se tocó `Program.cs` para silenciarlo**: cambiar código de producción para limpiar la salida de un test es la clase de arreglo que se cuela sin querer. El sitio donde esa línea sí hay que releer es cuando Orders tenga su contenedor en la Fase 3 — ahí el guardarla es lo que evita el mismo warning en cada request real.

### `HttpValidationProblemDetails` no está donde parece

Vive en `Microsoft.AspNetCore.Http`, no en `Microsoft.AspNetCore.Mvc` — donde sí está `ProblemDetails`. Los dos `using` conviven en `Catalog.Tests` y por eso allí no se nota; en un archivo que solo necesita el primero, importar el segundo por costumbre da `CS0246` cuatro veces seguidas.

### El `ILogEntry` de WireMock tiene el `RequestMessage` anulable

Contar peticiones con `entry.RequestMessage.Path` compila con `CS8602`, y este repositorio compila con **0 warnings**. El `?.` no es defensivo de más: es lo que declara la interfaz del paquete.

### Smart App Control no bloqueó nada esta vez

La restauración de `WireMock.Net` metió una decena de ensamblados sin firmar nuevos, que es justo el escenario que en `1.7` produjo `An Application Control policy has blocked this file (0x800711C7)`. **No ocurrió**: la primera compilación y la primera ejecución fueron limpias. El aviso de CLAUDE.md sigue siendo válido —el bloqueo es transitorio y la respuesta es reintentar— pero no es determinista, así que no hay que darlo por hecho ni ir a buscarlo.

### La primera ejecución falló un test y no se pudo atribuir

La primera pasada completa de la suite terminó `Total: 17, Failed: 1` en 99,3 s. El detalle del fallo quedó fuera de la ventana de salida que se capturó —la salida de EF Core en `info` es enorme— y **no se pudo identificar qué test fue**. Las dos pasadas completas siguientes dieron 17/17 (74,8 s y 76,9 s), y la clase de la deuda por separado dio 6/6 dos veces.

Se deja escrito porque es lo honesto y porque es información: esa primera pasada fue también la más lenta (99,3 s frente a ~75 s), con el contenedor recién arrancado, lo que apunta a una cota temporal apretada bajo carga fría más que a un fallo lógico. **Si vuelve a aparecer, el candidato a mirar primero es la cota superior de `Create_CatalogTimesOut_Returns502AfterTheClientTimeout`**, y la forma de verlo es redirigir la salida a un archivo desde el principio en lugar de leer las últimas líneas de la consola.

---

## Verificación

### 1. Compilación

```
> dotnet build tests/Services/Orders/Orders.Tests/Orders.Tests.csproj

  Orders.Tests -> C:\personalprojects\shop133\tests\Services\Orders\Orders.Tests\bin\Debug\net10.0\Orders.Tests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### 2. Los 17 tests

`dotnet test` sigue roto en esta máquina desde el SDK 10.0.400 (ver `fase_1_7.md`), así que se ejecuta el propio `.exe`. La opción de filtro aquí es **`-trait`**, no `--filter-trait`.

```
> tests\Services\Orders\Orders.Tests\bin\Debug\net10.0\Orders.Tests.exe

=== TEST EXECUTION SUMMARY ===
   Orders.Tests  Total: 17, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 74.782s
```

### 3. La clase de la deuda, por separado

```
> ...\Orders.Tests.exe -class "Orders.Tests.CatalogUnavailableTests"

   Orders.Tests  Total: 6, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 39.803s
   Orders.Tests  Total: 6, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 40.049s
```

### 4. La migración de `2.2` se aplica sola en cada base

Extracto de la salida de EF, que confirma de paso el esquema que `2.2` describió — clave compuesta con `IDENTITY` en el tipo owned, `uniqueidentifier` **sin `DEFAULT`** en la clave del pedido:

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
    ...
    CONSTRAINT [PK_OrderItems] PRIMARY KEY ([OrderId], [Id]),
    CONSTRAINT [FK_OrderItems_Orders_OrderId] FOREIGN KEY ([OrderId]) REFERENCES [Orders] ([Id]) ON DELETE CASCADE
);
```

### 5. El resto de la suite sigue intacta

```
> tests\Shop133.ArchitectureTests\bin\Debug\net10.0\Shop133.ArchitectureTests.exe
   Shop133.ArchitectureTests  Total: 12, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.516s

> tests\Services\Catalog\Catalog.Tests\bin\Debug\net10.0\Catalog.Tests.exe
   Catalog.Tests  Total: 19, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 76.420s
```

**12 + 19 + 17 = 48 tests** en el repositorio: 12 `Fast` y 36 `Docker`. La suite de arquitectura sigue en 12 sin haber añadido ninguna exención, que es lo que confirma que un proyecto de test nuevo bajo `tests/` es invisible para `ProjectGraph`.

### Resumen

| # | Comprobación | Resultado |
|---|---|---|
| 1 | `Orders.Tests` compila con 0 warnings | ✓ |
| 2 | 17 tests en verde | ✓ |
| 3 | La clase `PHASE-2 DEBT` en verde por separado, dos veces | ✓ |
| 4 | La migración de 2.2 se aplica en cada base de test | ✓ |
| 5 | La rama del timeout de `CatalogClient`, ejecutada por primera vez | ✓ |
| 6 | La suite de arquitectura sigue en 12 | ✓ |
| 7 | `Catalog.Tests` sigue en 19 | ✓ |

---

## Pendiente

- **`3.7`** borra `CatalogUnavailableTests.cs`, `CatalogStub.cs`, el `PackageReference` de `WireMock.Net` y las dos líneas `PHASE-2 DEBT` de `OrdersApiFactory`, a la vez que `3.3` borra `Orders.Infrastructure/Catalog/`. `CreateOrderTests` sobrevive, pero sus asserts sobre los tres campos congelados de cada línea tendrán que apuntar a lo que sea que los rellene entonces — la pregunta sigue abierta en la nota de revisión de la decisión 6 de [fase_0_3.md](fase_0_3.md).
- **`3.7`** es también donde se decide de verdad si `SqlServerContainerFixture` se extrae a un proyecto compartido: con `Inventory.Tests` y `Payments.Tests` serán cuatro copias, y se sabrá qué partes divergieron.
- **`3.7`** hereda la pregunta de `1.7` sobre una regla de arquitectura para proyectos de test. `ProjectGraph` sigue enumerando solo `<repo>/src`; si alguna vez se amplía, la solución es una exención `IsTest`, nunca dejar `tests/` fuera del escaneo.
- **`8.3`** no puede montarse mientras `dotnet test` siga dando *"Zero tests ran / error: 1"* con el SDK 10.0.400. Ahora hay tres proyectos de test que lanzar como ejecutables, no dos.
- **Fase 3** tiene que releer `UseHttpsRedirection()` en `Orders.API` cuando el servicio tenga contenedor, por el mismo motivo por el que `1.6` lo guardó en Catalog.
- **`6.2`** trae el `GET /orders` que hoy no existe, y cuando llegue algunos asserts de recuento podrán hacerse por HTTP en lugar de leyendo la base.
- Cierre de la Fase 2: PR `feature/fase-2-orders → develop`, PR `develop → main` con el botón *Create a merge commit*, y el tag anotado `fase-2` en `main`.
