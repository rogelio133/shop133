# Orders.Tests — qué cubre y cómo ejecutarlo

Guía operativa de [`Services/Orders/Orders.Tests`](Services/Orders/Orders.Tests). El *porqué* de cada decisión
está en [docs/fase_2_4.md](../docs/fase_2_4.md) y, para lo que cambió al borrar la deuda síncrona, en
[docs/fase_3_3.md](../docs/fase_3_3.md); esto es lo otro: **qué se está probando y qué hay que teclear para
correrlo**.

---

## Qué es

**10 tests de integración** sobre `POST /orders` y `GET /orders/{id}`, todos marcados
`[Trait("Category", "Docker")]`. Levantan Orders.API en memoria contra un **SQL Server real en contenedor**.

No hay mocks del `DbContext`, y es deliberado: el proveedor InMemory de EF Core no impone restricciones
relacionales y deja pasar bugs que en SQL Server explotan.

| Clase | Tests | Vida |
|---|---|---|
| `CreateOrderTests` | 10 | Viva |

> **Eran 17 hasta `3.3`.** `CatalogUnavailableTests` (6 tests) y `CatalogStub` se borraron enteros, junto con el
> paquete `WireMock.Net` y `Orders.Infrastructure/Catalog/`: al dejar Orders de llamar a Catalog no queda ningún
> servicio cuya caída pueda impedir que se cree el pedido, así que esos tests **dejaron de tener sentido** — no
> son cobertura perdida. El roadmap situaba ese borrado en `3.7`, pero se adelantó porque dejaron de compilar en
> cuanto `OrdersApiFactory` perdió su parámetro `catalogBaseUrl` (decisión 8 de `fase_3_3.md`).

---

## Cómo está montado

Los tres ficheros de [`Infrastructure/`](Services/Orders/Orders.Tests/Infrastructure):

| Fichero | Papel |
|---|---|
| `SqlServerContainerFixture.cs` | Un contenedor `mcr.microsoft.com/mssql/server:2022-latest` para todo el ensamblado. Expone `CreateDatabaseAsync` / `DropDatabaseAsync` / `ConnectionStringFor`. Puerto aleatorio a propósito: el 1433 del host ya lo ocupa el `sqlserver` de docker-compose. |
| `OrdersApiCollection.cs` | El `[CollectionDefinition]` que cuelga todas las clases del mismo fixture ⇒ **un solo contenedor** y ejecución en serie (xUnit paraleliza entre *collections*, nunca dentro de una). |
| `OrdersApiFactory.cs` | `WebApplicationFactory<Program>`. Crea la base `OrdersTests_NNN`, le aplica la migración de `2.2`, y expone `CountOrdersAsync()`. |

Tres cosas que no son evidentes y que conviene saber antes de ejecutar o de añadir un test:

- **Una base de datos por test, no por clase.** xUnit construye la clase de test una vez por método y la
  fábrica es un campo de instancia, así que cada test estrena su `OrdersTests_NNN`. Eso explica los ~2,8 s por
  test (`CREATE DATABASE` + migración + `DROP`) y, sobre todo, que se pueda afirmar *"no hay ningún pedido"*
  sin cualificarlo. Es la diferencia con `Catalog.Tests`, que sí tiene disciplina de datos sembrados.
- **El entorno es `Testing`, no el `Development` por defecto.** `Development` cargaría los User Secrets de
  Orders.API — la contraseña real de `orders_user` y el `OrdersDb` del compose — y si la línea del connection
  string se rompiera, la suite escribiría pedidos en la base de desarrollo sin que nada lo delatara.
- **Dos `UseSetting`, no uno — y eran tres hasta `3.3`.** `Program.cs` exige `ConnectionStrings:OrdersDb` **y**
  `ConnectionStrings:RabbitMq` *antes* de `app.Build()`, así que sustituir servicios en `ConfigureTestServices`
  llegaría tarde: el host ni se construiría. La regla que esto ilustra vale para toda la fase: **cada guarda
  nueva en un `Program.cs` es una línea nueva en su fábrica de tests, y cada guarda que se va se lleva la
  suya.** Nada más que esta suite detecta el desajuste.

---

## ⚠️ Desde `3.3` hace falta RabbitMQ, no solo Docker

