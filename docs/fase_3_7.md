# Fase 3.7 — Tests de consumers con el harness en memoria de MassTransit

**Fecha:** 2026-08-31 · **Estado:** completado · **Roadmap:** [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md)

---

## Objetivo

Cerrar la Fase 3 automatizando lo que `3.4`, `3.5` y `3.6` entregaron verificado **a mano**. Los tres
documentos terminan con la misma frase:

> **No hay tests automatizados de nada de esto.** Todo lo de arriba se verificó a mano contra un broker y una
> base reales; automatizarlo con el harness en memoria es `3.7`.

El punto pide tres cosas:

1. Que Inventory y Payments **publiquen el evento correcto ante cada entrada**.
2. El **test de idempotencia** — mismo `MessageId` dos veces, un solo efecto. El roadmap lo llama *"la única
   verificación fiable de 3.6"*, y con razón: a mano hay que republicar el mensaje y comparar estado.
3. Quitarle a `Orders.Tests` **la dependencia del broker real** que estrenó `3.3`.

Y vencen aquí dos deudas apuntadas por escrito: la extracción de `SqlServerContainerFixture` —aplazada en
`2.4` y citada por `3.4`, `3.5` y `3.6` como "se decide en 3.7, con cuatro copias delante"— y la afirmación,
pendiente desde `3.3`, de que `POST /orders` **publica** `OrderCreated`.

**Fuera de alcance deliberadamente:**

| Queda fuera | Entra en |
|---|---|
| Topología real de exchanges y colas contra un RabbitMQ de verdad | `8.2` |
| Los 4 escenarios obligatorios contra `OrderStateMachine` | `4.7` |
| Concurrencia optimista sobre `StockItem` | Sin dueño — ver el hallazgo 2 más abajo, que lo reprodujo |
| Que el `Program.cs` de Inventory/Payments registre de verdad su consumer | `8.2` — ver decisión 3 |
| El agujero de precios (un producto real pedido a `0.01` se cobra un céntimo) | `4.8`/`4.9` |

---

## Decisiones

### 1. `SqlServerContainerFixture` se extrae a `tests/Shop133.TestUtilities`

La pregunta lleva abierta desde `1.7` y se aplazó formalmente en `2.4` con un argumento que era bueno
entonces: *"dos ocurrencias no son un patrón"*. El comentario que quedó en el propio fichero fijaba la cita:

> La evidencia llega en 3.7, cuando Inventory.Tests y Payments.Tests pidan lo mismo: entonces serán cuatro
> copias y la extracción se decidirá con datos.

Los datos: `diff -u` entre las dos copias existentes devuelve **diferencias únicamente en la línea del
`namespace` y en la prosa de los comentarios**. Ni una línea de código ejecutable había divergido en cinco
puntos del roadmap. Con cuatro copias, un arreglo aquí —el `SINGLE_USER WITH ROLLBACK IMMEDIATE` que evita
el error 3702, la etiqueta de imagen que reutiliza la del compose— habría que aplicarlo cuatro veces sin que
nada avisara de la que se olvidase.

**Descartado seguir copiando.** Era defendible con dos y deja de serlo con cuatro.

**Descartado subir también las factories.** `CatalogApiFactory`, `OrdersApiFactory`,
`InventoryConsumerHost` y `PaymentsConsumerHost` **sí** divergen —una base por clase frente a una por test,
`WebApplicationFactory` frente a un `ServiceCollection` desnudo, claves de configuración distintas— y
compartir lo que diverge es exactamente cómo un proyecto de utilidades se convierte en un cajón de sastre
del que ya nadie puede quitar nada. La regla de contención está escrita en el `.csproj`: **aquí solo entra
lo que las cuatro suites usan igual.**

El proyecto es una **biblioteca**, no un ejecutable de test: no lleva `<OutputType>Exe</OutputType>`, y por
eso su referencia a xUnit es `xunit.v3.extensibility.core` y no el metapaquete `xunit.v3`, que inyectaría un
`Main`.

### 2. `MassTransit.TestFramework` no hace falta — y no se añade

El roadmap y tres documentos anteriores daban por hecho que este punto añadiría ese paquete, con la
advertencia de fijarlo a **8.5.10** porque `MassTransitPackages_StayOnMajorVersion8` solo escanea `src/` y no
vigilaría el pin.

