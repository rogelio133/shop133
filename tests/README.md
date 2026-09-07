# Tests de shop133 — qué cubre cada suite y cómo ejecutarlas

Guía operativa de los cinco proyectos de `tests/`. El *porqué* de cada decisión está en los documentos de
[docs/](../docs); esto es lo otro: **qué se está probando y qué hay que teclear para correrlo**.

> Hasta `3.7` este fichero hablaba solo de `Orders.Tests`. Se amplió al cerrar la Fase 3, cuando llegaron
> `Inventory.Tests` y `Payments.Tests` y los avisos comunes empezaron a repetirse.

---

## El mapa

| Proyecto | Tests | Categoría | Qué prueba |
|---|---|---|---|
| [`Shop133.ArchitectureTests`](Shop133.ArchitectureTests) | 16 | `Fast` | Las reglas de [CLAUDE.md](../CLAUDE.md) en forma ejecutable, leyendo los `.csproj` de `src/`. |
| [`Shop133.TestUtilities`](Shop133.TestUtilities) | — | — | **No es una suite.** La biblioteca con `SqlServerContainerFixture`, que comparten las cuatro de abajo. |
| [`Services/Catalog/Catalog.Tests`](Services/Catalog/Catalog.Tests) | 19 | `Docker` | Los endpoints CRUD de `1.3`/`1.4` sobre SQL Server real. |
| [`Services/Orders/Orders.Tests`](Services/Orders/Orders.Tests) | 25 | `Docker` **y** `Fast` | `POST /orders` y que se publica `OrderCreated` (12, `Docker`) · los cuatro escenarios de la saga (9, **`Fast`**) · la persistencia de la saga en `OrdersDb` (4, `Docker`). |
| [`Services/Inventory/Inventory.Tests`](Services/Inventory/Inventory.Tests) | 15 | `Docker` | `OrderCreatedConsumer` (reserva, rechazos, atomicidad, idempotencia) y `ReleaseStockConsumer` (la compensación de `4.4`). |
| [`Services/Payments/Payments.Tests`](Services/Payments/Payments.Tests) | 9 | `Docker` | `StockReservedConsumer`: cobro, rechazo por importe e idempotencia. |

**84 tests**: 25 `Fast` y 59 `Docker`. El trait va en la clase, nunca en cada método.

`Orders.Tests` es la única suite con las dos categorías, desde `4.7`: `OrderStateMachineTests` prueba un
*proceso* con el repositorio de saga en memoria, así que no necesita base de datos y corre en ~10 s los
nueve. Para el bucle de desarrollo eso es lo que hay que teclear:

```powershell
dotnet tests\Services\Orders\Orders.Tests\bin\Debug\net10.0\Orders.Tests.dll -trait "Category=Fast"
```

---

## ✅ Desde `3.7` NO hace falta RabbitMQ

Esto cambió y es importante, porque durante toda la Fase 3 fue al revés.

Desde `3.3`, `POST /orders` publicaba de verdad contra el broker del compose, y un `Publish` sobre el
transporte de RabbitMQ **espera a que haya conexión en lugar de fallar rápido**: con el broker caído la
petición no daba error, se quedaba colgada hasta que el test expiraba. `docker compose up -d` era
prerrequisito.

Desde `3.7`, `OrdersApiFactory` desmonta el bus de RabbitMQ y monta el harness en memoria, y las dos suites
de consumers nunca lo usaron. **Comprobado con el broker parado: `Orders.Tests` pasaba 12/12** (hoy 25/25).

Lo que sí sigue haciendo falta es **Docker**, para el SQL Server de Testcontainers. El harness quita el
broker, no la base de datos — la regla 1 de la estrategia de tests prohíbe el provider InMemory de EF Core.

**La excepción, desde `4.7`:** los 9 tests de `OrderStateMachineTests` no necesitan **ni broker ni base**.
Prueban las transiciones de la saga con `InMemoryRepository()`, y eso no viola la regla 1 — lo que esa regla
prohíbe es fingir una base de datos relacional con el provider InMemory de EF Core, no probar un proceso que
no tiene ninguna. Los que sí tocan `OrdersDb.OrderStates` (`OrderStatePersistenceTests`) son `Docker` como
todos los demás.

