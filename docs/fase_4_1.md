# Fase 4.1 — `OrderStateMachine` en Orders.Domain con MassTransit Saga

**Fecha:** 2026-09-01 · **Estado:** completado · **Roadmap:** [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md)

---

## Objetivo

La Fase 3 dejó la coreografía entera y **rota por el final**: `POST /orders` → `OrderCreated` → `StockReserved` → `PaymentCompleted`/`PaymentFailed`, cuatro servicios, tres saltos, cero HTTP entre ellos. Pero nadie consume los dos últimos eventos. La verificación 6 de [fase_3_5.md](fase_3_5.md) lo midió: un cobro rechazado deja `QuantityReserved = 4` y la reserva viva para siempre, y los exchanges de `PaymentCompleted`/`PaymentFailed` tienen cero colas enlazadas — se publican al vacío, sin fallo y sin aviso. Es la regla 7 de CLAUDE.md incumplida a sabiendas.

La Fase 4 es quien lo cierra y este punto es su primera piedra: **que la saga exista y correlacione**. Nada más. El pedido sigue naciendo `Pending`, `Order.Status` no se mueve, y de la coreografía de la Fase 3 no se toca ni una línea.

Lo que sí entrega es la línea que [fase_0_3.md](fase_0_3.md) prometió en su decisión 5 y que llevaba desde entonces escrita sin ejecutar:

```csharp
Event(() => OrderCreated, e => e.CorrelateById(message => message.Message.OrderId));
```

Sin ella no hay correlación, porque ningún contrato lleva `CorrelationId` — se descartó a propósito para no tener dos fuentes de verdad al lado de un `OrderId` que siempre valdría lo mismo. `3.3` y `3.4` confirmaron contra el broker real que el sobre viaja con `correlationId: null`. Esa deuda se paga aquí.

**Fuera de alcance deliberadamente:**

| Queda fuera | Entra en |
|---|---|
| El resto de la cadena feliz (`StockReserved → PaymentPending → Confirmed`) y sus declaraciones de evento | `4.2` |
| Los caminos de error (`StockRejected → Cancelled`, `PaymentFailed → CompensatingStock → Cancelled`) | `4.3` |
| Publicar `ReleaseStock` y soltar el stock que un cobro rechazado deja reservado | `4.4` |
| Persistir la instancia de saga en `OrdersDb`, su token de concurrencia optimista y el outbox transaccional | `4.5` |
| Publicar `OrderConfirmed`/`OrderCancelled` y consumirlos | `4.3` / `4.6` |
| Mover `Order.Status` (`Confirm()`, `Cancel()`) | `4.2` / `4.3` |
| Los cuatro escenarios obligatorios contra la máquina de estados | `4.7` |
| El estado `PricingPending` y la validación del importe | `4.8` / `4.9` |

---

## Decisiones

### 1. El alcance es "esqueleto + primera transición", no la máquina entera

El roadmap parte en tres lo que es una sola clase: `4.1` la crea, `4.2` pone los estados y `4.3` el camino de error. Hay que elegir dónde cortar.

**Elegido:** un solo evento (`OrderCreated`), un solo estado (`StockPending`) y un solo `Initially` que transiciona. Es lo mínimo que se puede **verificar**: se crea una instancia, se correlaciona por `OrderId`, se ve en el log y se ve la cola en el broker.

**Descartado — entregar ya la cadena feliz completa.** Dejaría a `4.2` como un punto sin código, solo documentación de una lista de estados que ya existiría. Y el roadmap avisa de que `4.9` obliga a releer la lista de `4.2` para meterle `PricingPending` delante: escribirla entera hoy es escribirla dos veces.

**Descartado — solo el andamio, sin ninguna transición.** Es lo más literal al título del punto y es lo que no sirve: una saga que no reacciona a nada no crea instancia ninguna, así que no hay forma de comprobar que la correlación funciona. Un punto que no se puede verificar es un punto que no se puede cerrar.

### 2. La saga **observa** la coreografía; no la orquesta