Al ir a añadirlo resultó innecesario: `AddMassTransitTestHarness` e `ITestHarness` viven en el paquete
**`MassTransit`** de siempre —en `MassTransit.DependencyInjectionTestingExtensions` y en el namespace
`MassTransit.Testing`—, comprobado leyendo el `MassTransit.xml` de 8.5.10 antes de instalar nada. Los
proyectos de test lo ven por la vía transitiva de `<Svc>.API → MassTransit.RabbitMQ → MassTransit`.

El efecto secundario es el mejor de los dos: **no queda ningún `PackageReference` de MassTransit bajo
`tests/`**, así que la advertencia sobre el pin sin vigilancia se queda sin objeto. Al llegar por
transitividad, la versión del harness es *por construcción* la misma que usa el servicio.

`MassTransit.TestFramework` es otra cosa —las clases base `InMemoryTestFixture` del estilo antiguo, anterior
a la integración con el contenedor de dependencias— y no aporta nada aquí.

### 3. Inventory.Tests y Payments.Tests **no** usan `WebApplicationFactory`

Las otras dos suites levantan su servicio entero porque prueban endpoints HTTP. **Inventory.API y
Payments.API no tienen ni uno.** Lo que se prueba aquí es un `IConsumer<T>`, así que el host de test le
construye el contenedor de dependencias que necesita —el `DbContext` real y el bus en memoria— y nada más.

**Descartado `WebApplicationFactory<Program>`.** Habría exigido añadir a los dos servicios un
`public partial class Program { }` —la pregunta que `3.4` y `3.5` dejaron abierta para este punto, y cuya
respuesta es **no**—, el paquete `Microsoft.AspNetCore.Mvc.Testing`, y desmontar el bus de RabbitMQ que sus
`Program.cs` registran, todo para arrancar un servidor web al que no se le iba a hacer una sola petición.

**El precio, dicho en voz alta y no escondido:** nada en estas dos suites comprueba que el `Program.cs` del
servicio registre de verdad su consumer con `AddConsumer<…>()`. Si alguien borrara esa línea, los 18 tests
seguirían en verde y el servicio dejaría de consumir en silencio — que es justo el modo de fallo que `3.1`
pre-empeñó dejando `ConfigureEndpoints` puesto con cero consumers. Ese hueco es de **`8.2`**, que prueba la
topología real contra un RabbitMQ de verdad.

**Descartado también instanciar el consumer a pelo** y llamarle a `Consume` con un doble de
`ConsumeContext`. El harness es lo que hace observable lo único que distingue un duplicado descartado de uno
reprocesado: **cuántos eventos salieron**. Ver la decisión 5.

### 4. El bus de RabbitMQ de Orders.API se desmonta desde el test, sin tocar `src/`

`OrdersApiFactory` quita en `ConfigureTestServices` todos los `ServiceDescriptor` cuyo tipo de servicio o de
implementación vive en un ensamblado `MassTransit*`, y registra encima `AddMassTransitTestHarness()`.

Hay que **desmontar** en vez de sustituir porque no se llega antes: `Program.cs` lee su guarda y llama a
`AddMassTransit` *antes* de `app.Build()`, y `ConfigureTestServices` corre después. Es literalmente lo que
el comentario de `OrdersApiFactory` anticipaba en `3.3` al descartar hacerlo entonces — *"obligaría a
desmontar el bus que Program.cs ya registró, bastante más que una línea"*.

Se filtra por **ensamblado** y no por una lista de tipos concretos porque `AddMassTransit` registra decenas
y la lista quedaría desfasada en la siguiente versión menor.

**Descartado un interruptor de transporte en `Program.cs`** (elegir `UsingInMemory` o `UsingRabbitMq` según
una clave de configuración). Sería más robusto que este filtro, pero mete en producción código que existe
solo para los tests y, peor, deja al servicio poder arrancar **sin hablar con el broker** sin que nada
avise. Lo que hace aceptable la fragilidad del filtro es que su rotura no es silenciosa: sin bus registrado
el host no arranca, y con el de RabbitMQ todavía puesto los tests se cuelgan contra el URI inventado.

