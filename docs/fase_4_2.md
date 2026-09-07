# Fase 4.2 — Estados de la saga: la cadena feliz completa

**Fecha:** 2026-09-01 · **Estado:** completado · **Roadmap:** [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md)

---

## Objetivo

`4.1` dejó la saga existiendo y correlacionando, pero con **un solo evento y un solo estado**: `Initially(OrderCreated) → StockPending`, y ahí se quedaba para siempre. Los `StockReserved` y `PaymentCompleted` que Inventory y Payments publican desde `3.4`/`3.5` no los miraba nadie. La verificación 6 de [fase_3_5.md](fase_3_5.md) lo había medido: el exchange de `PaymentCompleted` tenía **cero colas enlazadas** — se publicaba al vacío, sin fallo y sin aviso.

Este punto cierra la cadena feliz de punta a punta:

```
OrderCreated  →  StockPending  →  PaymentPending  →  Confirmed  ⇒  Publish(OrderConfirmed)
```

Y con el último paso, la saga **emite su primer mensaje** en todo el proyecto. Hasta hoy solo observaba: los cinco eventos de la Fase 3 los publican los servicios. `OrderConfirmed` no tiene otro autor posible, porque nadie más ve las dos mitades del proceso a la vez — Inventory sabe que reservó, Payments sabe que cobró, y ninguno de los dos sabe que el pedido terminó bien.

Es también donde se cobra una previsión de `4.1`: **`OrderConfirmed` exige `CustomerEmail` y `PaymentCompleted` no lo lleva**. La decisión 6 de aquel punto copió el email en el `Initially` con el argumento de que "después ya no vuelve a pasar por delante". Aquí se usa por primera vez, y sin aquella línea no habría a quién avisar.

**Fuera de alcance deliberadamente:**

| Queda fuera | Entra en |
|---|---|
| `Order.Confirm()` / `Order.Cancel()` y mover `Order.Status` | `4.3` — y con él el **primer consumer de Orders.API** y su tabla `ProcessedMessages` en `OrdersDb` |
| `StockRejected → Cancelled`, `PaymentFailed → CompensatingStock → Cancelled`, `OrderCancelled` | `4.3` |
| Publicar `ReleaseStock` y soltar el stock de un cobro rechazado | `4.4` |
| Persistir la instancia de saga, el token de concurrencia optimista, `Finalize()` y el outbox | `4.5` |
| Consumir `OrderConfirmed` | `4.3` (Orders) / `4.6` (Notifications) |
| `PricingPending` delante de `StockPending` | `4.8` / `4.9` |
| Los cuatro escenarios obligatorios con el harness | `4.7` |

---

## Decisiones

### 1. Tres estados, no los cinco que enumera el roadmap

El punto se titula `Submitted → StockPending → StockReserved → PaymentPending → Confirmed`. Se entregan **tres**: `StockPending`, `PaymentPending` y `Confirmed`.

No es un recorte por comodidad: es la consecuencia directa de la decisión 2 de [fase_4_1.md](fase_4_1.md), que fijó que **la saga observa la coreografía y no la orquesta**. Un estado tiene sentido cuando la saga *espera* algo, y lo que crea esa espera es haber mandado un comando. Aquí no se manda ninguno:

- **`Submitted`** sería "el pedido está aceptado y todavía no he pedido el stock". Ese hueco no existe: el mismo `OrderCreated` que arranca la saga es el que Inventory ya está consumiendo. Se entraría y se saldría en la misma transición.
- **`StockReserved`** sería "el stock está reservado y todavía no he pedido el cobro". Tampoco existe: el mismo `StockReserved` que la saga recibe lo está recibiendo Payments.

Un estado que ninguna instancia puede tener cuando se la consulta es ruido en la tabla que creará `4.5` y una rama más que mantener en `4.7`.

**Descartado — declararlos igual, encadenando `TransitionTo`.** Sería fiel al texto del punto y produciría un diagrama más parecido al de los tutoriales de saga. Se descarta porque el diagrama mentiría: enseñaría cinco esperas donde hay tres, y escondería justo la diferencia entre orquestación y coreografía que el proyecto existe para hacer visible.

La regla que queda escrita, por si aparece un estado nuevo: **en una saga que observa, hay un estado por cada respuesta que se espera, no por cada hecho que ocurre.** `4.9` mete `PricingPending` *delante* de `StockPending` y encaja sin discusión — es otro sitio donde la saga espera de verdad, esta vez a Catalog. `Submitted` no reaparece.