Ésta es la decisión de diseño de toda la Fase 4 y conviene dejarla escrita antes de que `4.2` la dé por supuesta.

**Elegido:** la saga consume los mismos eventos que ya vuelan desde la Fase 3 y solo **emite** el comando de compensación (`4.4`) y los eventos terminales (`4.3`/`4.6`). Inventory sigue consumiendo `OrderCreated` por su cuenta y Payments `StockReserved`. Dos consumidores del mismo exchange fanout, no un relevo — se ve en la verificación 3: `Shop133.Contracts.Events:OrderCreated` tiene ahora dos bindings.

**Descartado — la orquestación pura**, en la que la saga manda `ReserveStock` a Inventory e Inventory deja de consumir `OrderCreated`. Es la forma canónica de una saga de orquestación y usaría el comando que `0.3` creó. Se descarta por tres motivos, y ninguno es que sea peor diseño:

1. Obliga a cambiar el consumer de **otro servicio**, y la Fase 4 del roadmap no contempla tocar Inventory.
2. Contradice dos decisiones ya escritas: la decisión 2 de [fase_3_2.md](fase_3_2.md) ("Payments consume `StockReserved` en ambas fases", que es por lo que `StockReserved` ganó su campo `Amount`) y la decisión 6 de [fase_3_4.md](fase_3_4.md), que cerró la pregunta de `StockLine` apoyándose precisamente en que Inventory consume el evento y no el comando.
3. La decisión 1 de `0.3` fijó los 9 mensajes "para que la saga de la Fase 4 no tenga que tocar Contracts". Reordenar quién consume qué es más caro que eso.

**El precio se dice en voz alta: `ReserveStock` se queda sin usar.** De los nueve mensajes de `0.3`, uno es ya material de lectura. Si `4.4` decide además que `ReleaseStock` no necesita sus `Lines` —cosa plausible, porque la PK de `StockReservations` *es* el `OrderId`—, conviene revisar si el noveno también sobra.

### 3. La idempotencia de la saga es su propio estado, y la guarda es explícita

CLAUDE.md deja este punto en la lista de "cada consumer nuevo tiene que acordarse de la guarda de `3.6`". La saga es el caso donde esa guarda **no aplica y hay algo mejor**.

**Descartado — la tabla `ProcessedMessages` de `3.6`.** No hay dónde ponerla: la saga no tiene `DbContext` hasta `4.5`, y su virtud entera —que la marca entra en el mismo `SaveChangesAsync` que el trabajo— desaparece cuando el "trabajo" es una transición de estado que gestiona MassTransit. Meterla aquí sería copiar la forma sin el motivo.

**Elegido:** una línea.

```csharp
During(StockPending, Ignore(OrderCreated));
```

El estado ya distingue el duplicado — un segundo `OrderCreated` del mismo pedido llega a una instancia que ya está en `StockPending`.

**Pero explícita, no por defecto, y eso es lo que hace falta medir.** El comportamiento de MassTransit ante un evento no aceptado en el estado actual es **faultear**, y un consumer que revienta ante un duplicado no es idempotente. Se comprobó quitando la línea (verificación 6):

```
MassTransit.NotAcceptedStateMachineException: Orders.Domain.Sagas.OrderState(2aab009d-…) Saga exception
on receipt of Shop133.Contracts.Events.OrderCreated: Not accepted in state StockPending
 ---> MassTransit.UnhandledEventException: The OrderCreated event is not handled during the StockPending
      state for the OrderStateMachine state machine
```

Con la línea puesta el duplicado se descarta en silencio y `order-state_error` **ni siquiera existe**. Sin ella aparece la cola con el mensaje dentro. Ése es el contraste; sin él el "test" no prueba nada, que es el aviso de `3.2` sobre los filtros que nunca enganchan.

Alcance de esta guarda, para no confundirla con la de `3.6`: reconoce el mismo **pedido**, no la misma **entrega**. Es la mitad de negocio. Aquí coinciden, porque una redelivery de RabbitMQ trae el mismo `OrderId`; en cuanto `4.2` añada eventos que puedan llegar dos veces con distinta procedencia, habrá que releerlo.