**Descartado `Testcontainers.RabbitMq`**, que ya se había descartado en `2.4`: haría la suite autónoma a
cambio de un paquete más y ~10 s de arranque por ensamblado, y seguiría sin permitir afirmar nada sobre el
mensaje publicado.

El `UseSetting` de `ConnectionStrings:RabbitMq` **se queda** —la guarda de `Program.cs` sigue ahí— pero su
valor pasa a ser deliberadamente falso, `amqp://el-harness-sustituye-esto:5672`. Un URI verosímil sería
peor: si el desmontaje se rompiera algún día, la suite se conectaría al RabbitMQ de desarrollo sin que nada
lo delatara. Nótese el viaje de esta clave: decorativa en `3.1`, dependencia real en `3.3`, decorativa otra
vez en `3.7`.

### 5. Los asserts miran los eventos publicados, no solo la base

Es la conclusión que `3.6` dejó medida y la que decide la forma de toda la suite: cuando un duplicado se
descarta, **el estado de la base queda idéntico** a si se hubiera reprocesado. La única diferencia
observable es cuántos eventos salieron. Un test que solo consultara `StockItems` o `Payments` pasaría en
verde con la idempotencia rota — que es exactamente por qué `3.6` tuvo que montar una cola espía a mano.

### 6. Ninguna regla de arquitectura nueva. La suite se queda en 15

`1.7` y `2.4` dejaron abierta la pregunta de una regla sobre proyectos de test, citando este punto como el
momento de replantearla ("cuando dos servicios tengan tests"; ahora son cuatro). Se ha mirado y **no se
añade**, con el precedente explícito de `3.3` y `3.5`: se explica por qué en el documento en vez de inventar
un filtro que no case con nada.

El motivo es que no hay una regla que romper a propósito. `ProjectGraph` enumera solo `<repo>/src`, así que
las reglas existentes no ven `tests/` — y eso es lo que permite que los proyectos de test referencien EF
Core transitivamente sin incumplir `EfCorePackages_LiveOnlyIn_InfrastructureProjects`. Las convenciones que
podrían codificarse aquí (que cada suite lleve `<OutputType>Exe</OutputType>`, que ninguna añada
`Microsoft.NET.Test.Sdk`) son propiedades del **runner**, no de la arquitectura del sistema: si se
incumplen, los tests no ejecutan y se ve al instante. Una regla que solo detecta lo que ya falla ruidosamente
no gana nada.

Si algún día se amplía el escaneo a `tests/`, la forma es una exención `IsTest` en `ProjectGraph`, **nunca**
dejar `tests/` fuera — como ya avisaba `2.4`.

---

## Cambios

### Nuevos — `tests/Shop133.TestUtilities`

| Archivo | Rol |
|---|---|
| [Shop133.TestUtilities.csproj](../tests/Shop133.TestUtilities/Shop133.TestUtilities.csproj) | **Nuevo.** Biblioteca, sin `OutputType`. Lleva la regla de contención escrita en un comentario. |
| [SqlServerContainerFixture.cs](../tests/Shop133.TestUtilities/SqlServerContainerFixture.cs) | **Nuevo.** Traslado literal del cuerpo que vivía duplicado; el comentario cuenta ahora la decisión de extraer. |

### Nuevos — `tests/Services/Inventory/Inventory.Tests` (9 tests)

| Archivo | Rol |
|---|---|
| [Inventory.Tests.csproj](../tests/Services/Inventory/Inventory.Tests/Inventory.Tests.csproj) | **Nuevo.** Sin `Mvc.Testing` y sin `MassTransit.TestFramework`, con el porqué de las dos ausencias. |
| [Infrastructure/InventoryConsumerCollection.cs](../tests/Services/Inventory/Inventory.Tests/Infrastructure/InventoryConsumerCollection.cs) | **Nuevo.** Un contenedor por ensamblado, ejecución en serie. |
| [Infrastructure/InventoryConsumerHost.cs](../tests/Services/Inventory/Inventory.Tests/Infrastructure/InventoryConsumerHost.cs) | **Nuevo.** `ServiceCollection` + `AddMassTransitTestHarness` + `InventoryDbContext`, base por test, helpers de lectura. |
| [OrderCreatedConsumerTests.cs](../tests/Services/Inventory/Inventory.Tests/OrderCreatedConsumerTests.cs) | **Nuevo.** Camino feliz, el reenvío de `Amount`, los tres rechazos, la atomicidad y las dos idempotencias. |

