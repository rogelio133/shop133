# Fase 3.1 — Instalar MassTransit + RabbitMQ transport en Orders, Inventory y Payments

**Fecha:** 2026-08-25 · **Estado:** completado · **Roadmap:** [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md), punto 3.1

---

## Objetivo

Dejar conectados al broker los tres servicios que van a participar en la saga —Orders, Inventory y Payments— **antes** de que exista ni un publisher ni un consumer. Es el punto que pone el cable: el paquete, la configuración del host, y la guarda que hace que un valor ausente falle diciendo qué falta.

La infraestructura ya estaba desde `0.2`: `docker-compose.yml` levanta `rabbitmq:4-management-alpine` con healthcheck y el override publica `5672`/`15672`. Los 9 mensajes viven en `Shop133.Contracts` desde `0.3`. Lo único que faltaba era el lado .NET.

El punto tiene además un valor pedagógico concreto, y es el que justifica que sea un item propio en vez de un trozo de `3.3`: **con el broker parado, los tres servicios arrancan igualmente**. Es exactamente lo contrario del `502` que devuelve `POST /orders` con Catalog caído desde `2.3`. Ese contraste está medido abajo.

**Fuera de alcance deliberadamente:**

| Queda fuera | Entra en |
|---|---|
| Publicar `OrderCreated` y borrar `Orders.Infrastructure/Catalog/` | `3.3` |
| Cualquier `IConsumer<T>` y la carpeta `Consumers/` | `3.4` / `3.5` |
| Idempotencia (`MessageId` procesados) | `3.6` |
| Retry, redelivery, error queues | `3.4`–`3.6`, con el consumer delante |
| Tests con `MassTransit.TestFramework` | `3.7` |
| `Notifications.API` — el roadmap nombra tres servicios | `4.6` |
| Persistencia de la saga (`MassTransit.EntityFrameworkCore`) | `4.5` |
| Dockerfiles y entradas en compose para los tres servicios | Sin asignar; hoy corren desde el IDE |
| `/health` con el health check del bus | `8.4` |

---

## Decisiones

### 1. `MassTransit.RabbitMQ` 8.5.10, con la versión fijada en el `.csproj`

8.5.10 es el último 8.x publicado. Lo relevante no es el número, es que **la v9 ya está en nuget.org** (9.2.0 el día de escribir esto) y tiene licencia comercial:

```
dotnet package search MassTransit.RabbitMQ --exact-match
  … 8.5.9, 8.5.10, 9.0.0, 9.0.1, 9.1.0, 9.1.1, 9.1.2, 9.2.0
```

O sea que `dotnet add package MassTransit.RabbitMQ` **sin fijar la versión instala hoy la de pago**, sin avisar de nada. La advertencia lleva en CLAUDE.md desde la Fase 0; lo que faltaba era que costara algo saltársela — de ahí la decisión 4.

**Descartado — dejar que el comando resuelva la última.** Es precisamente el modo de fallo del que avisa el proyecto. Misma trampa que FluentAssertions 8.x, que es la razón de que las aserciones de este repositorio sean las de xUnit.

### 2. Se declara solo el transport, no el core

`MassTransit.RabbitMQ` arrastra `MassTransit` y `MassTransit.Abstractions`. Mismo criterio, y por el mismo motivo, que el `Microsoft.EntityFrameworkCore.SqlServer` de `Orders.Infrastructure`, que no declara aparte `EntityFrameworkCore` ni `.Relational`.

Resuelto del `project.assets.json`:

```
MassTransit/8.5.10
MassTransit.Abstractions/8.5.10
MassTransit.RabbitMQ/8.5.10
RabbitMQ.Client/7.2.1
```

**`RabbitMQ.Client` 7.2.1 es un dato que conviene tener anotado**: es el cliente async nuevo, no el 6.x. Era la duda que quedaba con un broker RabbitMQ **4.x**, que eliminó el *global QoS*. No dio ningún problema — ni `PRECONDITION_FAILED` ni cierres de canal—, pero si algún día aparece uno al arrancar, el sitio donde mirar es ese par de versiones.

