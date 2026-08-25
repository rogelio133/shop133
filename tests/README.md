# Orders.Tests — qué cubre y cómo ejecutarlo

Guía operativa de [`Services/Orders/Orders.Tests`](Services/Orders/Orders.Tests). El *porqué* de cada decisión
está en [docs/fase_2_4.md](../docs/fase_2_4.md); esto es lo otro: **qué se está probando y qué hay que teclear
para correrlo**.

---

## Qué es

**17 tests de integración** sobre `POST /orders` y `GET /orders/{id}`, todos marcados
`[Trait("Category", "Docker")]`. Levantan Orders.API en memoria contra un **SQL Server real en contenedor** y
contra un **Catalog suplantado por HTTP**.

No hay mocks del `DbContext` ni del `HttpClient`, y es deliberado: el proveedor InMemory de EF Core no impone
restricciones relacionales, y un `HttpMessageHandler` falso *simularía* las excepciones de `CatalogClient` en
vez de provocarlas — que es justamente lo que hay que ejercitar.

| Clase | Tests | Vida |
|---|---|---|
| `CreateOrderTests` | 11 | Sobrevive a la Fase 3 |
| `CatalogUnavailableTests` | 6 | **Se borra entera en `3.7`** |

---

## Cómo está montado

Los cuatro ficheros de [`Infrastructure/`](Services/Orders/Orders.Tests/Infrastructure):

| Fichero | Papel |
|---|---|
| `SqlServerContainerFixture.cs` | Un contenedor `mcr.microsoft.com/mssql/server:2022-latest` para todo el ensamblado. Expone `CreateDatabaseAsync` / `DropDatabaseAsync` / `ConnectionStringFor`. Puerto aleatorio a propósito: el 1433 del host ya lo ocupa el `sqlserver` de docker-compose. |
| `OrdersApiCollection.cs` | El `[CollectionDefinition]` que cuelga todas las clases del mismo fixture ⇒ **un solo contenedor** y ejecución en serie (xUnit paraleliza entre *collections*, nunca dentro de una). |
| `OrdersApiFactory.cs` | `WebApplicationFactory<Program>`. Crea la base `OrdersTests_NNN`, le aplica la migración de `2.2`, y expone `CountOrdersAsync()`. |
| `CatalogStub.cs` | Un `WireMockServer` que suplanta `GET /products/{id}`: 200, 404, 5xx, cuerpo malformado, respuesta lenta — más contadores de peticiones. |

Tres cosas que no son evidentes y que conviene saber antes de ejecutar o de añadir un test:

- **Una base de datos por test, no por clase.** xUnit construye la clase de test una vez por método y las
  fábricas son campos de instancia, así que cada test estrena su `OrdersTests_NNN`. Eso explica los ~2,8 s por
  test (`CREATE DATABASE` + migración + `DROP`) y, sobre todo, que se pueda afirmar *"no hay ningún pedido"*
  sin cualificarlo. Es la diferencia con `Catalog.Tests`, que sí tiene disciplina de datos sembrados.
- **El entorno es `Testing`, no el `Development` por defecto.** `Development` cargaría los User Secrets de
  Orders.API — la contraseña real de `orders_user` y el `OrdersDb` del compose — y si la línea del connection
  string se rompiera, la suite escribiría pedidos en la base de desarrollo sin que nada lo delatara.
- **Dos `UseSetting`, no uno.** `Program.cs` exige `ConnectionStrings:OrdersDb` **y** `Services:CatalogBaseUrl`
  *antes* de `app.Build()`, así que sustituir servicios en `ConfigureTestServices` llegaría tarde: el host ni
  se construiría. Por eso la URL del stub entra por el constructor de `OrdersApiFactory`, y por eso las clases
  de test tienen constructor explícito — un inicializador de campo no puede leer otro campo de instancia.

---

## Qué cubre cada clase

### `CreateOrderTests` — 11 tests

**Camino feliz (3)**