Esto es nuevo y es la causa de fallo más probable.

Hasta `3.1`, el `UseSetting` de `ConnectionStrings:RabbitMq` era decorativo: nadie publicaba, y un bus sin
broker se limita a avisar y reintentar en segundo plano, así que la suite **pasaba con RabbitMQ parado**.

Desde `3.3`, `POST /orders` publica `OrderCreated` de verdad, y un `Publish` sobre el transporte de RabbitMQ
**espera a que haya conexión en lugar de fallar rápido**. Con el broker caído la petición no da error: se queda
colgada hasta que el test expire.

```powershell
docker compose up -d    # ahora es prerrequisito, no solo comodidad
```

Se irá en `3.7`, cuando el harness en memoria de MassTransit sustituya al broker real.

---

## Qué cubre `CreateOrderTests` — 10 tests

**Camino feliz (3)**

| Test | Qué afirma |
|---|---|
| `Create_ValidRequest_Returns201WithTheSnapshotTheClientSent` | El `201` congela el sku, el nombre y el precio **que mandó el cliente**. En `2.4` este test afirmaba lo contrario —que los dictaba Catalog— y ese giro es el contenido de `3.3`. Además: `Id` acuñado por la entidad, `Location` en minúsculas, `Status = "Pending"`, `Total` calculado. |
| `Create_ValidRequest_IsRetrievableByGetById` | El pedido se relee por HTTP y las líneas vuelven **sin `Include`** — son un tipo *owned* desde `2.2`. |
| `Create_RepeatedProductId_GroupsLinesSummingQuantities` | Dos entradas del mismo producto ⇒ **una** línea con las cantidades sumadas. Agrupar es un invariante de `Order`: un `ReserveStock` con dos entradas del mismo producto obligaría a Inventory a adivinar. |

**El cambio de arquitectura, en forma ejecutable (1)**

| Test | Qué afirma |
|---|---|
| `Create_ProductThatCatalogDoesNotKnow_Returns201Anyway` | **El test que resume `3.3`.** Un producto que no existe en el catálogo se acepta; en `2.4` esta misma petición daba `400`. Orders ya no pregunta a nadie, así que no lo puede saber: quien lo descubrirá es Inventory en `3.4` con un `StockRejected`. El pedido no se rechaza, se **cancela**. |

**Validación del cuerpo (5)**

| Test | Qué afirma |
|---|---|
| `Create_InconsistentSnapshotForSameProduct_Returns400` | **La rama que `3.3` estrena.** Al venir la foto en el cuerpo, dos líneas del mismo producto pueden contradecirse en precio. Quedarse con la primera haría pagar al cliente un importe que no eligió. Clave del error: `Items[0].ProductId`. |
| `Create_MissingRequiredField_Returns400` | La validación del modelo cortocircuita antes del controller, y no se escribe nada en la base. |
| `Create_InvalidEmail_Returns400` | El formato del correo se valida en el DTO con `[EmailAddress]`, no en la entidad. |
| `Create_EmptyItems_Returns400` | Un pedido sin líneas lo rechazan dos guardas: el `[MinLength(1)]` del DTO (gana esta) y el constructor de `Order`. |
| `Create_OversizedSku_Returns400` | Un sku de 51 caracteres. **Ojo: cambió de dueño en `3.3`** — antes lo paraba el guard de la entidad y lo traducía el `catch (ArgumentException)` del controller (sin él habría sido un `500`, y eso era lo que el test afirmaba); ahora el valor viene en el cuerpo y lo corta el `[MaxLength]` del DTO antes de ejecutar la acción. El catch sigue como defensa en profundidad, pero **ya no lo ejerce ningún test** — conviene saberlo antes de "limpiarlo" por parecer código muerto. |

**GET (1)** — `GetById_UnknownId_Returns404`.

**Lo que esta suite NO afirma:** que el mensaje se publicara. Eso necesita el harness en memoria de MassTransit
y es `3.7`. Lo que sí demuestra es lo que un broker real puede demostrar — que el `Publish` no lanza y no
bloquea. Que el mensaje sale, y con qué forma, se comprueba en el broker (sección *Verificación* de
[docs/fase_3_3.md](../docs/fase_3_3.md)).