### 3. El URI del broker en una sola clave, `ConnectionStrings:RabbitMq`, en User Secrets

Un único `amqp://guest:guest@localhost:5672`, leído con `GetConnectionString("RabbitMq")` y protegido con la misma guarda que `ConnectionStrings:OrdersDb` en `2.2`.

Va a User Secrets y no a `appsettings.json` porque lleva usuario y contraseña. Es la decisión opuesta a la de `Services:CatalogBaseUrl` en `2.3`, y por el criterio de siempre: **lo que lleva credencial va a secretos, lo que no, al archivo versionado.**

`cfg.Host(new Uri(...))` saca usuario y contraseña del *userinfo* del URI, así que no hacen falta `h.Username()`/`h.Password()`.

**Descartado — una sección `RabbitMq` con `Host`, `Username` y `Password` separados.** Tendría la ventaja de dejar el host (que no es secreto) en `appsettings.json`, pero son tres claves, tres guardas y tres sitios que pueden desincronizarse, a cambio de publicar un dato que no le sirve a nadie sin las credenciales.

**La guarda no es decorativa, y aquí menos que en `2.2`.** Sin ella la clave ausente no falla al registrar el bus: falla al **arrancarlo**, dentro de un hosted service, en un mensaje que no menciona la configuración. Poniéndola, revienta antes de `app.Build()` y dice qué comando ejecutar.

`Inventory.API` y `Payments.API` no tenían `UserSecretsId` — lo crea `dotnet user-secrets init`, que es lo que añadió esa propiedad a sus dos `.csproj`.

### 4. La regla de la versión se convierte en test: `MassTransitPackages_StayOnMajorVersion8`

CLAUDE.md dice que al añadir una regla de arquitectura hay que plantearse si `Shop133.ArchitectureTests` puede hacerla cumplir, porque *una regla que solo vive en prosa se rompe en silencio*. Con la v9 ya publicada y a un comando de distancia, esta es exactamente esa clase de regla.

Recorre los `.csproj` de `src/`, filtra los paquetes cuyo id empieza por `MassTransit` y exige que su versión empiece por `8.`. La suite pasa de **12 a 13 tests**.

**Va en un archivo nuevo, `PackageRulesTests.cs`, y no en `LayeringRulesTests`.** Ahí ya hay una regla que mira paquetes (`EfCorePackages_LiveOnlyIn_InfrastructureProjects`), pero esa afirma la regla 5 —la dirección de las flechas dentro de un servicio— y su comentario de clase lo dice. Una regla sobre qué versión de una dependencia es legítima no tiene nada que ver con las capas; meterla ahí habría convertido el comentario de esa clase en mentira.

**Una versión vacía cuenta como infracción**, a propósito: significaría que alguien dejó la versión al criterio de otro sitio, que es justo lo que la regla impide.

### 5. `SetKebabCaseEndpointNameFormatter()` se fija ahora, con cero consumers

Parece prematuro y es lo contrario. El formatter decide **el nombre de la cola de cada consumer**, así que cambiarlo en `3.4` no sería editar una línea: dejaría colas huérfanas en el broker que nadie vacía. Se decide cuando es gratis.

Kebab en minúsculas porque los nombres de cola de RabbitMQ distinguen mayúsculas, y `order-created` no da lugar a dudas donde `OrderCreated` sí.

### 6. `cfg.ConfigureEndpoints(context)` también, aunque hoy no registre nada

Con cero consumers no hace absolutamente nada. Se deja puesta porque es la línea que `3.4` y `3.5` esperan encontrar: **sin ella, registrar un `IConsumer` no crea su receive endpoint y el mensaje se pierde en silencio.** Es el fallo más caro de diagnosticar de esta fase, y cuesta menos prevenirlo que encontrarlo.

### 7. Tres copias literales del bloque, sin factorizar

