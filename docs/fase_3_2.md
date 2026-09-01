# Fase 3.2 — Definir eventos en `Shop133.Contracts`

**Fecha:** 2026-08-25 · **Estado:** completado · **Roadmap:** [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md), punto 3.2

---

## Objetivo

El punto nombra cinco eventos —`OrderCreated`, `StockReserved`, `StockRejected`, `PaymentCompleted`, `PaymentFailed`— que **ya existen desde `0.3`**. La decisión 1 de [fase_0_3.md](fase_0_3.md) entregó a propósito los 9 mensajes completos en vez de los 5 de esta fase, para que el camino de compensación se viera escrito de golpe. La sección *Pendiente* de [fase_3_1.md](fase_3_1.md) dejó dicho cómo se cierra esto: *"el punto se resuelve revisando que los 5 que nombra el roadmap siguen siendo los correctos ahora que hay transporte"*.

Así que `3.2` no escribe tipos nuevos. Es **la última revisión de los contratos antes de que exista un solo consumer**, y ese es todo su valor: es el momento más barato que va a haber. Antes de tocar nada se midió — `grep -rn "StockReserved" --include=*.cs src/ tests/` devuelve el propio contrato y **cinco comentarios**, ni un `IConsumer<T>` ni una llamada a `Publish`. A partir de `3.4` cambiar un contrato significa reescribir consumers a la vez (regla 4 de [CLAUDE.md](../CLAUDE.md)).

La revisión encontró **un hueco real** y lo cierra: `StockReserved` no llevaba el importe que Payments.API necesita para cobrar. De paso convierte en ejecutable la decisión de `0.3` que más caro sale deshacer.

**Fuera de alcance deliberadamente:**

| Queda fuera | Entra en |
|---|---|
| Publicar `OrderCreated` y borrar `Orders.Infrastructure/Catalog/` | `3.3` |
| Quién rellena los 3 campos de `OrderLine` que hoy trae Catalog por HTTP | `3.3` — abierto desde `0.3` |
| Partir `OrderLine` en un `StockLine { ProductId, Quantity }` | `3.4` — ver decisión 4 |
| Si `ReleaseStock` puede prescindir de `Lines` | `4.4` — ver *Pendiente* |
| Cualquier `IConsumer<T>` y la carpeta `Consumers/` | `3.4` / `3.5` |
| Idempotencia | `3.6` — usa el `MessageId` del sobre, no un campo de contrato |

---

## Decisiones

### 1. `StockReserved` gana `Amount` — el hueco que encontró la revisión

Tal y como estaba, `StockReserved` llevaba **solo el `OrderId`**. Pero el punto `3.5` dice que Payments.API lo consume, simula el cobro y publica `PaymentCompleted { OrderId, Amount, TransactionId }`.

De dónde saca Payments ese `Amount`? De ningún sitio. No puede leer `OrdersDb` —regla 1, una base de datos por servicio, y desde `0.4` lo impide SQL Server, no la buena voluntad: `payments_user` no tiene permiso—. Y en la Fase 3 no hay saga que se lo diga: la comunicación es **coreografía**, cada servicio reacciona al evento del anterior.

Esto contradice de frente el principio que la propia decisión 3 de [fase_0_3.md](fase_0_3.md) dejó escrito, y que es el que obliga a que `OrderConfirmed` lleve el `CustomerEmail`:

> **un evento debe llevar todo lo que su consumidor necesita para actuar**, porque no puede ir a buscar el resto.

**Elegido:** `StockReserved { OrderId, Amount }`. Inventory.API recibe el total en `OrderCreated.Total` y lo reenvía tal cual.

`Amount` y no `Total`, por simetría con `PaymentCompleted.Amount`, que es el campo al que acaba alimentando.

**Lo incómodo, que es lo que hay que dejar escrito:** Inventory.API no usa `Amount` para *nada*. Es un servicio intermedio acarreando un dato financiero que no le pertenece, y está puesto a conciencia. Esa incomodidad **es la lección**: en coreografía, un servicio termina transportando datos ajenos porque es el único que está en medio del camino. Ese es justo el argumento a favor de la **orquestación** de la Fase 4, donde el que sabe el total es la saga, que lo guardó al arrancar con `OrderCreated`. El campo no está mal puesto: está señalando el coste del estilo de comunicación que la Fase 3 usa y la Fase 4 abandona.