### Nuevos — `tests/Services/Payments/Payments.Tests` (9 tests)

| Archivo | Rol |
|---|---|
| [Payments.Tests.csproj](../tests/Services/Payments/Payments.Tests/Payments.Tests.csproj) | **Nuevo.** |
| [Infrastructure/PaymentsConsumerCollection.cs](../tests/Services/Payments/Payments.Tests/Infrastructure/PaymentsConsumerCollection.cs) | **Nuevo.** |
| [Infrastructure/PaymentsConsumerHost.cs](../tests/Services/Payments/Payments.Tests/Infrastructure/PaymentsConsumerHost.cs) | **Nuevo.** Fija `DeclineAmountAbove` **por código**, no leyendo el `appsettings.json` del servicio. |
| [StockReservedConsumerTests.cs](../tests/Services/Payments/Payments.Tests/StockReservedConsumerTests.cs) | **Nuevo.** Cobro, rechazo, la frontera del umbral, las dos guardas de importe y las dos idempotencias. |

### Modificados

| Archivo | Cambio |
|---|---|
| [tests/Services/Orders/.../OrdersApiFactory.cs](../tests/Services/Orders/Orders.Tests/Infrastructure/OrdersApiFactory.cs) | Desmonta el bus de RabbitMQ y monta el harness; expone `Harness`; el URI del broker pasa a ser falso a propósito. |
| [tests/Services/Orders/Orders.Tests/CreateOrderTests.cs](../tests/Services/Orders/Orders.Tests/CreateOrderTests.cs) | **+2 tests** (10 → 12): que `OrderCreated` se publica con la foto y el total, y que un cuerpo inválido no publica nada. |
| `Catalog.Tests.csproj`, `Orders.Tests.csproj` | `ProjectReference` a `Shop133.TestUtilities`; se borran las dos copias de la fixture; los comentarios que aplazaban la extracción se sustituyen por el resultado. |
| 6 ficheros de `Catalog.Tests` y `Orders.Tests` | `using Shop133.TestUtilities;`. |
| [shop133.slnx](../shop133.slnx) | Tres proyectos nuevos y dos carpetas de solución. |
| [CLAUDE.md](../CLAUDE.md), [tests/README.md](../tests/README.md), [docs/README.md](README.md), [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md) | Estado, recuentos y comandos. |

### Lo que no se tocó

**`src/` no tiene ni un cambio.** Es comprobable con `git diff --stat src/`, y era el objetivo de la
decisión 3 (nada de `public partial class Program { }` en Inventory ni en Payments) y de la 4 (nada de
interruptor de transporte en `Orders.API`). Los dos consumers se rompieron **temporalmente** a propósito
para comprobar que los tests los cazan, y se restauraron; ver la Verificación.

### Paquetes NuGet nuevos

| Paquete | Versión | Licencia | Dónde |
|---|---|---|---|
| `Microsoft.Data.SqlClient` | 6.1.1 | MIT | `Shop133.TestUtilities`. Ya estaba en el grafo por la vía transitiva de `Microsoft.EntityFrameworkCore.SqlServer` 10.0.8; hay que declararlo porque este proyecto no referencia ningún servicio y `Testcontainers.MsSql` **no** trae el cliente, solo sabe levantar el contenedor. |
| `xunit.v3.extensibility.core` | 4.0.0 | Apache-2.0 | `Shop133.TestUtilities`, solo por `IAsyncLifetime`. Subpaquete del árbol que ya restaura `xunit.v3`. |

`xunit.v3` 4.0.0 y `Testcontainers.MsSql` 4.14.0 en las dos suites nuevas son los mismos de `1.7`/`2.4`.
**Ningún paquete de MassTransit** — ver la decisión 2.

---

## Detalles que cuestan tiempo