El bloque `AddMassTransit` es idéntico en los tres `Program.cs`. No se extrae a un método de extensión compartido: eso exigiría un proyecto que los tres referencien, y `Shop133.Contracts` tiene que quedarse en cero paquetes (regla 4, y hay un test que lo comprueba). Un `Shop133.Messaging` nuevo requeriría permiso y sería una decisión de arquitectura tomada sin evidencia.

Mismo criterio que la copia literal de `SqlServerContainerFixture` en `2.4` y que las constantes de longitud duplicadas en `OrderItem`: **con tres copias todavía no se sabe en qué van a divergir.** `3.4` y `3.5` van a tocar dos de ellas para registrar consumers; ahí se reevalúa con el diff delante.

---

## Cambios

### Modificados

| Archivo | Cambio |
|---|---|
| [Orders.API.csproj](../src/Services/Orders/Orders.API/Orders.API.csproj) | `PackageReference` de `MassTransit.RabbitMQ` 8.5.10 |
| [Inventory.API.csproj](../src/Services/Inventory/Inventory.API/Inventory.API.csproj) | Íd. + `UserSecretsId` nuevo |
| [Payments.API.csproj](../src/Services/Payments/Payments.API/Payments.API.csproj) | Íd. + `UserSecretsId` nuevo |
| [Orders.API/Program.cs](../src/Services/Orders/Orders.API/Program.cs) | `using MassTransit;`, guarda de `ConnectionStrings:RabbitMq` y bloque `AddMassTransit` antes de `app.Build()` |
| [Inventory.API/Program.cs](../src/Services/Inventory/Inventory.API/Program.cs) | Íd. — era la plantilla `webapi` sin tocar |
| [Payments.API/Program.cs](../src/Services/Payments/Payments.API/Program.cs) | Íd. — era la plantilla `webapi` sin tocar |
| [ProjectGraph.cs](../tests/Shop133.ArchitectureTests/ProjectGraph.cs) | `PackageReferences` pasa de `IReadOnlyList<string>` a `IReadOnlyList<PackageReferenceInfo>`: conserva la versión, no solo el id |
| [LayeringRulesTests.cs](../tests/Shop133.ArchitectureTests/LayeringRulesTests.cs) | Ajuste del call site de `IsForbiddenEfCorePackage` al tipo nuevo |
| [ContractsRulesTests.cs](../tests/Shop133.ArchitectureTests/ContractsRulesTests.cs) | Íd. en el `string.Join` del mensaje de fallo |

### Nuevos

| Archivo | Rol |
|---|---|
| [PackageRulesTests.cs](../tests/Shop133.ArchitectureTests/PackageRulesTests.cs) | `MassTransitPackages_StayOnMajorVersion8` — la advertencia de licencia en forma ejecutable |

Y uno más, que no estaba previsto en el plan del punto:

| Archivo | Cambio |
|---|---|
| [OrdersApiFactory.cs](../tests/Services/Orders/Orders.Tests/Infrastructure/OrdersApiFactory.cs) | Tercer `UseSetting`, para `ConnectionStrings:RabbitMq`. Sin él la suite entera falla — ver *Detalles que cuestan tiempo* |

### Paquete nuevo

| Paquete | Versión | Licencia | Dónde |
|---|---|---|---|
| `MassTransit.RabbitMQ` | 8.5.10 | Apache-2.0 | `Orders.API`, `Inventory.API`, `Payments.API` |

### Lo que no se tocó

`Shop133.Contracts` (ni un archivo — sigue en cero paquetes y cero referencias) · `Orders.Domain` (la `OrderStateMachine` es `4.1`; no se añade la dependencia antes del caso de uso) · `Orders.Infrastructure` · `Notifications.API` · `docker-compose.yml` · `docker-compose.override.yml` · `.env.example` (en local el URI va a User Secrets, no hace falta clave nueva) · `Orders.Infrastructure/Catalog/` y su guarda `Services:CatalogBaseUrl`, vivos hasta `3.3`.