| Test | Qué afirma |
|---|---|
| `Create_ValidRequest_Returns201WithTheSnapshotCatalogDictated` | El `201` congela sku, nombre y precio **que dictó Catalog** — el cuerpo solo lleva `productId` y `quantity`. Además: `Id` acuñado por la entidad, `Location` en minúsculas, `Status = "Pending"`, `Total` calculado. |
| `Create_ValidRequest_IsRetrievableByGetById` | El pedido se relee por HTTP y las líneas vuelven **sin `Include`** — son un tipo *owned* desde `2.2`. |
| `Create_RepeatedProductId_GroupsLinesAndQueriesCatalogOnce` | Dos entradas del mismo producto ⇒ **una** línea con las cantidades sumadas y **una** sola petición a Catalog. |

**Producto inexistente (3)**

| Test | Qué afirma |
|---|---|
| `Create_UnknownProduct_Returns400NamingTheLine` | `400` y no `404`: lo que no existe es un valor del *cuerpo*, no el recurso de la URL. La clave del error es `Items[0].ProductId`. |
| `Create_SeveralUnknownProducts_ReturnsThemAllInOneProblem` | Todos los desconocidos salen en **un solo** `ValidationProblemDetails`, cada uno en su índice. |
| `Create_UnknownProduct_DoesNotPersistAnything` | Un `400` no deja fila en la base. |

**Validación del cuerpo (4)**

| Test | Qué afirma |
|---|---|
| `Create_MissingRequiredField_Returns400WithoutCallingCatalog` | La validación del modelo cortocircuita antes del controller: un cuerpo mal formado no cuesta viajes de red. |
| `Create_InvalidEmail_Returns400` | El formato del correo se valida en el DTO con `[EmailAddress]`, no en la entidad. |
| `Create_EmptyItems_Returns400` | Un pedido sin líneas lo rechazan dos guardas: el `[MinLength(1)]` del DTO (gana esta) y el constructor de `Order`. |
| `Create_CatalogReturnsOversizedSku_Returns400AndNot500` | **El caso "los dos servicios dejaron de encajar".** Catalog devuelve un sku de 51 caracteres; el `catch (ArgumentException)` del controller lo convierte en `400` **en vez de un 500**, y no se escribe nada. |

**GET (1)** — `GetById_UnknownId_Returns404`.

### `CatalogUnavailableTests` — 6 tests · `// PHASE-2 DEBT`

Los cinco modos de fallo de `CatalogClient`, más el de precedencia:

| Test | Qué afirma |
|---|---|
| `Create_CatalogRefusesConnection_Returns502AndCreatesNothing` | El escenario que el roadmap pide por su nombre. El stub se arranca y se para: el puerto se sabe cerrado porque acaba de cerrarse. |
| `Create_CatalogTimesOut_Returns502AfterTheClientTimeout` | Con cronómetro: corta **el cliente a los 5 s**, no el servidor a los 10. Era la única rama de `CatalogClient` que nadie había ejecutado hasta `2.4`. |
| `Create_CatalogReturnsServerError_Returns502AndCreatesNothing` | Un `5xx` no es "el producto no existe" (eso es el `404`, que se traduce a `null`), así que no puede acabar en un `400` culpando al cliente. |
| `Create_CatalogReturnsMalformedBody_Returns502AndCreatesNothing` | `200` con un cuerpo que no deserializa. Sin el `catch (JsonException)` sería un `500` sin traducir. |
| `Create_CatalogFailsOnTheSecondLine_CreatesNothing` | **La lección central del acoplamiento síncrono**: la primera línea se resuelve y la segunda revienta ⇒ no hay pedidos a medias. |
| `Create_UnknownProductAndCatalogDown_Returns502NotBadRequest` | Los dos fallos a la vez y gana el `502`: no se le puede pedir al cliente que arregle un pedido cuya validación no ha terminado. |

**Los seis afirman además que la tabla `Orders` queda vacía**, contando filas con `factory.CountOrdersAsync`.
No es decoración: Orders.API no tiene un `GET /orders` que liste (llega en `6.2`), así que preguntar por un id
que nunca se devolvió no probaría nada — y el código de estado por sí solo no distingue *"no se guardó"* de
*"se guardó y la respuesta falló después"*.