### 4. `InMemoryRepository()`, y el agujero que deja es el argumento de `4.5`

La persistencia está pre-asignada a `4.5` en tres documentos ([fase_2_2.md](fase_2_2.md), [fase_3_5.md](fase_3_5.md), [fase_3_6.md](fase_3_6.md)), con tres preguntas abiertas: la tabla, el token de concurrencia optimista y si comparte `OrdersDbContext`. Gastarlas aquí sería adelantarlas mal.

**Elegido:** repositorio en memoria, **y medir lo que se pierde** en vez de dejarlo como nota. Verificación 7: se reinicia Orders.API, se reenvía el mismo `OrderCreated` y la saga **arranca de cero** — no reconoce el pedido que estaba esperando. Un pedido a mitad de saga cuando el proceso muere se queda sin nadie que lo mueva, y nadie se entera.

Eso no es un descuido de este punto: es exactamente el material que justifica `4.5`, y ahora está medido en vez de supuesto.

### 5. `OrderState` no es `Order`, y sus setters son públicos a propósito

`Order` vive en `Entities/`, se persiste en `OrdersDb.Orders` desde `2.2` y su `Status` es lo que el cliente ve. `OrderState` es el estado del **proceso** que coordina a Inventory y a Payments. La decisión 2 de [fase_2_1.md](fase_2_1.md) ya lo había dejado escrito: `StockPending`, `PaymentPending` y `CompensatingStock` "son estados de la *instancia de saga*, no del pedido, y van en el tipo que persiste 4.5". Éste es ese tipo.

De ahí una incoherencia de estilo que es deliberada: `Order`, `OrderItem`, `Product`, `StockItem` y `Payment` son agregados que se construyen válidos, con setters privados y guardas en el constructor. `OrderState` tiene **setters públicos y ningún constructor**. MassTransit materializa la instancia y la muta desde fuera (los `.Then` escriben sobre `context.Saga`); un agregado que se defiende no encaja ahí. Lo que protege su coherencia no son los setters: es que solo la `OrderStateMachine` lo toca.

`CurrentState` es **`string` y no `int`**, que es la otra forma que admite `InstanceState`. El `int` ahorra espacio y obliga a declarar los estados en un orden intocable — el mismo peligro de renumeración que documenta el enum `OrderStatus` de `2.1`, pero sin la ventaja de tener los números escritos a mano. Con `string`, la tabla que cree `4.5` se lee sin descifrar nada.

### 6. `CustomerEmail` se captura ahora porque después ya no pasa por delante

Es el único campo de negocio que `OrderState` guarda, y no es especulación: `OrderCancelled` tiene que llevar el email (Notifications.API no puede leer `OrdersDb` — regla 1, decisión 3 de `0.3`), y **ni `StockRejected` ni `PaymentFailed` lo traen**. Si no se copia en el `Initially`, en `4.3` la saga no tiene a quién avisar de la cancelación.

Fuera quedan, y por motivos distintos:

- **El importe.** `PaymentCompleted` ya trae su `Amount`; guardarlo sería un segundo sitio con el mismo número, el argumento por el que `Order.Total` se calcula y no se persiste.
- **Las líneas del pedido.** Las necesitaría `ReleaseStock`… si conserva sus `Lines`. La decisión 6 de [fase_3_4.md](fase_3_4.md) dejó eso abierto a `4.4` al observar que la PK de `StockReservations` *es* el `OrderId`. Guardarlas hoy sería decidir `4.4` desde aquí, sin el consumer delante.
- **Un token de concurrencia optimista.** No significa nada sin repositorio persistente: es `4.5`.

### 7. Los otros cuatro eventos no se declaran todavía

Podrían declararse ya con su `CorrelateById`, dejando los `During(...)` para `4.2`/`4.3`. Se descarta: declarar un `Event<T>` hace que `ConfigureEndpoints` **enlace su exchange a la cola de la saga**, y sin un `During` que lo atienda cada mensaje acabaría en `order-state_error`. Se ganaría una topología completa a cambio de una cola de error llenándose sola.