### 2. Publica `OrderConfirmed` y **no** toca `Order.Status`

La tabla de fuera-de-alcance de `4.1` era ambigua en los dos sentidos: asignaba `Order.Confirm()` a "4.2/4.3" y publicar `OrderConfirmed` a "4.3/4.6". Se parte así:

**`OrderConfirmed` entra aquí.** Sin él, llegar a `Confirmed` no produce nada observable salvo una línea de log, y el punto no se podría verificar contra el broker. Además deja el exchange creado y con su primer publicador, que es lo que `4.6` necesita encontrar.

**`Order.Status` no.** Moverlo no es escribir un método en la entidad: la saga vive en `Orders.Domain` y **no puede tocar `OrdersDbContext`** —la flecha va `.API → .Infrastructure → .Domain` (regla 5)—, así que hace falta un consumer en `Orders.API/Consumers/`, el primero del servicio. Y ese consumer arrastra la tabla `ProcessedMessages` de `3.6` en `OrdersDb` (entidad, configuración y migración), porque la regla 6 no admite consumers sin guarda.

Se agrupa todo en `4.3`, que ya tiene que escribir `Cancel()` y consumir `OrderCancelled`: un punto que estrena el inbox de Orders y sus dos desenlaces a la vez se lee mejor que dos medios puntos.

**El precio está medido y es la parte interesante:** durante `4.2` la saga puede estar en `Confirmed` mientras `GET /orders/{id}` sigue contestando `"status":"Pending"` (verificación 4). Es una **inconsistencia temporal real**, del tipo exacto que el checklist final del roadmap pide poder reproducir y explicar. No es un defecto del punto; es lo que se ve cuando el estado del proceso y el estado del agregado viven en sitios distintos y solo uno de los dos se ha conectado.

**Descartado — que la saga escriba en `OrdersDb` a través de un puerto** (`IOrderWriter` en `Orders.Domain`, implementado en `Orders.Infrastructure`). Respetaría la regla 5 y ahorraría el salto por el broker. Se descarta porque mete una interfaz de una sola implementación y un único método para no usar el mecanismo que el proyecto ya tiene montado y que `4.6` va a usar de todas formas sobre el mismo evento. Dos consumidores de un fanout es el patrón que este sistema repite desde `3.4`; un puerto sería una segunda forma de hacer lo mismo.

### 3. `OnMissingInstance(m => m.Fault())` — y **no** es el valor por defecto

Ésta es la decisión que salió al revés de como se había planificado, así que se documenta con la corrección incluida.

**Lo que se creía:** que MassTransit faultea cuando llega un evento correlacionado (no `Initially`) y no hay instancia viva, de modo que bastaba con no escribir nada.

**Lo que se midió (verificación 7):** MassTransit 8 lo **descarta en silencio**. Ni excepción, ni cola de error, ni una línea de log. Se reenvió un `PaymentCompleted` de un pedido real después de reiniciar Orders.API y el mensaje sencillamente se desvaneció.

Eso importa porque `InMemoryRepository()` pierde todas las instancias al reiniciar —medido en la verificación 7 de `4.1`—, y desde `4.2` esa pérdida tiene consecuencia: en `4.1` el único evento declarado era el que *arranca* la saga, así que un reenvío simplemente empezaba de cero. Ahora un pedido que estaba esperando su cobro se queda huérfano, y con el descarte por defecto **desaparece sin dejar rastro**: justo el agujero que `4.5` existe para cerrar, hecho invisible.

**Elegido:** dos líneas explícitas, `e.OnMissingInstance(missing => missing.Fault())`, que lo mandan a `order-state_error`, donde se ve y se puede contar.

Es la misma lección que la guarda `Ignore` de `4.1`, en la otra dirección: **lo que hay que dejar escrito es aquello que el valor por defecto no hace**. Y confirma por segunda vez que en esta máquina de estados no se puede razonar sobre el comportamiento por defecto sin comprobarlo.

Cuando `4.5` persista la saga, estas dos líneas dejarán de dispararse por reinicios y pasarán a señalar lo único que quedará: un evento de un pedido que nunca existió. Merece una relectura entonces, no una retirada.

### 4. Idempotencia: los `Ignore` van por estado, y el terminal es el que se olvida