---

## Detalles que cuestan tiempo

### Un comentario XML no admite dos guiones seguidos

El comentario del `.csproj` iba a decir literalmente *"sin `--version` instala la de pago"*. Los tres proyectos dejaron de cargar:

```
error MSB4025: The project file could not be loaded. An XML comment cannot
contain '--', and '-' cannot be the last character. Line 31, position 33.
```

Es la regla de XML de toda la vida, pero muerde justo al documentar una opción de línea de comandos, que es cuando más ganas hay de escribirla. El comentario acabó diciéndolo con palabras y explicando por qué no puede nombrar la opción.

### Instalar el transport no declara **ninguna** topología

La expectativa al planificar el punto era ver tres colas temporales `…_bus_…` en el broker —el *bus endpoint* que MassTransit crea— y ningún exchange. **Se midió y no hay ni eso**: con los tres servicios conectados, `/api/queues` y `/api/exchanges` devuelven cero (más allá de los `amq.*` que trae RabbitMQ de fábrica).

MassTransit 8.5 declara la topología **de forma perezosa**: el bus endpoint aparece cuando algo necesita dirección de respuesta o se publica, y los exchanges de `Shop133.Contracts` cuando haya un publish (`3.3`) o un consumer registrado (`3.4`).

Lo que hay que llevarse: **"no veo ninguna cola en la UI" no significa que el bus no esté conectado.** La prueba de que sí lo está es la pestaña *Connections*, no la de *Queues*. Es un buen sitio para perder media hora en `3.3` si no está escrito.

### El bus arranca en un hosted service, así que un broker caído NO tumba el servicio

Es el punto pedagógico del item. Con `docker compose stop rabbitmq`, `dotnet run` de Orders.API:

```
      Now listening on: http://localhost:5189
      Application started. Press Ctrl+C to shut down.
warn: MassTransit[0]
      Connection Failed: rabbitmq://localhost/
       ---> RabbitMQ.Client.Exceptions.ConnectFailureException: Connection failed, host 127.0.0.1:5672
      Retrying 00:00:05.9492261: Broker unreachable: guest@localhost:5672/
      …
      Retrying 00:00:08.5494495: Broker unreachable: guest@localhost:5672/
```

**`warn`, no `fail`; y la aplicación arrancó antes de que el bus lo intentara siquiera.** El servicio sirve HTTP con normalidad — `GET /orders/{id}` de un id inexistente devolvió su `404` de siempre, o sea que la consulta a `OrdersDb` se ejecutó con el broker muerto.

Compárese con `2.3`: con Catalog caído, `POST /orders` devuelve `502` y el pedido **no se crea**. La misma indisponibilidad, dos consecuencias distintas — que es exactamente lo que la Fase 3 viene a cambiar.

Y al levantarlo (`docker compose start rabbitmq`), **se reconectó solo**, sin reiniciar el servicio:

```
      Bus started: rabbitmq://localhost/
```

### El punto abierto de `0.3`: `required` sobrevive al serializador — confirmado

[fase_0_3.md](fase_0_3.md) dejó asignada a este punto la pregunta de si el serializador por defecto de MassTransit 8 respeta los miembros `required` de los records de `Shop133.Contracts` o los rellena en silencio.

Medido con un proyecto de usar y tirar **fuera del repositorio**, que referencia el `Shop133.Contracts` real y deserializa con `MassTransit.Serialization.SystemTextJsonMessageSerializer.Options`:

```
MassTransit  : 8.5.10.0
Serializer   : System.Text.Json
RespectRequired (opción de STJ, false = las valida): False

--- OrderLine completa
    DESERIALIZÓ: OrderLine { ProductId = 7, ProductSku = TAZA-001, ProductName = Taza, Quantity = 2, UnitPrice = 9.50 }

--- OrderLine SIN productSku (required)
    LANZÓ JsonException: JSON deserialization for type 'Shop133.Contracts.OrderLine' was missing required properties including: 'productSku'.

--- OrderCreated SIN customerEmail (required)
    LANZÓ JsonException: JSON deserialization for type 'Shop133.Contracts.Events.OrderCreated' was missing required properties including: 'customerEmail'.
```