**`harness.InactivityTask` es UNA SOLA tarea que se completa la primera vez que el bus queda inactivo.** Se
usó como "espera a que el consumer termine" dentro del helper de publicación, y en los tests que publican dos
mensajes el segundo `await` **volvía al instante sin esperar nada**, contando los eventos con el mensaje
todavía en vuelo. Se descubrió porque un test falló pidiendo 2 eventos y viendo 1, pero **lo grave era lo
otro**: los dos tests de idempotencia, que esperan exactamente 1, *pasaban por el motivo equivocado*. Un test
verde que no ha llegado a ejercitar lo que dice es peor que uno rojo. La forma correcta es **una sola espera
por test, después de todas las publicaciones**.

**`harness.Consumed` está indexado por `MessageId`.** Fue el segundo intento de espera —contar mensajes
consumidos hasta alcanzar los publicados— y falla justo en los dos casos que esta suite existe para probar:
dos entregas con el mismo id **colapsan en una sola entrada**, y un mensaje sin `MessageId` no se registra en
absoluto. El síntoma es desconcertante: la espera por recuento funciona con ids distintos y agota el timeout
con ids iguales.

**El transporte en memoria entrega en paralelo, y eso hacía los tests no deterministas.** Dos mensajes del
mismo pedido publicados seguidos pueden procesarse a la vez; entonces ninguno ve la reserva del otro, los dos
intentan el `INSERT` y uno revienta por clave duplicada. El mismo test daba 2 eventos en una ejecución y 1 en
la siguiente. **Eso no es un defecto del test: es exactamente el agujero de concurrencia que `3.6` dejó
anotado sin dueño**, reproducido por accidente. Se fija `cfg.ConcurrentMessageLimit = 1` en los dos hosts
porque lo que estos tests modelan es una **reentrega**, que es secuencial por definición; el agujero se queda
donde estaba, documentado y sin dueño, no tapado.

**Un test de idempotencia que solo cuenta eventos de negocio pasa aunque la guarda esté rota.** Comprobado
rompiendo a propósito la guarda de transporte de los dos consumers: los 18 tests seguían en verde. El motivo
es que hay una segunda red debajo — sin la guarda, la segunda entrega llega a `MarkProcessed` con un
`MessageId` ya escrito y el `INSERT` en `ProcessedMessages` revienta por clave duplicada, así que el consumer
**falla** y tampoco publica. "Un solo evento" no distingue *se descartó limpiamente* de *explotó*. Lo que sí
las distingue es `Assert.Empty(Published<Fault<T>>())`: descartar en silencio no publica ningún fault,
explotar sí. Con ese assert añadido, romper la guarda pone en rojo los 4 tests de idempotencia — que es lo
que se les pide.

**Smart App Control bloqueó `Inventory.Tests.exe` y ninguno de los dos remedios documentados funcionó.**
Ni reintentar (falló dos veces) ni `dotnet build -c Release`, que es lo que resolvió el caso de `3.5`: el
`.exe` de Release quedó bloqueado igual. **Lo que sí funciona es ejecutar el `.dll` con el host de .NET:**
`dotnet tests\...\Inventory.Tests.dll`. El motivo es que lo que Smart App Control rechaza es el *apphost*
—el `.exe` que genera el SDK, sin firma y sin reputación—, mientras que `dotnet.exe` está firmado por
Microsoft y cargar el `.dll` desde él no pasa por esa evaluación. Es un rodeo mejor que los dos anteriores
porque no depende de que Windows cambie de opinión. Dato curioso que confirma que el bloqueo es por fichero
y no por proyecto: `Payments.Tests.exe`, creado media hora después, arranca sin problema mientras
`Inventory.Tests.exe` sigue bloqueado.

**`ITestHarness` está en `MassTransit.Testing`; `AddMassTransitTestHarness` en `MassTransit` a secas.** Hacen
falta los dos `using` y el compilador solo se queja del segundo. Mismo tropiezo que `3.2` anotó con
`SystemTextJsonMessageSerializer`, que vive en `MassTransit.Serialization`.

**`--filter-class` no es una opción del ejecutable de test.** Es de `dotnet test`. Pasársela al `.exe` o al
`dotnet <dll>` da un `exit 3` sin mensaje claro. La del runner es `-class`, con un guion — ya estaba
apuntado para `-trait` y vale igual aquí.