La guarda de `4.1` (`During(StockPending, Ignore(OrderCreated))`) se convierte en seis, repartidas por los tres `During`. Sigue siendo la mitad de **negocio** de la guarda de `3.6` —reconoce el mismo *pedido*, no la misma *entrega*—, y sigue sin poder ser la tabla `ProcessedMessages`, que no tiene dónde vivir hasta `4.5`.

La regla para no equivocarse al añadir estados: **en cada estado se ignoran los eventos que ya se atendieron *antes* de llegar a él.**

El `During(Confirmed, …)` es el que más fácil se deja fuera, porque no contiene ninguna transición y parece código muerto. No lo es: sin él, una reentrega tardía de cualquiera de los tres eventos manda a la cola de error un pedido que terminó perfectamente. Se comprobó rompiéndolo (verificación 6b) y salieron **dos** faults de un solo reenvío.

**Y lo que deliberadamente no se ignora:** no hay `Ignore(PaymentCompleted)` en `StockPending`. Añadirlo "por simetría" sería un error — un cobro aceptado sin haber visto la reserva no es un duplicado, es una **entrega fuera de orden**, y RabbitMQ no ordena entre colas. Ignorarlo dejaría el pedido esperando para siempre un `PaymentCompleted` que ya pasó; faultear lo pone donde se ve. Mismo criterio que la decisión 3.

### 5. `Confirmed` es un estado normal, no `Finalize()`

Lo canónico en MassTransit es `.Finalize()` en el estado terminal, con `SetCompletedWhenFinalized()` para que el repositorio borre la instancia.

**Descartado por ahora.** Hoy el repositorio es en memoria: no hay fila que borrar, así que finalizar no ahorra nada y sí quita poder inspeccionar el desenlace — que es justamente lo que hace verificable este punto. Cuando `4.5` cree la tabla habrá un coste real (filas que se acumulan) y un contrapeso real (un pedido cerrado del que no queda rastro salvo `Order.Status`), y entonces la decisión se toma con la tabla delante. Es el mismo criterio con el que `2.2` dejó los índices de `OrdersDb` para cuando hubiera una consulta que los pidiera.

### 6. Ninguna regla de arquitectura nueva. La suite se queda en 16

Este punto no introduce ninguna forma estructural que no estuviera ya vigilada: la máquina de estados sigue siendo la misma clase en el mismo proyecto, y `StateMachineFiles_LiveOnlyIn_OrdersDomain` la cubre desde `4.1`.

Se dice por escrito en vez de inventar una regla para subir el contador, siguiendo el precedente de `3.3` y `3.5`. Una regla que nunca engancha pasa verde para siempre, que es el aviso de `3.2`.

---

## Cambios

### Modificados

| Archivo | Qué cambió |
|---|---|
| [Orders.Domain/Sagas/OrderStateMachine.cs](../src/Services/Orders/Orders.Domain/Sagas/OrderStateMachine.cs) | Dos estados nuevos (`PaymentPending`, `Confirmed`), dos eventos nuevos (`StockReserved`, `PaymentCompleted`) con su `CorrelateById` y su `OnMissingInstance(Fault)`, dos transiciones, el `Publish` de `OrderConfirmed` y las seis guardas `Ignore`. |

**Es el único archivo de `src/` que se toca.** Ni un contrato, ni `OrderState`, ni un `.csproj`, ni `Program.cs`: la saga ya estaba registrada desde `4.1` y `ConfigureEndpoints` ya creaba la cola `order-state`.

### Lo que no se tocó

- **`Shop133.Contracts`.** Ni un campo, por segundo punto consecutivo. Era el objetivo de la decisión 1 de `0.3` al fijar los 9 mensajes de golpe.
- **`OrderState`.** No necesita ningún campo nuevo: `CustomerEmail` ya estaba (decisión 6 de `4.1`) y es todo lo que `OrderConfirmed` pide además del id. El importe sigue fuera a propósito — `PaymentCompleted` lo trae.
- **`Order`, `OrderItem`, `OrderStatus`, `OrdersDbContext` y las migraciones.** Decisión 2.
- **`OrdersController`.** Publica `OrderCreated` igual que desde `3.3`.
- **Inventory y Payments**, sus consumers y sus `Program.cs`. Es la decisión 2 de `4.1`, y por eso Payments sigue consumiendo `StockReserved` en paralelo a la saga.
- **`OrdersApiFactory`.** Ninguna clave de configuración nueva, así que ningún `UseSetting` nuevo — la regla de `3.1` se comprobó y no aplica.
- **Paquetes NuGet.** Ninguno. **Migraciones.** Ninguna.