**La validación de `required` sobrevive al transporte, y el mensaje de error nombra la propiedad que falta.** Un mensaje incompleto no llega al consumer con `null` dentro: revienta al deserializar. Consecuencia para `3.4`: ese fallo va a aparecer como un mensaje en la *error queue*, no como un `NullReferenceException` en la lógica del consumer.

El nombre de la opción de STJ despista — `RespectRequiredConstructorParameters = False` **no** significa que no valide; se refiere a los parámetros de constructor, no a los miembros `required`, que se validan siempre.

### Añadir una guarda en `Program.cs` rompe `Orders.Tests` entera — 17 de 17

El efecto secundario que no estaba previsto al planificar el punto. `Orders.Tests` levanta el servicio con `WebApplicationFactory<Program>`, así que **ejecuta el `Program.cs` real**, guarda nueva incluida:

```
Total: 17, Errors: 0, Failed: 17, Skipped: 0, Time: 15.734s

System.InvalidOperationException : Falta la configuración 'ConnectionStrings:RabbitMq'.
  at Program.<Main>$(String[] args)  — src\Services\Orders\Orders.API\Program.cs(111,0)
```

Es exactamente el mecanismo que `2.4` ya había documentado y que el propio `OrdersApiFactory` explica en un comentario —la clave se lee **antes** de `app.Build()`, así que `ConfigureTestServices` llega tarde y el host ni se construye—, solo que ese comentario decía *"las dos claves que Program.cs exige"* y ahora son tres. La corrección es una tercera línea `UseSetting`.

**La regla que se lleva de aquí: en este repositorio, cada guarda nueva en un `Program.cs` es una línea nueva en la fábrica de tests de ese servicio.** Son dos archivos que tienen que cambiar juntos y nada más que la suite lo detecta. `3.3`, que quita `Services:CatalogBaseUrl`, tendrá que quitar la suya.

**No introduce una dependencia real de RabbitMQ en la suite**, y se comprobó en vez de suponerlo: con `docker compose stop rabbitmq`, `CatalogUnavailableTests` pasa igual (6/6). Ningún test publica ni consume nada, y el bus se limita a avisar y reintentar en segundo plano. Lo único que hace falta es que la clave **exista**.

### Un test de arquitectura que nunca se ha visto fallar no está probado

Antes de darlo por bueno, se subió a mano `Payments.API` a `9.2.0`:

```
Shop133.ArchitectureTests.PackageRulesTests.MassTransitPackages_StayOnMajorVersion8 [FAIL]
  MassTransit se queda en 8.x: la v9 tiene licencia comercial y este proyecto no la
  tiene. Fijar la versión en el .csproj, nunca dejarla al criterio de 'dotnet add
  package'. Referencias fuera de la 8.x: Payments.API → MassTransit.RabbitMQ 9.2.0
```

El mensaje nombra el proyecto, el paquete y la versión, y dice qué hacer. Revertido después.

---

## Verificación

### 1. Compilación

```
dotnet build shop133.slnx

Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### 2. Suite de arquitectura — 13 tests

```
tests\Shop133.ArchitectureTests\bin\Debug\net10.0\Shop133.ArchitectureTests.exe -trait "Category=Fast"

=== TEST EXECUTION SUMMARY ===
   Shop133.ArchitectureTests  Total: 13, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.909s
```

### 3. La regla nueva falla cuando debe

Ver *Detalles que cuestan tiempo*. Revertido.

### 4. Los tres servicios conectan

```
=== orders ===        Now listening on: http://localhost:5189
                      Application started. Press Ctrl+C to shut down.
                      Bus started: rabbitmq://localhost/
=== inventory ===     Now listening on: http://localhost:5015
                      Application started. Press Ctrl+C to shut down.
                      Bus started: rabbitmq://localhost/