Se declaran cuando haya un estado que los reciba. Mientras tanto la topología es honesta: un binding nuevo, cero faults — comprobado en la verificación 3.

### 8. `ILogger<T>` inyectado, no `LogContext`

La transición tiene que ser **observable**, o la verificación 5 no puede afirmar nada: MassTransit no registra el cambio de estado a nivel `Information`.

**Descartado — `LogContext.Info?.Log(...)`,** que es el modismo de MassTransit y no necesitaría constructor. Se prefiere el `ILogger<OrderStateMachine>` inyectado porque es lo que ya hacen los dos consumers del proyecto (`OrderCreatedConsumer`, `StockReservedConsumer`), y tener dos formas de registrar la misma clase de suceso obliga a explicar la diferencia cada vez. Funciona porque `AddSagaStateMachine` registra la máquina de estados como singleton del contenedor, así que se resuelve por constructor como cualquier otra cosa.

### 9. El paquete es `MassTransit` (core) y no `MassTransit.Abstractions`

`Orders.Domain` tenía **cero `PackageReference`** hasta hoy. Antes de añadir nada se miró qué hace falta de verdad, en el caché de NuGet:

```
== abstractions ==            == core ==
T:MassTransit.SagaStateMachineInstance     T:MassTransit.MassTransitStateMachine`1
T:MassTransit.State`1
```

`SagaStateMachineInstance` está en las abstracciones, pero `MassTransitStateMachine<T>` no. Referenciar solo `MassTransit.Abstractions` —que sería la opción "más limpia" para una capa de dominio— **no compila**. Entra el core.

Eso **no rompe la regla 5 de CLAUDE.md, la cumple**: la excepción que permite a `Orders.Domain` referenciar `Shop133.Contracts` existe justamente "porque las saga state machines viven en Orders.Domain", y el comentario de `OrdersDomain_ProjectReferences_ContainOnlyContracts` lo lleva anticipando desde `0.6`. Ese test solo mira `ProjectReference`, que sigue siendo una.

Versión **explícita y clavada a la de las tres `.API`**, `8.5.10`. Dejarla al criterio de `dotnet add package` instalaría hoy la 9.2.0, que tiene licencia comercial; `MassTransitPackages_StayOnMajorVersion8` cuenta un `Version` vacío como violación precisamente para eso, y ahora vigila también este `.csproj`.

### 10. La primera divergencia real del bloque `AddMassTransit` llega un punto antes de lo previsto

`3.1` aplazó la extracción del bloque duplicado. `3.4` y `3.5` la releyeron con el diff delante y la dejaron: lo único que divergía era la línea `AddConsumer`, que es justo lo que no se puede compartir. `3.5` dejó escrito, en el `Program.cs` de Payments, que **la próxima relectura sería `4.5`** con el outbox, "y esta vez con una divergencia real".

Llega aquí. `x.AddSagaStateMachine<OrderStateMachine, OrderState>().InMemoryRepository()` es una diferencia estructural que Inventory y Payments no van a tener nunca, no una línea de más.

**La conclusión no cambia:** extraer la mitad idéntica dejaría fuera precisamente lo que distingue a cada servicio, que es lo que uno quiere leer al abrir el archivo. La cita de `3.5` se corrige de fecha, no de contenido.

### 11. Una regla de arquitectura nueva. La suite pasa de 15 a 16

La regla 5 dice que la excepción de `Orders.Domain` existe "because saga state machines live in Orders.Domain". Hasta hoy eso era **solo prosa**: ninguna regla miraba dónde estaba una máquina de estados, porque no había ninguna.

`StateMachineFiles_LiveOnlyIn_OrdersDomain` escanea `src/**/*StateMachine.cs` y exige `src/Services/Orders/Orders.Domain/…`. Merece test por lo mismo que lo merecía el sitio de un consumer en `3.4`: mover la saga a `Orders.Infrastructure` compilaría sin una queja y dejaría al proyecto de dominio sin la única razón por la que existe.