---

## Detalles que cuestan tiempo

**El comportamiento por defecto ante una instancia inexistente es descartar, no faultear.** Está en la decisión 3 porque es una decisión, pero también es el gotcha más caro del punto: un mensaje que desaparece sin excepción, sin cola de error y sin log no se distingue de un mensaje que se procesó bien. Si algún día un pedido se queda a medias y no hay nada en `order-state_error`, la primera sospecha es que alguien quitó estas dos líneas.

**`.Publish(context => new T{...})` existe; no hace falta `PublishAsync` + `Init<T>`.** Casi todos los ejemplos de saga usan la forma larga (`.PublishAsync(context => context.Init<OrderConfirmed>(new OrderConfirmed{...}))`) y es fácil dar por hecho que la corta no compila sobre el `BehaviorContext` de una saga. Compila. `Init<T>` solo hace falta cuando hay que tocar el sobre —cabeceras propias, un TTL—; el `MessageId` y el `ConversationId` los pone MassTransit igual por los dos caminos.

**Romper la guarda del estado terminal produce *dos* faults, no uno.** Al reenviar a mano un `StockReserved` de un pedido ya confirmado, Payments lo recibe también: su guarda de negocio de `3.5` ve el pago ya hecho y **republica el `PaymentCompleted` guardado**. Así que un solo reenvío llega a la saga dos veces, con dos eventos distintos. Es un efecto secundario útil —prueba dos guardas de una vez— pero desconcierta si se esperaba un mensaje en la cola de error y hay dos.

**Un reenvío a mano no sirve para probar la guarda si hay que reiniciar el servicio en medio.** Reiniciar Orders.API borra las instancias en memoria, así que el reenvío pasa a probar la rama de "instancia inexistente" en vez de la de "evento no aceptado". Para el contraste de la verificación 6b hay que **crear un pedido nuevo después del reinicio** y reenviar el suyo. Las dos excepciones se distinguen bien: `NotAcceptedStateMachineException … Not accepted in state Confirmed` frente a `SagaException … An existing saga instance was not found`.

**El reenvío a mano sigue necesitando las cuatro cosas de siempre** — JSON sin BOM, `content_type: application/vnd.masstransit+json`, `messageType` con el URN completo y `message_id` en `properties`. `{"routed":true}` sigue significando que llegó a una cola, no que alguien pudiera leerlo.

**`Get-Process -Name "Orders.API" | Stop-Process`** es lo que para el servicio de verdad: matar el `dotnet run` de fondo deja vivo el proceso hijo, que sigue con la cola `order-state` enganchada y consume los mensajes del experimento siguiente.

**Smart App Control no dio guerra en este punto.** Igual que en `4.1`, y sigue sin significar nada de la próxima vez: el bloqueo es por fichero y por reputación.

---

## Verificación

Ejecutado el 2026-09-01 contra la infraestructura de `docker compose` (SQL Server, RabbitMQ, más el contenedor de `catalog-api`) y Orders/Inventory/Payments lanzados desde línea de comandos con `--launch-profile http`. Salidas reales.

### 1. Build

```
Build succeeded.
    2 Warning(s)
    0 Error(s)

Time Elapsed 00:00:08.26
```

*(Los 2 avisos son los `xUnit1051` de `CreateOrderTests.cs`, heredados de `3.7` y ajenos a este punto.)*

### 2. Tests de arquitectura — siguen en 16

```
   Shop133.ArchitectureTests  Total: 16, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.249s
```

Ninguna regla nueva, decisión 6.

### 3. `Orders.Tests` sigue en 12/12

```
   Orders.Tests  Total: 12, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 57.321s
```

Como se esperaba: `OrdersApiFactory` desmonta MassTransit y monta el harness, así que la saga no llega a registrarse. Automatizarla es `4.7`.

### 4. Topología: los dos bindings que faltaban

Antes de arrancar Orders.API con la saga de `4.2`:

```
== BINDINGS (antes de 4.2) ==
Shop133.Contracts.Events:OrderCreated              -> order-created
Shop133.Contracts.Events:OrderCreated              -> order-state
Shop133.Contracts.Events:StockReserved             -> stock-reserved
```

Después:

```
== BINDINGS (con la saga de 4.2 arrancada) ==
Shop133.Contracts.Events:OrderCreated              -> order-created
Shop133.Contracts.Events:OrderCreated              -> order-state
Shop133.Contracts.Events:PaymentCompleted          -> order-state
Shop133.Contracts.Events:StockReserved             -> order-state
Shop133.Contracts.Events:StockReserved             -> stock-reserved
```

Dos hechos objetivos: **`StockReserved` pasa a dos bindings** (Payments y la saga — la decisión 2 de `4.1` hecha visible por segunda vez) y **`PaymentCompleted` gana su primer binding**, así que deja de publicarse al vacío. Eso es medio incumplimiento de la regla 7 cerrado; el otro medio es `PaymentFailed`, que es `4.3`.

### 5. La cadena feliz completa

```
POST /orders  {"customerEmail":"saga42-v2@shop133.test","items":[{"productId":2,"quantity":2,
               "productSku":"TAZA-002","productName":"Taza Calavera Catrina","unitPrice":229.00}]}

pedido = b44627e0-ad54-4408-9b12-908885d380d1  status en el 201 = Pending  total = 458
```

Log de Orders, el pedido entero:

```
Pedido b44627e0-… creado con 1 línea(s) por un total de 458; OrderCreated publicado.
Saga arrancada para el pedido b44627e0-… de saga42-v2@shop133.test; pasa a StockPending.
Pedido b44627e0-…: stock reservado por 458; pasa a PaymentPending.
Pedido b44627e0-…: cobro aceptado por 458 (transacción SIM-A93D8CB79B6446BE96AD77CA26BEBC93); pasa a Confirmed y se publica OrderConfirmed.
```

Y el exchange nuevo, creado por el primer `Publish`:

```
== EXCHANGES de Shop133.Contracts ==
Shop133.Contracts.Events:OrderConfirmed
Shop133.Contracts.Events:OrderCreated
Shop133.Contracts.Events:PaymentCompleted
Shop133.Contracts.Events:PaymentFailed
Shop133.Contracts.Events:StockRejected
Shop133.Contracts.Events:StockReserved

== Colas enlazadas a OrderConfirmed = 0
```

**Cero colas enlazadas**, o sea que hoy `OrderConfirmed` se publica al vacío igual que hacía `PaymentCompleted` hasta esta mañana. Lo enlazan `4.3` (Orders, para mover `Order.Status`) y `4.6` (Notifications).

Lo que **no** cambió, y es la decisión 2 medida:

```
GET /orders/{id} -> status = Pending
```

La saga está en `Confirmed` y el pedido sigue diciendo `Pending`. Inconsistencia temporal, visible y con dueño: `4.3`.

### 6a. Idempotencia (regla 6), con la guarda puesta

Reenvío a mano del mismo `StockReserved` con **`message_id` nuevo** (`6f3e2929-…`), para que atraviese la guarda de transporte de `3.6` de Payments y llegue de verdad al estado de la saga:

```
{"routed":true}

== COLAS tras el reenvio (guarda puesta) ==
order-created                  messages=0
order-created_error            messages=0
order-state                    messages=0
order-state_error              messages=0
stock-reserved                 messages=0
```

Las líneas de la saga para ese pedido siguen siendo **cuatro**, una por transición: no se reprocesó nada. Y Payments hizo lo suyo:

```
El pedido 4623b85e-… ya se había cobrado el 09/01/2026 18:10:02 +00:00 con resultado Completed;
no se vuelve a cobrar y se reenvía el desenlace guardado.
```

Ese reenvío del `PaymentCompleted` guardado llegó también a la saga, en `Confirmed`. O sea que este único experimento ejercitó `Ignore(StockReserved)` **y** `Ignore(PaymentCompleted)`.

### 6b. El contraste — sin él, lo anterior no demuestra nada

Comentadas `Ignore(StockReserved)` e `Ignore(PaymentCompleted)` del `During(Confirmed, …)`, recompilado y reiniciado. Como el reinicio borra las instancias, se crea un pedido **nuevo** (`8e1db892-…`), se le deja llegar a `Confirmed` y se reenvía su `StockReserved`:

```
== COLAS (guarda de Confirmed rota) ==
order-state                    messages=0
order-state_error              messages=2
```