**Y no desaparece en la Fase 4.** La decisión 1 de `0.3` eligió los 9 mensajes de golpe para que la saga no tuviera que tocar Contracts, así que **no existe un comando `ProcessPayment`**: el diagrama de flujo de `0.3` va `StockReserved → PaymentCompleted` directo y Payments.API consume este evento en las dos fases. El campo hace falta en ambas.

### 2. Descartado: que Payments mantuviera su propia copia del importe

La alternativa ortodoxa. Payments.API consume **también** `OrderCreated`, persiste `(OrderId, Total)` en `PaymentsDb` —que existe desde `0.4` y hoy está vacía— y cuando llega `StockReserved` lee su copia. Nadie transporta datos ajenos y cada servicio es dueño de lo que guarda.

**Descartada por una carrera real, no teórica.** RabbitMQ no ordena entre colas distintas: `OrderCreated` y `StockReserved` llegan por caminos independientes, y `StockReserved` puede llegar **antes** de que Payments haya terminado de procesar `OrderCreated`. El consumer tendría que detectar que no tiene el pedido y aplazar el mensaje —retry, redelivery, o `Task.Delay` y rezar—, que es infraestructura de mensajería avanzada metida en `3.5` para resolver un problema que un campo `decimal` quita de en medio.

El coste de descartarla está reconocido: es la opción correcta el día que el importe deje de ser un número y pase a ser algo que Payments necesite consultar por su cuenta. Hoy no lo es.

### 3. Descartado: aplazar la decisión a `3.5`, con Payments delante

Es el patrón que este proyecto usa a menudo y que la decisión 4 de aquí abajo aplica sin rechistar: no decidas la forma de un mensaje sin su consumidor escrito.

**Aquí no aplica, y el motivo es el coste.** En `3.5` ya existirá el consumer de Inventory de `3.4`, que es precisamente quien tiene que **publicar** este evento. Cambiar `StockReserved` entonces no es editar un `record`: es editar un `record` y el consumer que lo construye. Hoy cuesta cero, medido — nada consume el tipo.

Y la diferencia de fondo con la decisión 4: aquí no hay nada que averiguar. La pregunta *"¿de dónde saca Payments el importe?"* tiene una respuesta cerrada —de ningún sitio— y no depende de cómo se escriba el consumer. La del `StockLine` sí depende.

### 4. `ReserveStock`/`ReleaseStock` siguen llevando `OrderLine` entero — el aplazamiento se mantiene

De los cinco campos de `OrderLine`, a Inventory le sobran **tres**: `UnitPrice`, `ProductSku` y `ProductName`. La nota de revisión de la decisión 6 de [fase_0_3.md](fase_0_3.md) reconoce que con 3 de 5 el argumento original se debilita, y se comprometió a decidirlo **"en `3.4`, con el consumidor delante"**.

**Se mantiene el aplazamiento.** No por inercia: partir el tipo hoy sigue siendo especular sobre lo que `Inventory.API` necesitará —puede querer el nombre para sus propios logs, que es exactamente la clase de cosa que no se sabe hasta escribir el consumer— y obligaría a la saga de la Fase 4 a mapear `OrderLine → StockLine`, código escrito a ciegas.

La coherencia con la decisión 3 es la que se explicó allí: aquí falta información que solo aparece con el código delante; en el caso de `StockReserved` no faltaba ninguna.

### 5. Los otros cuatro eventos se revisaron y se quedan como están

Que un tipo no cambie también es una decisión, y sin escribirla no consta que se haya mirado.