---

## Cómo están montadas

Las cuatro suites de servicio comparten forma:

| Pieza | Papel |
|---|---|
| `SqlServerContainerFixture` (en `Shop133.TestUtilities`) | Un contenedor `mcr.microsoft.com/mssql/server:2022-latest` por ensamblado. Expone `CreateDatabaseAsync` / `DropDatabaseAsync` / `ConnectionStringFor`. Puerto aleatorio a propósito: el 1433 del host ya lo ocupa el `sqlserver` de docker-compose. |
| `<Algo>Collection.cs` | El `[CollectionDefinition]` que cuelga todas las clases del mismo fixture ⇒ **un solo contenedor** y ejecución en serie (xUnit paraleliza entre *collections*, nunca dentro de una). |
| `<Servicio>ApiFactory` / `<Servicio>ConsumerHost` | Lo que cambia entre suites. Crea la base `<Servicio>Tests_NNN`, le aplica las migraciones y monta el host. |

**Una base de datos por test, no por clase.** xUnit construye la clase de test una vez por método y la
fábrica es un campo de instancia, así que cada test estrena la suya. Eso explica los ~2-3 s por test
(`CREATE DATABASE` + migraciones + `DROP`) y, sobre todo, que se pueda afirmar *"no hay ningún pedido"* o
*"no hay ningún cobro"* sin cualificarlo.

Dos diferencias que conviene tener presentes:

- **Catalog y Inventory tienen seed; Orders y Payments no.** En los dos primeros, `MigrateAsync()` **es** el
  seed —los 50 productos viven dentro de `SeedSouvenirCatalog` y las 50 filas de stock dentro de
  `SeedStockItems`—, así que hay datos de partida. En `Catalog.Tests` eso trae una disciplina: **ningún test
  modifica ni borra una fila sembrada**, los que escriben crean su propio producto.
- **Catalog y Orders levantan el servicio con `WebApplicationFactory`; Inventory y Payments no.** Estos dos
  no tienen un solo endpoint HTTP: lo que se prueba es un `IConsumer<T>`, así que el host de test le monta el
  contenedor de dependencias que necesita y nada más. Ver la decisión 3 de
  [docs/fase_3_7.md](../docs/fase_3_7.md).

**Cada guarda nueva en un `Program.cs` es una línea nueva en su fábrica de tests, y cada guarda que se va se
lleva la suya.** `Program.cs` lee sus claves y lanza *antes* de `app.Build()`, así que sustituir servicios en
`ConfigureTestServices` llega tarde: el host ni se construye. Nada más que estas suites detecta el desajuste.

---

## Pasos para ejecutar

### Requisitos previos

1. **Docker Desktop corriendo.** Las 59 pruebas `Docker` levantan su propio SQL Server en un puerto
   aleatorio. Las 25 `Fast` no lo necesitan.
2. **La imagen `mcr.microsoft.com/mssql/server:2022-latest`.** Es la misma etiqueta que
   `docker-compose.yml`, así que normalmente ya está descargada; la primera vez son ~1,5 GB.
3. RabbitMQ **no** hace falta. Ver el aviso de arriba.

### Ejecutar

```powershell
# 1. Compilar
dotnet build

# 2. Ejecutar. Cada proyecto de test es su propio ejecutable (regla 5b de CLAUDE.md),
#    pero conviene lanzarlo por el .dll — ver el primer aviso de abajo.
dotnet tests\Shop133.ArchitectureTests\bin\Debug\net10.0\Shop133.ArchitectureTests.dll   # 16, sin Docker
dotnet tests\Services\Catalog\Catalog.Tests\bin\Debug\net10.0\Catalog.Tests.dll          # 19, ~80 s
dotnet tests\Services\Orders\Orders.Tests\bin\Debug\net10.0\Orders.Tests.dll             # 25, ~73 s
dotnet tests\Services\Inventory\Inventory.Tests\bin\Debug\net10.0\Inventory.Tests.dll    # 15, ~101 s
dotnet tests\Services\Payments\Payments.Tests\bin\Debug\net10.0\Payments.Tests.dll       #  9, ~61 s

# 3. Filtrar. Ojo: la opción es `-trait` / `-class`, con UN guion.
#    `--filter-trait` y `--filter-class` son de `dotnet test` y dan "unknown option"
#    o un exit 3 sin mensaje.
dotnet tests\Services\Orders\Orders.Tests\bin\Debug\net10.0\Orders.Tests.dll -class "Orders.Tests.CreateOrderTests"
dotnet tests\Shop133.ArchitectureTests\bin\Debug\net10.0\Shop133.ArchitectureTests.dll -trait "Category=Fast"

#    El bucle de desarrollo desde 4.7: los 9 tests de la saga, sin Docker, en ~10 s.
dotnet tests\Services\Orders\Orders.Tests\bin\Debug\net10.0\Orders.Tests.dll -trait "Category=Fast"

# 4. Ante un fallo, redirigir a fichero en vez de leer la cola de la consola:
#    EF Core registra a nivel info y el detalle se pierde de vista.
dotnet tests\Services\Inventory\Inventory.Tests\bin\Debug\net10.0\Inventory.Tests.dll > inv.log 2>&1
```