=== payments ===      Now listening on: http://localhost:5156
                      Application started. Press Ctrl+C to shut down.
                      Bus started: rabbitmq://localhost/
```

API de management (`http://localhost:15672`, guest/guest):

```
client        user  state
------        ----  -----
Payments.API  guest running
Inventory.API guest running
Orders.API    guest running
```

**MassTransit pone el nombre del ensamblado como `connection_name`**, lo cual hace la UI mucho más legible de lo esperado: se ve qué servicio es cada conexión sin mirar puertos.

Colas y exchanges: **ninguno**, y es correcto — ver *Detalles que cuestan tiempo*.

### 5. Broker caído ⇒ el servicio arranca igual

Ver *Detalles que cuestan tiempo*: arranca, sirve HTTP (`404` real desde `OrdersDb`), avisa con `warn`, reintenta con backoff y se reconecta solo al volver el broker.

### 6. El punto abierto de `0.3`

Cerrado y con salida real — ver arriba.

### Resumen

| # | Comprobación | Resultado |
|---|---|---|
| 1 | `dotnet build` de la solución | ✓ 0 warnings, 0 errores |
| 2 | Suite de arquitectura | ✓ 13/13 |
| 3 | La regla de la v9 falla con un 9.2.0 | ✓ y nombra proyecto, paquete y versión |
| 4 | Los tres buses arrancan | ✓ `Bus started` ×3 |
| 5 | Tres conexiones en el broker | ✓ nombradas por ensamblado |
| 6 | Sin topología declarada todavía | ✓ 0 colas, 0 exchanges propios |
| 7 | Broker caído ⇒ el servicio arranca | ✓ `warn` + retry, HTTP vivo |
| 8 | Reconexión sola al volver el broker | ✓ sin reiniciar el servicio |
| 9 | `required` sobrevive al serializador | ✓ `JsonException` nombrando la propiedad |
| 10 | `Orders.Tests` tras el tercer `UseSetting` | ✓ 17/17 (`76.9 s`) |
| 11 | `Orders.Tests` con el broker parado | ✓ 6/6 — la suite no depende de RabbitMQ |
| 12 | `Catalog.Tests` sin cambios | ✓ 19/19 |

---

## Pendiente

- **`3.2`** — definir los eventos en `Shop133.Contracts`. Ya existen desde `0.3`; el punto se resuelve revisando que los 5 que nombra el roadmap siguen siendo los correctos ahora que hay transporte.
- **`3.3`** — al quitar la guarda de `Services:CatalogBaseUrl` de `Program.cs` hay que quitar **también** su `UseSetting` de `OrdersApiFactory`: son dos archivos que cambian juntos. `POST /orders` publica `OrderCreated` y desaparece `Orders.Infrastructure/Catalog/` entero. Es donde aparecerán los primeros exchanges en el broker. Queda abierto de `0.3` **quién rellena los 5 campos de `OrderLine`** cuando la llamada a Catalog ya no exista.
- **`3.4` / `3.5`** — los consumers, y con ellos las primeras colas. Ahí se decide si el bloque `AddMassTransit` duplicado tres veces merece extraerse (decisión 7).
- **`3.7`** — borrar `CatalogUnavailableTests.cs`, `CatalogStub.cs` y el `PackageReference` de `WireMock.Net`; añadir `MassTransit.TestFramework` (que la regla nueva ya cubre: tendrá que ser 8.x).
- **Contenedores de Orders, Inventory y Payments** — sin punto asignado en el roadmap. Cuando lleguen, dos cosas ya sabidas: el host pasa a ser `rabbitmq` y no `localhost`, y **`guest` solo se acepta desde localhost**, así que habrá que crear un usuario por servicio o el broker rechazará la conexión con un error de autenticación que no explica la causa.
- **`8.4`** — `AddMassTransit` ya registra un health check del bus; está inerte hasta que haya un `MapHealthChecks`. No hay que escribirlo, solo exponerlo.