Va en `ServiceBoundaryRulesTests.cs` y no en un fichero nuevo — precedente de `3.4`; `3.1` separó `PackageRulesTests` porque una regla de licencia no es una regla de capas, no porque cada regla merezca archivo.

Es **más estrecha** que sus dos hermanas a propósito: dice "en `Orders.Domain`", en singular, no "en el `.Domain` de su servicio". Hoy solo hay una saga y solo Orders tiene capa de dominio; una regla que ya contemplara servicios inexistentes sería un filtro que nunca engancha. Si aparece una segunda saga, se ensancha entonces.

Y se rompió a propósito antes de darla por buena — verificación 2.

---

## Cambios

### Nuevos — la saga

| Archivo | Rol |
|---|---|
| [Orders.Domain/Sagas/OrderState.cs](../src/Services/Orders/Orders.Domain/Sagas/OrderState.cs) | La instancia de saga: `CorrelationId` (que *es* el `OrderId`), `CurrentState`, `CustomerEmail`, `CreatedAt`. |
| [Orders.Domain/Sagas/OrderStateMachine.cs](../src/Services/Orders/Orders.Domain/Sagas/OrderStateMachine.cs) | La máquina de estados: correlación, `Initially → StockPending` y la guarda de idempotencia. |

### Modificados

| Archivo | Qué cambió |
|---|---|
| [Orders.Domain/Orders.Domain.csproj](../src/Services/Orders/Orders.Domain/Orders.Domain.csproj) | Primer `PackageReference` del proyecto: `MassTransit` `8.5.10`. |
| [Orders.API/Program.cs](../src/Services/Orders/Orders.API/Program.cs) | `using Orders.Domain.Sagas;` y, dentro de `AddMassTransit`, `AddSagaStateMachine<OrderStateMachine, OrderState>().InMemoryRepository()`. Reescrito el comentario de `ConfigureEndpoints`, que decía "hoy no registra nada: no hay consumers". |
| [Shop133.ArchitectureTests/ServiceBoundaryRulesTests.cs](../tests/Shop133.ArchitectureTests/ServiceBoundaryRulesTests.cs) | Regla `StateMachineFiles_LiveOnlyIn_OrdersDomain` + su helper `IsInsideOrdersDomain`. Suite 15 → **16**. |

### Paquetes NuGet nuevos

| Paquete | Versión | Licencia | Dónde |
|---|---|---|---|
| `MassTransit` | 8.5.10 | Apache-2.0 | `Orders.Domain` |

### Lo que no se tocó

- **`Shop133.Contracts`.** Ni un campo. Era el objetivo de la decisión 1 de `0.3` al fijar los 9 mensajes, y se cumple.
- **`Order`, `OrderItem`, `OrderStatus`.** El pedido sigue naciendo `Pending` y nada lo mueve. `Confirm()`/`Cancel()` son `4.2`/`4.3`.
- **`OrdersDbContext`, `OrderConfiguration` y las migraciones.** La saga no toca la base: `4.5`.
- **`OrdersController`.** Publica `OrderCreated` igual que desde `3.3`.
- **Inventory y Payments**, sus consumers y sus `Program.cs`. Es la decisión 2.
- **`OrdersApiFactory`.** No se añadió ninguna clave de configuración, así que no hace falta un `UseSetting` nuevo — la regla de `3.1` ("cada guarda nueva en un `Program.cs` es una línea nueva en la fábrica") se comprobó y esta vez no aplica.
- **`docker-compose*.yml`.** Orders.API sigue sin contenedor.

---

## Detalles que cuestan tiempo

**`Ignore` recibe el evento, no una lambda.** `Event(() => OrderCreated, …)` y `During(StockPending, Ignore(OrderCreated))` conviven en el mismo constructor con formas distintas, y es fácil escribir `Ignore(() => OrderCreated)` por simetría. Las sobrecargas reales son `Ignore(Event)` y `Ignore(Event<T>)`.