```
MassTransit.NotAcceptedStateMachineException: Orders.Domain.Sagas.OrderState(8e1db892-…) Saga exception
on receipt of Shop133.Contracts.Events.StockReserved: Not accepted in state Confirmed
 ---> MassTransit.UnhandledEventException: The StockReserved event is not handled during the Confirmed
      state for the OrderStateMachine state machine

MassTransit.NotAcceptedStateMachineException: Orders.Domain.Sagas.OrderState(8e1db892-…) Saga exception
on receipt of Shop133.Contracts.Events.PaymentCompleted: Not accepted in state Confirmed
```

**Dos** mensajes en la cola de error de un solo reenvío, por el rebote de Payments descrito arriba. Restauradas las dos líneas, recompilado y purgada la cola (`DELETE /api/queues/%2F/order-state_error/contents` → HTTP 204).

### 7. Sin instancia: el defecto es descartar en silencio

Éste es el experimento que salió al revés de lo previsto, y de ahí la decisión 3.

**Primero, sin `OnMissingInstance`** (que era como estaba escrito el punto al planificarlo). Reiniciado Orders.API —lo que borra todas las instancias— y reenviado el `PaymentCompleted` del pedido `4623b85e-…`, que estaba en `Confirmed` antes del reinicio:

```
{"routed":true}

== COLAS (instancia perdida por el reinicio) ==
order-state                    messages=0
order-state_error              messages=0

== Que dice Orders ==
(nada)
```

**El mensaje se desvaneció.** Ni excepción, ni cola de error, ni una línea de log.

Añadido `e.OnMissingInstance(missing => missing.Fault())` a los dos eventos, recompilado, reiniciado y repetido exactamente el mismo reenvío:

```
== COLAS (instancia perdida, con Fault explicito) ==
order-state                    messages=0
order-state_error              messages=1

MassTransit.SagaException: Orders.Domain.Sagas.OrderState(4623b85e-…) Saga exception on receipt of
Shop133.Contracts.Events.PaymentCompleted: An existing saga instance was not found
```

Ése es el material de `4.5`: un pedido a mitad de proceso cuando el servicio muere se queda sin nadie que lo mueva, y ahora al menos deja constancia.

### 8. Estado final limpio

Purgada `order-state_error` y lanzado un último pedido con el código definitivo (`.Publish` corto, `Fault` explícito, guardas restauradas):

```
Pedido b44627e0-…: cobro aceptado por 458 …; pasa a Confirmed y se publica OrderConfirmed.

== COLAS ==
order-created                  messages=0
order-created_error            messages=0
order-state                    messages=0
order-state_error              messages=0
stock-reserved                 messages=0
```

---

## Pendiente

- **`4.3`** — `StockRejected → Cancelled` y `PaymentFailed → CompensatingStock → Cancelled`, `OrderCancelled`, y con ellos `Order.Confirm()`/`Order.Cancel()`, el **primer consumer de Orders.API** y la tabla `ProcessedMessages` en `OrdersDb` (decisión 2). Es el punto que quita la inconsistencia temporal de la verificación 5.
- **`4.4`** — `ReleaseStock`, y con él la decisión de si conserva sus `Lines`.
- **`4.5`** — la tabla de la instancia, su token de concurrencia optimista, si comparte `OrdersDbContext`, si `Confirmed` pasa a `Finalize()` (decisión 5) y el outbox transaccional. **Y hay que releer el `OnMissingInstance` de la decisión 3**: con la saga persistida deja de dispararse por reinicios y pasa a señalar otra cosa.
- **`4.6`** — Notifications.API consume `OrderConfirmed`, que desde hoy ya se publica y hoy no lo enlaza nadie.
- **`4.7`** — los cuatro escenarios con el harness. Este punto se verificó **a mano**, como `3.4`, `3.5` y `4.1`; para automatizarlo, `OrdersApiFactory` tendrá que dejar de desmontar la saga junto con el resto de MassTransit.
- **`4.9` obliga a releer la decisión 1**: `PricingPending` entra *antes* de `StockPending` y con él un `During` más — y, siguiendo la regla del final de esa decisión, sin resucitar `Submitted`.
- **`ReserveStock` sigue sin usar** por la decisión 2 de `4.1`. Conviene decirlo en el documento que cierre la fase.
- **Sin dueño** — la concurrencia optimista sobre `StockItem` que anotó `3.4`, y que ninguna reserva confirmada baje nunca `QuantityOnHand`.
- **Entorno** — los 2 avisos `xUnit1051` de `CreateOrderTests.cs` vienen de `3.7` y siguen ahí.