> **Esta clase entera, `CatalogStub` y el paquete `WireMock.Net` se borran en `3.7`**, cuando Orders publique
> `OrderCreated` en lugar de preguntar: entonces no habrá ningún servicio cuya caída pueda impedir que se cree
> el pedido. No es deuda que haya que mantener — es un test que *deja de tener sentido*.

---

## Pasos para ejecutar

### Requisitos previos

1. **Docker Desktop corriendo.** Los 17 tests son `Category=Docker`; sin demonio, la suite falla al arrancar el
   fixture. **No hace falta `docker compose up`**: Testcontainers levanta su propio contenedor en un puerto
   aleatorio, precisamente para no chocar con el `sqlserver` del compose que ya ocupa el 1433.
2. **La imagen `mcr.microsoft.com/mssql/server:2022-latest`.** Es la misma etiqueta que `docker-compose.yml`,
   así que normalmente ya está descargada; la primera vez son ~1,5 GB.

### Ejecutar

```powershell
# 1. Compilar
dotnet build tests/Services/Orders/Orders.Tests

# 2. Ejecutar — el proyecto es su propio ejecutable (regla 5b de CLAUDE.md).
#    17 tests, ~76 s.
tests\Services\Orders\Orders.Tests\bin\Debug\net10.0\Orders.Tests.exe

# 3. Filtrar. Ojo: la opción es `-trait` / `-class`, con UN guion.
tests\Services\Orders\Orders.Tests\bin\Debug\net10.0\Orders.Tests.exe -class "Orders.Tests.CreateOrderTests"
tests\Services\Orders\Orders.Tests\bin\Debug\net10.0\Orders.Tests.exe -class "Orders.Tests.CatalogUnavailableTests"
tests\Services\Orders\Orders.Tests\bin\Debug\net10.0\Orders.Tests.exe -trait "Category=Docker"

# 4. Ante un fallo, redirigir a fichero en vez de leer la cola de la consola:
#    EF Core registra a nivel info y el detalle se pierde de vista.
tests\Services\Orders\Orders.Tests\bin\Debug\net10.0\Orders.Tests.exe > orders-tests.log 2>&1
```

### Dos avisos que cuestan tiempo

- **`dotnet test` está roto desde que el SDK pasó a 10.0.400.** Reporta `Zero tests ran / error: 1` en ~150 ms.
  No es un problema de este proyecto: pasa igual con `Shop133.ArchitectureTests`, que no necesita Docker. El
  rodeo es el `.exe` de arriba, y queda pendiente de arreglar antes de `8.3`.
  Y cuidado con el filtro: `--filter-trait` es una opción de `dotnet test`, no del ejecutable — pasársela al
  `.exe` da `error: unknown option`.
- **Smart App Control puede tumbar la *primera* ejecución tras restaurar un paquete**, con
  `An Application Control policy has blocked this file. (0x800711C7)` nombrando una DLL de Testcontainers.
  **Vuelve a lanzarlo y ya**: el bloqueo es transitorio mientras Windows consulta el Intelligent Security
  Graph. Nunca degrades el paquete persiguiéndolo (el bloqueo se mueve al ensamblado que sea nuevo) ni
  desactives Smart App Control: **no se puede volver a activar sin reinstalar Windows**.

---

## Añadir un test nuevo

- La clase lleva `[Collection(OrdersApiCollection.Name)]` y `[Trait("Category", "Docker")]` — el trait va en la
  clase, nunca en cada método.
- Nombre del método: `Method_Scenario_ExpectedResult`. Identificadores en inglés; la prosa de los `<summary>`,
  en español.
- Aserciones con el `Assert` de xUnit. **Nada de FluentAssertions**: la 8.x pasó a licencia comercial.
- Un `.cs` nuevo **no necesita tocar el `.csproj`** — el SDK lo incluye por glob implícito. Si Visual Studio no
  lo muestra, lo que está desactualizado es su caché, no el proyecto.