---

## Pasos para ejecutar

### Requisitos previos

1. **Docker Desktop corriendo.** Los 10 tests son `Category=Docker`; sin demonio, la suite falla al arrancar el
   fixture. Testcontainers levanta su propio SQL Server en un puerto aleatorio, precisamente para no chocar con
   el `sqlserver` del compose que ya ocupa el 1433.
2. **`docker compose up -d`** — desde `3.3`, por el RabbitMQ. Ver el aviso de arriba.
3. **La imagen `mcr.microsoft.com/mssql/server:2022-latest`.** Es la misma etiqueta que `docker-compose.yml`,
   así que normalmente ya está descargada; la primera vez son ~1,5 GB.

### Ejecutar

```powershell
# 1. Compilar
dotnet build tests/Services/Orders/Orders.Tests

# 2. Ejecutar — el proyecto es su propio ejecutable (regla 5b de CLAUDE.md).
#    10 tests, ~45 s.
tests\Services\Orders\Orders.Tests\bin\Debug\net10.0\Orders.Tests.exe

# 3. Filtrar. Ojo: la opción es `-trait` / `-class`, con UN guion.
tests\Services\Orders\Orders.Tests\bin\Debug\net10.0\Orders.Tests.exe -class "Orders.Tests.CreateOrderTests"
tests\Services\Orders\Orders.Tests\bin\Debug\net10.0\Orders.Tests.exe -trait "Category=Docker"

# 4. Ante un fallo, redirigir a fichero en vez de leer la cola de la consola:
#    EF Core registra a nivel info y el detalle se pierde de vista.
tests\Services\Orders\Orders.Tests\bin\Debug\net10.0\Orders.Tests.exe > orders-tests.log 2>&1
```

### Tres avisos que cuestan tiempo

- **`dotnet test` está roto desde que el SDK pasó a 10.0.400.** Reporta `Zero tests ran / error: 1` en ~150 ms.
  No es un problema de este proyecto: pasa igual con `Shop133.ArchitectureTests`, que no necesita Docker. El
  rodeo es el `.exe` de arriba, y queda pendiente de arreglar antes de `8.3`.
  Y cuidado con el filtro: `--filter-trait` es una opción de `dotnet test`, no del ejecutable — pasársela al
  `.exe` da `error: unknown option`.
- **Smart App Control puede tumbar la ejecución** con
  `An Application Control policy has blocked this file. (0x800711C7)` nombrando una DLL de Testcontainers
  (`Docker.DotNet.Handler.Abstractions.dll`). El remedio documentado es **volver a lanzarlo**: el bloqueo suele
  ser transitorio mientras Windows consulta el Intelligent Security Graph. **En `3.3` no bastó** — doce
  reintentos a lo largo de ~10 minutos y las dos suites (`Orders.Tests` **y** `Catalog.Tests`, que ese punto no
  tocó) siguieron cayendo enteras al construir el fixture. Los tests se *descubren* bien; ninguno llega a
  ejecutar su cuerpo. Nunca degrades el paquete persiguiéndolo (el bloqueo se mueve al ensamblado que sea
  nuevo) ni desactives Smart App Control: **no se puede volver a activar sin reinstalar Windows**.
- **Si Docker Desktop acaba de arrancar**, Testcontainers puede fallar con `NpipeEndpointAuthenticationProvider`
  aunque `docker info` ya responda: el pipe `\\.\pipe\docker_engine` tarda un poco más en aparecer que el
  demonio. Espera y reintenta.

---

## Añadir un test nuevo

- La clase lleva `[Collection(OrdersApiCollection.Name)]` y `[Trait("Category", "Docker")]` — el trait va en la
  clase, nunca en cada método.
- Nombre del método: `Method_Scenario_ExpectedResult`. Identificadores en inglés; la prosa de los `<summary>`,
  en español.
- Aserciones con el `Assert` de xUnit. **Nada de FluentAssertions**: la 8.x pasó a licencia comercial.
- Un `.cs` nuevo **no necesita tocar el `.csproj`** — el SDK lo incluye por glob implícito. Si Visual Studio no
  lo muestra, lo que está desactualizado es su caché, no el proyecto.