---

## Verificación

Ejecutado el 2026-08-31 contra Docker Desktop 29.6.2. Salidas reales.

### 1. La extracción de la fixture no cambió nada

`Catalog.Tests` es el control: no se le tocó un solo test, solo de dónde viene la fixture.

```
   Catalog.Tests  Total: 19, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 84.901s
```

### 2. Build limpio de la solución entera, con los tres proyectos nuevos

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### 3. Las cinco suites

```
   Shop133.ArchitectureTests  Total: 15, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.183s
   Catalog.Tests              Total: 19, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 74.834s
   Orders.Tests               Total: 12, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 52.969s
   Inventory.Tests            Total:  9, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 62.094s
   Payments.Tests             Total:  9, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 62.817s
```

**64 tests**: 15 `Fast` + 49 `Docker`.

### 4. La prueba de que el broker real se fue

`Orders.Tests` con RabbitMQ **parado**. Antes de este punto, `POST /orders` se quedaba colgado hasta que el
test expiraba, porque un `Publish` sobre el transporte de RabbitMQ espera a que haya conexión en vez de
fallar rápido.

```
> docker compose stop rabbitmq
 Container shop133-rabbitmq Stopped

   Orders.Tests  Total: 12, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 60.602s
```

### 5. Los tests de idempotencia se rompieron a propósito

Con `if (alreadyProcessed && false)` en cada consumer —la guarda de transporte de `3.6` desactivada— y el
assert del fault ya puesto:

```
Inventory.Tests.OrderCreatedConsumerTests.Consume_SameMessageIdTwice_OnTheRejectionPath_PublishesASingleStockRejected [FAIL]
      Assert.Empty() Failure: Collection was not empty
Inventory.Tests.OrderCreatedConsumerTests.Consume_SameMessageIdTwice_PublishesASingleStockReserved [FAIL]
      Assert.Empty() Failure: Collection was not empty
   Inventory.Tests  Total: 9, Errors: 0, Failed: 2

Payments.Tests.StockReservedConsumerTests.Consume_SameMessageIdTwice_OnTheDeclinePath_PublishesASinglePaymentFailed [FAIL]
      Assert.Empty() Failure: Collection was not empty
Payments.Tests.StockReservedConsumerTests.Consume_SameMessageIdTwice_PublishesASinglePaymentCompleted [FAIL]
      Assert.Empty() Failure: Collection was not empty
   Payments.Tests  Total: 9, Errors: 0, Failed: 2
```

Sin el assert del fault, esos mismos 4 tests pasaban en verde con la guarda rota — ver *Detalles que cuestan
tiempo*. Los dos consumers quedaron restaurados; `git diff --stat src/` no devuelve nada.

### 6. `src/` intacto

```
> git diff --stat src/
(sin salida)
```

---

## Pendiente

- **`4.7`** — los cuatro escenarios obligatorios contra `OrderStateMachine`. El escenario 4 (evento
  duplicado) tiene aquí su equivalente por consumer; el 3 (compensación) necesita la saga.
- **`4.1`, `4.4`, `4.6`** — cada consumer nuevo tiene que acordarse de la guarda de `3.6` **y** de su test de
  idempotencia. Los dos hosts de este punto son la plantilla; copiar el `Assert.Empty(Published<Fault<T>>())`
  con ellos, que es lo que hace que el test sirva de algo.
- **`8.2`** — la topología real de exchanges y colas, y con ella lo único que estas suites no pueden ver:
  que el `Program.cs` de cada servicio registre de verdad su consumer.
- **Sin dueño** — la carrera de concurrencia sobre `StockItem` y sobre `ProcessedMessages`, que este punto
  reprodujo por accidente y neutralizó con `ConcurrentMessageLimit = 1`. Cubrirla exige antes decidir qué
  debe pasar: hoy el segundo `INSERT` revienta y el mensaje va a la cola de errores.
- **`8.3`** — `dotnet test` sigue roto desde el SDK 10.0.400 (`Zero tests ran / error: 1` en ~150 ms,
  también para el proyecto de arquitectura, que no necesita Docker). Con cinco suites, el rodeo de ejecutar
  cada una a mano empieza a pesar.