- **`OrderCreated`** — `Total` es redundante: se puede sumar desde `Lines`, y de hecho `Order.Total` es una propiedad calculada, no una columna ([Order.cs:212](../src/Services/Orders/Orders.Domain/Entities/Order.cs#L212)). **Se mantiene igualmente.** El importe de un pedido es un hecho congelado, como el precio unitario de `OrderLine`: si cada consumidor lo recalcula por su cuenta, cada uno puede redondear distinto y no habría forma de saber cuál es el bueno. Una fuente de verdad, decidida por quien acepta el pedido.
- **`StockRejected`** y **`PaymentFailed`** — `Reason` sigue siendo texto de diagnóstico y para el email de Notifications, **no un código que nadie deba parsear** para decidir. Sin cambios.
- **`PaymentCompleted`** — sin cambios. `TransactionId` sigue siendo un valor simulado que existe desde `0.3` porque es lo que permitiría emitir la devolución si algún día hubiera que compensar el pago.

**Los dos eventos de error no tienen consumidor en la Fase 3.** `OrderCancelled` lo publica la saga en `4.3` y Notifications.API llega en `4.6`, así que en `3.4` y `3.5` estos dos mensajes se publican en un exchange sin colas ligadas. Conviene saberlo: **no falla ni avisa** — el broker descarta el mensaje en silencio, exactamente igual que si el consumer no estuviera registrado. Es la misma clase de fallo mudo que `3.1` evitó dejando puesto `cfg.ConfigureEndpoints(context)` desde el principio.

### 6. La regla de arquitectura: el namespace **es** el nombre del exchange

CLAUDE.md pide considerar, al tocar una regla, si `Shop133.ArchitectureTests` puede hacerla ejecutable — *"una regla que solo vive en prosa se rompe en silencio"*.

La candidata es la decisión 2 de `0.3`, y no por gusto: es **la única decisión de aquel punto que sale cara si se cambia después**. MassTransit deriva el nombre del exchange de RabbitMQ del nombre completo del tipo (`Shop133.Contracts.Events:OrderCreated`). Mover un tipo de namespace **no es un refactor** — renombra un exchange y deja huérfanos los mensajes que estuvieran en vuelo. Y no rompe la compilación de nadie, así que no hay nada que avise.

`Contracts_PublicTypes_LiveInEventsOrCommandsNamespace` exige que todo tipo público viva en `Shop133.Contracts.Events` o `Shop133.Contracts.Commands`, con `OrderLine` como **única excepción declarada a mano** en el propio test: está en la raíz a propósito porque lo usan ambos lados y no viaja solo por el broker — no es un mensaje, es un DTO que va dentro de otros.

**Va en `ContractsRulesTests.cs`, no en un archivo nuevo.** Es una regla sobre el contenido de Contracts, igual que las cinco que ya estaban ahí. El precedente que *no* se sigue es el de `3.1`, que sacó `MassTransitPackages_StayOnMajorVersion8` a su propio `PackageRulesTests.cs` — pero aquello se separó porque una regla de licencia no es una regla de capas, no porque cada regla nueva merezca un archivo.

Implementación por reflexión sobre `GetExportedTypes()` comparando `type.Namespace`, en la línea de `Contracts_PublicTypes_AreSealedRecords`. Ni `ProjectGraph` ni `NetArchTest` hacen falta aquí. La suite pasa de **13 a 14**.

---

## Cambios

**Ningún `.csproj` se tocó.** Contracts sigue con cero `PackageReference` y cero `ProjectReference`, que es lo que hace verificable la regla 4.

| Archivo | Rol |
|---|---|
| [src/Shared/Shop133.Contracts/Events/StockReserved.cs](../src/Shared/Shop133.Contracts/Events/StockReserved.cs) | Gana `Amount`, y el `///` que explica por qué Inventory acarrea un dato que no usa y por qué el campo sobrevive a la Fase 4 |
| [tests/Shop133.ArchitectureTests/ContractsRulesTests.cs](../tests/Shop133.ArchitectureTests/ContractsRulesTests.cs) | `[Fact]` nuevo + los dos arrays `MessageNamespaces` y `AllowedRootTypes`. 13 → 14 tests |

Otros archivos: [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md) (checkbox 3.2), [docs/README.md](README.md) (fila del índice) y [CLAUDE.md](../CLAUDE.md) (párrafo de estado y conteos de tests).

**El flujo, con el cambio dentro:**

```
OrderCreated{Lines,Total} → ReserveStock ─┬→ StockReserved{Amount} → PaymentCompleted{Amount} → OrderConfirmed
                                          │                        └→ PaymentFailed → ReleaseStock → OrderCancelled
                                          └→ StockRejected ──────────────────────────────────────→ OrderCancelled
```

El `Total` entra por la izquierda, atraviesa Inventory sin que a Inventory le importe, y sale por `PaymentCompleted`. Ese recorrido es la decisión 1 dibujada.

---

## Detalles que cuestan tiempo

**Un `decimal` viaja como *string* JSON, no como número.** Salida real del serializador de MassTransit:

```json
{
  "orderId": "7810e9c5-4885-4596-948d-6036cbdac1b3",
  "amount": "39.98"
}
```

`SystemTextJsonMessageSerializer.Options` trae `JsonNumberHandling.WriteAsString | AllowReadingFromString`, así que el importe sale entrecomillado. Entre servicios .NET da igual —el round-trip es correcto y `back.Amount == 39.98m`—, pero importa en dos sitios: al mirar un mensaje en la UI de RabbitMQ, donde parece un error y no lo es, y el día que un consumidor no-.NET lea la cola. Es el mismo fenómeno que CLAUDE.md ya tenía anotado para OpenAPI, donde un campo numérico se anuncia como `"type": ["integer","string"]`.

**`SystemTextJsonMessageSerializer` está en `MassTransit.Serialization`, no en `MassTransit`.** El `using MassTransit;` que uno escribe por reflejo compila y luego falla con `CS0103: The name 'SystemTextJsonMessageSerializer' does not exist in the current context`, que no menciona el namespace que falta.

**Que un test pase no demuestra que sirva.** La regla nueva se verificó **rompiéndola a propósito**: se creó un `TempRuleProbe` en el namespace raíz de Contracts, se compiló y la suite pasó a 13/14 nombrando el tipo y su namespace. Sin esa comprobación, una regla mal escrita —un filtro que no case nunca— pasa en verde para siempre y da una falsa sensación de cobertura. El archivo se borró acto seguido y la suite volvió a 14/14.

**El cambio de contrato cuesta cero *hoy*, y esa ventana se cierra en `3.4`.** Es el mismo argumento con el que la revisión del 2026-08-19 añadió `ProductSku` y `ProductName` a `OrderLine`, y conviene tenerlo presente al leer la Fase 3: todo lo que quede por decidir sobre la forma de un mensaje se encarece en cuanto exista el primer `IConsumer<T>`.

---

## Verificación

Ejecutado el 2026-08-25. Salidas reales.

| # | Comprobación | Resultado |
|---|---|---|
| 1 | El cambio cuesta cero: usos de `StockReserved` en `src/`+`tests/` | ✓ el contrato y 5 comentarios, ningún consumer |
| 2 | `dotnet build shop133.slnx` | ✓ **0 Warning(s), 0 Error(s)** |
| 3 | Suite de arquitectura | ✓ **14/14** |
| 4 | La regla nueva falla si se rompe | ✓ 13/14, nombrando tipo y namespace |
| 5 | Round-trip de `StockReserved` con el serializador de MassTransit | ✓ `OrderId` igual, `Amount` 39.98 |
| 6 | Payload sin `amount` ⇒ `required` lo rechaza | ✓ `JsonException` nombrando `'amount'` |
| 7 | Los 10 tipos y sus namespaces | ✓ 9 en `Events`/`Commands`, `OrderLine` en la raíz |

**Build y suite:**

```
Build succeeded.
    0 Warning(s)
    0 Error(s)

   Shop133.ArchitectureTests  Total: 14, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.870s
```

**La regla, rota a propósito** (`TempRuleProbe` en el namespace raíz, luego borrado):

```
Shop133.ArchitectureTests.ContractsRulesTests.Contracts_PublicTypes_LiveInEventsOrCommandsNamespace [FAIL]
  El namespace de un mensaje es el nombre de su exchange en RabbitMQ: todo tipo de Contracts vive en
  Shop133.Contracts.Events o Shop133.Contracts.Commands. Incumplen: Shop133.Contracts.TempRuleProbe
  (namespace: Shop133.Contracts)

   Shop133.ArchitectureTests  Total: 14, Errors: 0, Failed: 1, Skipped: 0, Not Run: 0, Time: 0.779s
```

**Cómo se verificó el contrato.** Que `dotnet build` pase no demuestra casi nada — igual que en `0.3`, **ningún proyecto usa todavía estos tipos**, así que compilarían igual estando mal. Se repitió el método de `0.3` y `3.1`: un proyecto de consola desechable **en el scratchpad, fuera del repo**, con `ProjectReference` a `Shop133.Contracts` y `PackageReference` a `MassTransit` 8.5.10, que serializa con `SystemTextJsonMessageSerializer.Options` — las opciones reales del broker, no `System.Text.Json` a pelo.

```
=== 1. Round-trip de StockReserved con Amount ===
{
  "orderId": "7810e9c5-4885-4596-948d-6036cbdac1b3",
  "amount": "39.98"
}
OrderId igual: True  Amount: 39.98

=== 2. Payload SIN amount: 'required' tiene que rechazarlo ===
JsonException: JSON deserialization for type 'Shop133.Contracts.Events.StockReserved' was missing required properties including: 'amount'.

=== 3. Tipos exportados y su namespace (regla de 3.2) ===
Shop133.Contracts.Commands   ReleaseStock       [OrderId, Lines]
Shop133.Contracts.Commands   ReserveStock       [OrderId, Lines]
Shop133.Contracts.Events     OrderCancelled     [OrderId, CustomerEmail, Reason]
Shop133.Contracts.Events     OrderConfirmed     [OrderId, CustomerEmail]
Shop133.Contracts.Events     OrderCreated       [OrderId, CustomerEmail, Lines, Total]
Shop133.Contracts.Events     PaymentCompleted   [OrderId, Amount, TransactionId]
Shop133.Contracts.Events     PaymentFailed      [OrderId, Reason]
Shop133.Contracts.Events     StockRejected      [OrderId, Reason]
Shop133.Contracts.Events     StockReserved      [OrderId, Amount]
Shop133.Contracts            OrderLine          [ProductId, ProductSku, ProductName, Quantity, UnitPrice]
```

El check 6 es el que importa: confirma que la protección de `required` que `3.1` midió para los demás mensajes **cubre también el campo nuevo**. Un `StockReserved` sin importe no llega a Payments con un `0m` silencioso — falla al deserializar y acaba en la cola de error.

`Catalog.Tests` (19) y `Orders.Tests` (17) no tocan estos tipos; basta con que la build pase. El proyecto de consola **no se añadió al repo**.

---

## Pendiente

- **`3.3`** — Orders publica `OrderCreated`, desaparece `Orders.Infrastructure/Catalog/` y con él la guarda de `Services:CatalogBaseUrl` en `Program.cs` **y su `UseSetting` en `OrdersApiFactory`**: cambian juntos. Sigue abierto de `0.3` **quién rellena los 3 campos de `OrderLine` que hoy trae la llamada a Catalog** — el problema de propiedad del dato frente a localidad del dato. Este punto no lo resolvió porque no cambia la forma de `OrderCreated`: la respuesta afecta a quién construye la línea, no al contrato.
- **`3.4`** — el consumer de Inventory, y con él la decisión del `StockLine` que la decisión 4 vuelve a aplazar. Es también donde Inventory tendrá que **reenviar `OrderCreated.Total` como `StockReserved.Amount`**; si se olvida, no falla nada visible: el pedido se cobra por 0.
- **`3.5`** — Payments.API consume `StockReserved` y ya tiene de dónde sacar el importe. Es la comprobación de que la decisión 1 era correcta.
- **`4.4`** — si `ReleaseStock` puede prescindir de `Lines`. Inventory habrá guardado la reserva en `InventoryDb` en `3.4`, así que probablemente pueda soltarla con el `OrderId` solo; y menos datos en la compensación es menos superficie para el duplicado que su propio `///` advierte. No se decide antes de saber cómo quedó la tabla de reservas.
- **`4.1`** — la saga correlaciona con `.CorrelateById(x => x.Message.OrderId)`. Sin esa línea no hay correlación: la decisión 5 de `0.3` dejó los contratos sin `CorrelationId` a cambio de esa línea de configuración.