**La propiedad se puede llamar igual que el tipo del mensaje.** `public Event<OrderCreated> OrderCreated` compila: en posición de argumento de tipo el compilador solo busca tipos, así que `OrderCreated` resuelve al `record` de Contracts, y en `Ignore(OrderCreated)` resuelve a la propiedad. Es el modismo de MassTransit y parece un error hasta que se compila.

**El nombre de la cola sale del tipo de la *instancia*, no de la máquina de estados.** Con `SetKebabCaseEndpointNameFormatter` (fijado en `3.1` y que no se puede cambiar sin dejar colas huérfanas), `OrderState` → `order-state`. Si se buscaba `order-state-machine`, no está.

**La línea que prueba que la saga está enganchada es distinta de la de un consumer.** No es `Configured endpoint …, Consumer: …` sino:

```
Configured endpoint order-state, Saga: Orders.Domain.Sagas.OrderState, State Machine: Orders.Domain.Sagas.OrderStateMachine
```

Sin ella el `AddSagaStateMachine` no llegó a `ConfigureEndpoints` y **el mensaje se pierde en silencio**, que sigue siendo el fallo más caro de diagnosticar de esta parte del proyecto.

**`order-state_error` no existía antes de romper la guarda, y eso es la prueba, no un descuido.** Las colas `_error` se crean de forma perezosa en el primer fault. Aquí su ausencia *sí* significa algo —que ningún mensaje falló—, al revés que en `3.4`, donde su ausencia no probaba nada porque nunca había fallado nada. Después del experimento se purgó con `DELETE /api/queues/%2F/order-state_error/contents` (HTTP 204); la cola queda creada y vacía.

**El repost a mano ya necesitaba cuatro cosas y sigue necesitándolas todas.** JSON sin BOM, `content_type: application/vnd.masstransit+json`, `messageType` con el URN completo y `message_id` en `properties` (esto último desde `3.6`, y aquí hace falta aunque la saga no lo mire, porque el mismo fanout entrega a Inventory, que sí). `{"routed":true}` sigue significando que llegó a una cola, no que alguien pudiera leerlo.

**Un repost con `message_id` nuevo es lo que hay que usar para probar la guarda de la saga.** Con el `message_id` original, la guarda de transporte de Inventory se lo come y no se distingue quién descartó qué. Con uno nuevo el mensaje atraviesa `3.6` y llega al estado, que es lo que se quería medir.

**El directorio de trabajo de una PowerShell en segundo plano puede no ser el del repositorio.** `dotnet run --project src/Services/Orders/Orders.API` falló con `The provided file path does not exist` después de que una llamada previa hubiera cambiado de directorio. Con ruta absoluta arrancó a la primera. El mensaje culpa al proyecto, no al `cwd`.

**Smart App Control no dio guerra en este punto**, ni con el paquete nuevo ni con los ensamblados recién compilados. No es que esté arreglado: es que el bloqueo es por fichero y por reputación, así que no aparecer una vez no dice nada de la siguiente.

**`MassTransit` 8.5.10 sí trae target `net10.0`.** CLAUDE.md dice que la 8.x envía `net8.0`/`net9.0` y que correr sobre un host `net10.0` está bien; comprobado en el caché, `lib/` tiene `net10.0`, `net9.0`, `net8.0`, `net472` y `netstandard2.0`. La nota se corrige.

---

## Verificación

Ejecutado el 2026-09-01 contra la infraestructura de `docker compose` (SQL Server, RabbitMQ) y los tres servicios lanzados desde el IDE. Salidas reales.

### 1. Build

```
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:08.13
```

*(La compilación completa desde limpio emite además 2 avisos `xUnit1051` en `CreateOrderTests.cs`, anteriores a este punto y ajenos a él.)*

### 2. Tests de arquitectura — rotos a propósito primero

Con la suite en verde:

```
   Shop133.ArchitectureTests  Total: 16, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 1.456s
```

Se crea `src/Services/Orders/Orders.Infrastructure/ThrowawayStateMachine.cs` para comprobar que la regla nueva engancha de verdad:

```
    Shop133.ArchitectureTests.ServiceBoundaryRulesTests.StateMachineFiles_LiveOnlyIn_OrdersDomain [FAIL]
      Una máquina de estados de saga vive en Orders.Domain: es la razón por la que ese proyecto existe
      y por la que la regla 5 le permite referenciar Shop133.Contracts. Fuera de lugar:
      src/Services/Orders/Orders.Infrastructure/ThrowawayStateMachine.cs

   Shop133.ArchitectureTests  Total: 16, Errors: 0, Failed: 1, Skipped: 0, Not Run: 0, Time: 0.229s
```

Borrado el fichero de usar y tirar:

```
   Shop133.ArchitectureTests  Total: 16, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.248s
```

### 3. Topología: la cola de la saga y el segundo binding

Antes de arrancar Orders.API con la saga:

```
== COLAS ==
order-created                  messages=0
order-created_error            messages=0
stock-reserved                 messages=0
```

Traza de arranque:

```
Configured endpoint order-state, Saga: Orders.Domain.Sagas.OrderState, State Machine: Orders.Domain.Sagas.OrderStateMachine
Now listening on: http://localhost:5189
Bus started: rabbitmq://localhost/
```

Después:

```
== COLAS ==
order-created                  messages=0
order-created_error            messages=0
order-state                    messages=0
stock-reserved                 messages=0

== BINDINGS del exchange OrderCreated ==
MassTransit:Fault--Shop133.Contracts.Events:OrderCreated-- -> MassTransit:Fault
Shop133.Contracts.Events:OrderCreated         -> order-created
Shop133.Contracts.Events:OrderCreated         -> order-state
```

**Dos bindings sobre el mismo fanout**: `order-created` es Inventory desde `3.4`, `order-state` es la saga. Es la decisión 2 hecha visible.

### 4. `Orders.Tests` sigue en 12/12

`OrdersApiFactory` desmonta todo descriptor cuyo ensamblado empiece por `MassTransit` y monta el harness pelado. El singleton de `OrderStateMachine` vive en el ensamblado `Orders.Domain` y **sobrevive al desmontaje**, huérfano y sin que nadie lo resuelva. No molesta:

```
   Orders.Tests  Total: 12, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 67.377s
```

### 5. La correlación funciona y la coreografía de la Fase 3 sigue intacta

```
POST /orders  {"customerEmail":"saga41@shop133.test","items":[{"productId":2,"quantity":2,
               "productSku":"TAZA-002","productName":"Taza Ancla","unitPrice":129.00}]}

{"id":"2aab009d-7cb3-4cc9-bc4c-3be17612130a","customerEmail":"saga41@shop133.test",
 "status":"Pending","createdAt":"2026-09-01T02:58:42.9675578+00:00","total":258.00, …}
```

Los tres servicios, en el mismo pedido:

```
info: Orders.Domain.Sagas.OrderStateMachine[0]
      Saga arrancada para el pedido 2aab009d-7cb3-4cc9-bc4c-3be17612130a de saga41@shop133.test; pasa a StockPending.

info: Inventory.API.Consumers.OrderCreatedConsumer[0]
      Stock reservado para el pedido 2aab009d-7cb3-4cc9-bc4c-3be17612130a: 1 línea(s) por un importe de 258.00.

info: Payments.API.Consumers.StockReservedConsumer[0]
      Cobro aceptado para el pedido 2aab009d-7cb3-4cc9-bc4c-3be17612130a por 258.00, transacción SIM-3798C9A88CB24EF1BA33F7042FFA3B48.
```

El `status` de la respuesta sigue siendo `Pending`: la saga arranca **al lado** del pedido, no lo mueve. Eso es `4.2`.

### 6. Idempotencia (regla 6), con el contraste

Reenvío a mano del mismo `OrderCreated` con **`message_id` nuevo** (`3e2b3169-…`), para que atraviese la guarda de transporte de `3.6` y llegue de verdad al estado:

```
{"routed":true}

== COLAS ==
order-created                  messages=0
order-created_error            messages=0
order-state                    messages=0
stock-reserved                 messages=0
```