### Tres avisos que cuestan tiempo

- **Smart App Control puede bloquear el `.exe`, y el rodeo bueno es el `.dll`.** El síntoma es
  `An Application Control policy has blocked this file (0x800711C7)` o *"failed to run"*. Lo que Windows
  rechaza es el **apphost** —el `.exe` que genera el SDK, sin firma y sin reputación—, así que ejecutar
  `dotnet <ruta>.dll` lo esquiva del todo: `dotnet.exe` está firmado por Microsoft. Es mejor que los dos
  remedios que había apuntados (reintentar, o `dotnet build -c Release`): en `3.7` **ninguno de los dos
  funcionó** con `Inventory.Tests.exe`, que sigue bloqueado mientras `Payments.Tests.exe`, creado media hora
  después, arranca sin problema. Nunca desactives Smart App Control: **no se puede volver a activar sin
  reinstalar Windows**.
- **`dotnet test` está roto desde que el SDK pasó a 10.0.400.** Reporta `Zero tests ran / error: 1` en
  ~150 ms. No es un problema de este proyecto: pasa igual con `Shop133.ArchitectureTests`, que no necesita
  Docker. Queda pendiente de arreglar antes de `8.3`.
- **Si Docker Desktop acaba de arrancar**, Testcontainers puede fallar con
  `NpipeEndpointAuthenticationProvider` aunque `docker info` ya responda: el pipe
  `\\.\pipe\docker_engine` tarda un poco más en aparecer que el demonio. Espera y reintenta.

---

## Añadir un test nuevo

- La clase lleva su `[Collection(...)]` y `[Trait("Category", "Docker")]` — el trait va en la clase, nunca
  en cada método.
- Nombre del método: `Method_Scenario_ExpectedResult`. Identificadores en inglés; la prosa de los
  `<summary>`, en español.
- Aserciones con el `Assert` de xUnit. **Nada de FluentAssertions**: la 8.x pasó a licencia comercial.
- Un `.cs` nuevo **no necesita tocar el `.csproj`** — el SDK lo incluye por glob implícito. Si Visual Studio
  no lo muestra, lo que está desactualizado es su caché, no el proyecto.

### Si el test es de un consumer

Tres cosas que `3.7` aprendió por las malas y que ahorran una tarde:

- **Espera una sola vez por test, al final.** `harness.InactivityTask` es *una sola tarea* que se completa
  la primera vez que el bus queda inactivo; a partir de ahí cualquier `await` posterior vuelve al instante.
  Meterla en un helper de publicación hace que en los tests de dos mensajes el segundo `await` no espere
  nada, y el test cuenta los eventos a mitad de camino.
- **No cuentes `harness.Consumed` para esperar.** Está indexado por `MessageId`: dos entregas del mismo id
  colapsan en una entrada y un mensaje sin id no se registra — justo los casos que un test de idempotencia
  necesita.
- **Un test de idempotencia necesita `Assert.Empty(Published<Fault<T>>())`.** Sin él pasa igual con la
  guarda borrada, porque entonces el duplicado no se descarta: revienta en el `INSERT` de
  `ProcessedMessages` por clave duplicada y tampoco publica. Contar solo eventos de negocio no distingue
  *se descartó en silencio* de *explotó*.