**No existe `order-state_error`.** Y en el log de Orders:

```
Líneas 'Saga arrancada': 1
```

Una sola, con dos entregas. Ahora **con la línea `During(StockPending, Ignore(OrderCreated))` comentada**, mismo reenvío:

```
== COLAS ==
order-state                    messages=0
order-state_error              messages=1
```

```
MassTransit.NotAcceptedStateMachineException: Orders.Domain.Sagas.OrderState(2aab009d-7cb3-4cc9-bc4c-3be17612130a)
Saga exception on receipt of Shop133.Contracts.Events.OrderCreated: Not accepted in state StockPending
 ---> MassTransit.UnhandledEventException: The OrderCreated event is not handled during the StockPending
      state for the OrderStateMachine state machine
   at MassTransit.MassTransitStateMachine`1.DefaultUnhandledEventCallback(UnhandledEventContext`1 context)
```

Restaurada la línea. Sin este segundo bloque, el primero no demuestra que la guarda hiciera nada.

### 7. El estado se pierde al reiniciar — el argumento de `4.5`

El reinicio de Orders.API para el experimento anterior lo dejó medido de paso. Con la instancia del pedido `2aab009d-…` viva en `StockPending`, se para el servicio, se arranca de nuevo y se reenvía el mismo `OrderCreated`:

```
Líneas 'Saga arrancada' en el proceso nuevo: 1
      Saga arrancada para el pedido 2aab009d-7cb3-4cc9-bc4c-3be17612130a de saga41@shop133.test; pasa a StockPending.
```

La saga **no reconoce el pedido**: arranca de cero como si fuera nuevo. `InMemoryRepository` no sobrevive al proceso, así que un pedido a mitad de saga cuando el servicio muere se queda sin nadie que lo mueva y sin rastro. Eso es lo que cierra `4.5`.

---

## Pendiente

- **`4.2`** — el resto de la cadena (`StockReserved → PaymentPending → Confirmed`) y las declaraciones de los cuatro eventos que hoy faltan, con la decisión 7 delante. **Y hay que releerla contra `4.9`**: la lista de estados del roadmap no contempla `PricingPending`, que va *antes* de `StockPending`.
- **`4.3`** — `StockRejected → Cancelled` y `PaymentFailed → CompensatingStock → Cancelled`, más el `Order.Status` que sigue clavado en `Pending`. Al añadir eventos que pueden llegar por caminos distintos habrá que releer el alcance de la guarda de la decisión 3.
- **`4.4`** — `ReleaseStock`, y con él la decisión de si conserva sus `Lines`. `OrderState` no las guarda hoy precisamente para no decidirlo desde aquí.
- **`4.5`** — la tabla de la instancia de saga, su token de concurrencia optimista, si comparte `OrdersDbContext`, cómo se indexa la PK `uniqueidentifier` y el outbox transaccional que cierra el agujero de la doble escritura de `3.3`. El agujero de la decisión 4 está medido en la verificación 7.
- **`4.6`** — Notifications.API consume `OrderConfirmed`/`OrderCancelled`, que todavía no publica nadie.
- **`4.7`** — los cuatro escenarios obligatorios con el harness. Este punto se verificó **a mano**, como `3.4` y `3.5`; automatizarlo es del punto de test, y para entonces `OrdersApiFactory` tendrá que dejar de desmontar la saga junto con el resto de MassTransit.
- **`ReserveStock` se ha quedado sin usar** por la decisión 2, y podría acompañarlo `ReleaseStock.Lines` según lo que decida `4.4`. Conviene decirlo en el documento que cierre la fase en vez de dejar dos de los nueve mensajes de `0.3` sin explicación.
- **Sin dueño** — la concurrencia optimista sobre `StockItem` que anotó `3.4` sigue sin punto asignado, y ninguna reserva confirmada baja nunca `QuantityOnHand`.
- **Entorno** — los 2 avisos `xUnit1051` de `CreateOrderTests.cs` vienen de `3.7` y siguen ahí; no son de este punto pero rompen el "0 Warning(s)" de una compilación desde limpio.
