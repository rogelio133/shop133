# Fase 4.3 — Los caminos de error de la saga y los dos primeros consumers de Orders.API

**Fecha:** 2026-09-02 · **Estado:** completado · **Roadmap:** [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md)

---

## Objetivo

`4.2` cerró la cadena feliz de punta a punta, pero dejó la saga con **un solo desenlace posible**. Los dos caminos de error se quedaban colgados, y con ellos dos agujeros ya medidos:

1. **`StockRejected` y `PaymentFailed` se publicaban al vacío.** Sus exchanges existían con **cero colas enlazadas** desde `3.4` y `3.5` — la verificación 6 de [fase_3_5.md](fase_3_5.md) lo dejó escrito. Un pedido sin stock, o con el cobro rechazado, se quedaba en `StockPending`/`PaymentPending` **para siempre**, sin fallo y sin aviso.
2. **`Order.Status` seguía clavado en `Pending`** aunque la saga llegara a `Confirmed`. La verificación 5 de [fase_4_2.md](fase_4_2.md) lo midió: la saga terminada y `GET /orders/{id}` contestando `"Pending"`, no durante un instante sino indefinidamente.

Este punto cierra los dos. La saga pasa a tener tres finales:

```
OrderCreated → StockPending ─┬─ StockReserved → PaymentPending ─┬─ PaymentCompleted → Confirmed ⇒ Publish(OrderConfirmed)
                             │                                  └─ PaymentFailed    → Cancelled ⇒ Publish(OrderCancelled)
                             └─ StockRejected  → Cancelled ⇒ Publish(OrderCancelled)
```

Y **Orders.API estrena sus dos primeros consumers**, que es lo que por fin mueve el pedido en `OrdersDb`. El rodeo es obligatorio y conviene entenderlo: la `OrderStateMachine` vive en `Orders.Domain`, que **no ve `OrdersDbContext`** — la flecha va `.API → .Infrastructure → .Domain` (regla 5). Así que entre "la saga terminó" y "la fila cambió" hay forzosamente un mensaje y una cola. Con ellos llega también a `OrdersDb` la tabla `ProcessedMessages` de `3.6`.

**Fuera de alcance deliberadamente:**

| Queda fuera | Entra en |
|---|---|
| `CompensatingStock` (ver decisión 1) | `4.4`, si es que llega a existir |
| Publicar `ReleaseStock` y soltar el stock de un cobro rechazado | `4.4` |
| Persistir la instancia de saga, el token de concurrencia optimista y el outbox | `4.5` |
| Notifications.API consumiendo `OrderConfirmed`/`OrderCancelled` | `4.6` |
| Los cuatro escenarios obligatorios con el harness | `4.7` |
| `PricingPending` delante de `StockPending` | `4.8` / `4.9` |
| Guardar en el pedido **por qué** se canceló | Sin dueño — ver decisión 4 |

---

## Decisiones

### 1. `CompensatingStock` no llega a existir, y es la misma regla que dejó fuera a `Submitted`

El punto se titula `StockRejected → Cancelled / PaymentFailed → CompensatingStock → Cancelled`. De esos tres estados **solo entra `Cancelled`**.

La regla es la que dejó escrita la decisión 1 de [fase_4_2.md](fase_4_2.md): *en una saga que observa, hay un estado por cada **respuesta que se espera**, no por cada hecho que ocurre.* Y aquí no hay ninguna respuesta que esperar:

- La saga **todavía no manda `ReleaseStock`** — eso es `4.4`.
- Y aunque lo mandara hoy, **`Shop133.Contracts` no tiene ningún `StockReleased`** con el que Inventory pudiera contestar. Los 9 mensajes de `0.3` incluyen el comando, no su acuse.

Sin espera, `CompensatingStock` se entraría y se saldría en la misma transición: un estado que ninguna instancia puede tener al consultarla, exactamente lo que `4.2` rechazó de `Submitted` y `StockReserved`. Es la segunda vez que la misma regla recorta el mismo tipo de estado, lo cual es señal de que la regla es buena y no una excusa a posteriori.

**Descartado declararlo igualmente como estado de paso**, dejando el hueco listo para que `4.4` meta ahí su `Publish`. Sería inventar la firma antes de tener el caso de uso — el mismo error que `1.1` evitó dejando a `Product` sin `Update()`, y que `2.1` repitió dejando a `Order` sin `Confirm()` hasta hoy.

**Descartado añadir `StockReleased` a los contratos** para convertir la espera en real. Puede que sea la respuesta correcta, pero es una decisión de `4.4` —con el consumer de la compensación delante— y rompería los 9 mensajes de `0.3` sin que este punto lo necesite. El precedente aplicable es `3.2`: un contrato se revisa cuando aparece el consumidor que lo necesita.

Lo que sí distingue a los dos caminos de error **no es el estado, es lo que queda por deshacer**: desde `StockPending` no hay nada reservado; desde `PaymentPending`, sí. Hoy los dos llegan igual a `Cancelled` y el segundo **deja el stock reservado** — está medido en la verificación 4.

### 2. Un solo `Cancelled` para los dos caminos

Dos estados terminales de error (`StockRejectedFinal` / `PaymentDeclinedFinal`) darían un diagnóstico más fino al mirar la instancia. Se descarta: el desenlace del pedido es el mismo —terminó sin completarse— y quien quiera saber por qué lo lee en el `Reason` que viaja dentro de `OrderCancelled`. Es la misma decisión que ya tomó `2.1` con `OrderStatus`, que tiene un único `Cancelled` con el motivo fuera.

El coste de la alternativa es concreto y no teórico: **cada estado terminal necesita sus cinco guardas de idempotencia** (ver decisión 3), así que un segundo `Cancelled` serían cinco `Ignore` más para no ganar ni una transición distinta.

### 3. Las guardas de idempotencia: dónde van las nuevas y, sobre todo, dónde **no**

`4.1` estrenó una `Ignore`, `4.2` las subió a seis. Aquí llegan a **quince**, repartidas por cuatro `During`. La regla es la de `4.2` (*en cada estado se ignoran los eventos que ya se atendieron antes de llegar a él*), y lo que hay que dejar escrito es lo que **no** se ignora:

| Estado | Se ignora | Y deliberadamente **no** |
|---|---|---|
| `StockPending` | `OrderCreated` | `PaymentCompleted`, `PaymentFailed` — un cobro resuelto sin haber visto la reserva no es un duplicado, es una **entrega fuera de orden**. Ignorarlo dejaría el pedido esperando para siempre una respuesta que ya pasó; faultear lo pone en `order-state_error`, donde se ve. |
| `PaymentPending` | `OrderCreated`, `StockReserved` | `StockRejected` — llegar aquí implica haber recibido `StockReserved`, e Inventory publica **uno de los dos, nunca los dos**. Un `StockRejected` en este estado no es un duplicado: es Inventory contradiciéndose, y eso hay que verlo. |
| `Confirmed` | los **cinco** | — |
| `Cancelled` | los **cinco** | — |

Los dos terminales llevan los cinco y no los tres del camino recorrido: llegar a `Confirmed` descarta que `StockRejected` haya sido parte de la historia de ese pedido, pero **no impide que llegue tarde o reacuñado a mano**, y el resultado sería un pedido perfecto en la cola de error. `During(Cancelled, …)` es además el más fácil de olvidar de los cuatro, porque se llega a él por dos caminos y ninguno pasa por ahí al escribirlo. La verificación 6 lo comprueba en vez de suponerlo.

### 4. `Order.Confirm()` y `Order.Cancel()`: solo se sale de `Pending`, y el duplicado **no se distingue aquí**

Los dos métodos pasan por un `TransitionTo` privado que exige que el estado actual sea `Pending`. Cualquier otra transición lanza `InvalidOperationException`, **incluida la que va a donde ya se está**.

Eso último parece hostil y es lo que hace útil la guarda. Recibir dos veces el mismo `OrderConfirmed` es normal —RabbitMQ entrega al menos una vez— y quien lo reconoce es el consumer, que comprueba el estado y sale antes de llegar a la entidad. Si la excepción salta, es que la guarda del consumer falló, o que la saga y `OrdersDb` no cuentan la misma historia (un `Confirm()` sobre un pedido cancelado). **Dejar pasar la transición a sí mismo mezclaría el duplicado legítimo con el fallo real**, y el segundo dejaría de verse.

`Cancel()` **no recibe el motivo**, aunque `OrderCancelled` lo traiga: el pedido no distingue por qué se canceló (`///` de `OrderStatus.Cancelled`, escrito en `2.1`), y guardarlo sería una columna, una migración y un texto duplicado del que ya viaja hacia Notifications. Queda sin dueño hasta que la interfaz de la Fase 6 tenga que enseñárselo al cliente.

### 5. La `ProcessedMessage` de Orders vive en `.Infrastructure`, no en `Orders.Domain`

Es la primera entidad de Orders que no está en `Orders.Domain/Entities/`, donde `2.1` puso `Order` y `OrderItem`. La asimetría es deliberada: **esto no es negocio, es una constancia de transporte**. Un pedido existe aunque nadie use RabbitMQ; esta fila solo tiene sentido porque los mensajes se entregan al menos una vez. Meterla en el dominio obligaría a `Orders.Domain` —que solo referencia `Shop133.Contracts` y MassTransit— a conocer un problema de mensajería que no es suyo, y la pondría al lado de un agregado con invariantes cuando esto es una fila de bitácora.

Como efecto secundario coincide con donde ya estaba en Inventory y Payments, que no tienen proyecto de dominio: las tres copias ocupan el mismo sitio relativo.

**Es la tercera copia literal del par entidad + configuración** y sigue sin extraerse, por lo mismo que el bloque `AddMassTransit` (3.1, 3.4, 3.5): no hay dónde ponerla. Los `.Infrastructure` de Inventory y Payments tienen **cero `ProjectReference`** —ni siquiera a `Shop133.Contracts`— y no existe un proyecto de infraestructura común; crearlo para una clase de datos sería más estructura que ahorro.

**Y aquí la clave compuesta `(MessageId, ConsumerName)` deja de ser hipotética.** En `3.6` el argumento era un peligro futuro: Inventory y Payments tienen un consumer cada uno. Orders nace con **dos**, así que esta tabla es la primera del proyecto con dos `ConsumerName` distintos escribiendo en ella — se ve en la verificación 5.

### 6. Dos consumers, no uno con las dos interfaces

Un `OrderOutcomeConsumer : IConsumer<OrderConfirmed>, IConsumer<OrderCancelled>` compilaría, tendría **una** cola con los dos exchanges ligados y escribiría la guarda de transporte una sola vez. Se descarta por dos motivos:

- **La convención del proyecto nombra el consumer por el mensaje que consume** — `OrderCreatedConsumer`, `StockReservedConsumer`. Con dos mensajes en una clase, ni el nombre ni la cola dicen qué se escucha.
- **Dos colas separan los dos desenlaces**: un fallo procesando cancelaciones no atasca las confirmaciones, y en la UI del broker se ve por separado cuántos pedidos terminan de cada forma.

El precio, dicho en voz alta: la cabecera de guardas está duplicada casi línea por línea en los dos archivos. Son **dos copias, que no son un patrón** (precedente de `2.4`); si `4.6` o la Fase 6 traen un tercer consumer a Orders con la misma cabecera, la extracción se decide ahí con tres diffs delante.

### 7. Un desenlace de un pedido que no existe en `OrdersDb` **revienta**

El consumer no sale en silencio si el `SELECT` no encuentra el pedido: lanza y el mensaje acaba en `order-confirmed_error` / `order-cancelled_error`. Es coherente con el `OnMissingInstance(Fault())` de `4.2` y con el `throw` del `MessageId` ausente de `3.6`: **este servicio es el dueño de esa tabla y el evento lo publicó su propia saga**, así que no encontrar la fila es una incoherencia de verdad, no un caso normal. Hoy es alcanzable a mano (un evento reacuñado con un `OrderId` inventado) y, hasta `4.5`, también reiniciando el servicio a mitad de saga.

### 8. Ningún paquete, ningún proyecto, y por tanto **ninguna regla de arquitectura nueva**

La suite se queda en **16**. Todas las formas que introduce este punto ya están cubiertas: `ConsumerFiles_LiveOnlyIn_ServiceApiConsumersFolder` (3.4) vigila la ruta de los dos consumers nuevos, `StateMachineFiles_LiveOnlyIn_OrdersDomain` (4.1) la de la máquina de estados, y `EfCorePackages_LiveOnlyIn_InfrastructureProjects` la entidad nueva.

Se dice por escrito en vez de inventar una regla para subir el contador — precedente de `3.3` y `3.5`, y advertencia de `3.2`: un filtro que no coincide con nada pasa en verde para siempre.

---

## Cambios

### Saga

| Archivo | Rol |
|---|---|
| `src/Services/Orders/Orders.Domain/Sagas/OrderStateMachine.cs` | Estado `Cancelled`, eventos `StockRejected` y `PaymentFailed` con su `CorrelateById` + `OnMissingInstance`, las dos transiciones con su `Publish(OrderCancelled)`, y las guardas de los cuatro `During` |

### Dominio

| Archivo | Rol |
|---|---|
| `src/Services/Orders/Orders.Domain/Entities/Order.cs` | `Confirm()`, `Cancel()` y el `TransitionTo` privado que los guarda |

### Persistencia

| Archivo | Rol |
|---|---|
| `src/Services/Orders/Orders.Infrastructure/Entities/ProcessedMessage.cs` | **Nuevo** — y con él la carpeta `Entities/` en este proyecto |
| `src/Services/Orders/Orders.Infrastructure/Persistence/Configurations/ProcessedMessageConfiguration.cs` | **Nuevo** — PK compuesta, `ValueGeneratedNever()`, longitudes desde las constantes |
| `src/Services/Orders/Orders.Infrastructure/Persistence/OrdersDbContext.cs` | `DbSet<ProcessedMessage>` + su `ApplyConfiguration` |
| `src/Services/Orders/Orders.Infrastructure/Migrations/20260902021645_AddProcessedMessages.*` | **Nuevos** — generados |

### Mensajería

| Archivo | Rol |
|---|---|
| `src/Services/Orders/Orders.API/Consumers/OrderConfirmedConsumer.cs` | **Nuevo** — cola `order-confirmed` |
| `src/Services/Orders/Orders.API/Consumers/OrderCancelledConsumer.cs` | **Nuevo** — cola `order-cancelled` |
| `src/Services/Orders/Orders.API/Program.cs` | Dos `AddConsumer` dentro del `AddMassTransit` que ya existía |

**Ni un `.csproj` tocado, ni un paquete añadido, ni una línea de `Shop133.Contracts`.** Los dos eventos de error existen desde `0.3` y `OrderCancelled` también: `3.2` los revisó y los dejó tal cual. Este punto es el primero que los usa.

---

## Detalles que cuestan tiempo

### 1. Tras `dotnet ef migrations add`, hay que **volver a compilar** antes de correr los tests

`Orders.Tests` falló **12/12** justo después de crear la migración, todas en `MigrateAsync` con:

> `The model for context 'OrdersDbContext' has pending changes. Add a new migration before updating the database.`

El mensaje acusa de no haber creado la migración... que acababa de crearse. La causa es la ya anotada en CLAUDE.md desde `1.2` en su otra mitad: **`migrations add` compila antes de escribir los archivos**, así que el ensamblado que queda en `bin/` no contiene ni la migración nueva ni el snapshot actualizado. El test carga ese ensamblado viejo, compara el modelo con el snapshot viejo y ve diferencias.

`dotnet build` y otra vez: **12/12 en verde**. La regla, ampliada: después de `migrations add`, nada que cargue el ensamblado (`--no-build`, un test ya compilado) es de fiar hasta recompilar.

### 2. Smart App Control bloqueó `Orders.API.dll` — y el reintento bastó

`dotnet ef migrations add` falló con `Could not load file or assembly 'Orders.API.dll'. An Application Control policy has blocked this file. (0x800711C7)`, sobre un ensamblado recién construido por este mismo repositorio. **El primer reintento pasó**, sin necesidad del `dotnet build -c Release` que hizo falta en `3.5`.

Es el tercer comportamiento distinto del mismo bloqueo (en `1.7` bastó reintentar, en `3.5` no bastaron ocho reintentos, en `3.7` no bastó ni el Release). Confirma lo que ya decía CLAUDE.md: **reintentar primero y escalar después**, y nunca desactivar Smart App Control, que es irreversible.

### 3. `sqlcmd -P $env:VARIABLE` con la variable vacía se come el flag siguiente

Ya estaba anotado en `3.6` y volvió a morder exactamente igual: `-P $env:MSSQL_SA_PASSWORD` sin la variable definida en esa sesión hace que `-C` pase a ser la contraseña, desaparece la confianza en el certificado y el error habla de un **certificado autofirmado**, no de una variable vacía. La contraseña se saca de `.env` con `Select-String`.

### 4. Los nombres de columna de una colección *owned* no son los que uno escribiría

Al comprobar el stock reservado, `StockReservationLines.StockReservationOrderId` no existe: EF nombra la FK de una colección owned como **`OrderId`**, igual que la del dueño, y la PK compuesta es `(OrderId, Id)`. Tampoco existe `ReservedAt` — la columna es `CreatedAt`. Media consulta perdida por escribir de memoria en vez de mirar `sys.columns`.

### 5. Un solo reenvío a mano golpea la saga **dos veces**

Ya lo había anotado `4.2` y aquí es lo que hace útil la verificación 6: reenviar un `StockReserved` de un pedido ya cobrado llega también a Payments, cuya guarda de negocio de `3.5` republica el `PaymentFailed` guardado — así que el reenvío de **un** mensaje ejercita **dos** guardas terminales distintas de la saga. Conviene saberlo antes de contar mensajes en las colas.

---

## Verificación

### 1. Build y suite de arquitectura

```
Build succeeded.
    2 Warning(s)     ← los dos xUnit1051 de CreateOrderTests.cs, heredados de 3.7
    0 Error(s)

   Shop133.ArchitectureTests  Total: 16, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.782s
```

### 2. La migración, aplicada

```
INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260902021645_AddProcessedMessages', N'10.0.8');
Done.
```

Y las tres colas configuradas al arrancar Orders.API — la línea que prueba que el registro llegó a `ConfigureEndpoints`:

```
Configured endpoint order-confirmed, Consumer: Orders.API.Consumers.OrderConfirmedConsumer
Configured endpoint order-cancelled, Consumer: Orders.API.Consumers.OrderCancelledConsumer
Configured endpoint order-state, Saga: Orders.Domain.Sagas.OrderState, State Machine: Orders.Domain.Sagas.OrderStateMachine
Bus started: rabbitmq://localhost/
```

### 3. Escenario 2 — sin stock: el pedido **se cancela solo**

```
POST -> id=9f565b31-c2c5-4467-9f46-de2801bd9e4f status=Pending
GET  -> id=9f565b31-c2c5-4467-9f46-de2801bd9e4f status=Cancelled
```

Y la cadena entera en cuatro líneas de log, dos servicios:

```
Pedido 9f565b31-… creado con 1 línea(s) por un total de 10; OrderCreated publicado.
Saga arrancada para el pedido 9f565b31-… de sin-stock@shop133.test; pasa a StockPending.
[Inventory] Stock rechazado para el pedido 9f565b31-…: el producto 999999 no existe en el inventario.
Pedido 9f565b31-…: stock rechazado (el producto 999999 no existe en el inventario); pasa a Cancelled y se publica OrderCancelled. No hay nada que compensar.
Pedido 9f565b31-… cancelado en OrdersDb (…); su estado pasa de Pending a Cancelled.
```

**Es la diferencia con `2.3`, medida:** aquella petición devolvía `400` en el acto. Hoy devuelve `201` y el pedido se cancela solo unos segundos después — la validación síncrona convertida en un estado del pedido, que es lo que la coreografía reubica.

### 4. Escenario 3 — pago rechazado, y el stock que se queda

```
POST -> id=65fbee58-3c2b-430b-bd45-09617633b049 status=Pending total=1197
GET  -> status=Cancelled
```

```
Pedido 65fbee58-…: stock reservado por 1197; pasa a PaymentPending.
[Payments] Cobro rechazado para el pedido 65fbee58-… por 1197: el importe 1197.00 supera el límite autorizado de 1000.00.
Pedido 65fbee58-…: cobro rechazado (…); pasa a Cancelled y se publica OrderCancelled. OJO: el stock reservado NO se suelta hasta 4.4.
Pedido 65fbee58-… cancelado en OrdersDb (…); su estado pasa de Pending a Cancelled.
```

**Y el agujero de la regla 7, medido y atribuido** (`InventoryDb`):

```
ProductId   QuantityOnHand QuantityReserved
----------- -------------- ----------------
          3             18                4

OrderId                              | Quantity | CreatedAt
8E1DB892-C963-41FB-B191-1FD4CDE81572 | 1        | 2026-09-01 18:19:37 +00:00   ← de una prueba anterior
65FBEE58-3C2B-430B-BD45-09617633B049 | 3        | 2026-09-02 02:35:31 +00:00   ← el pedido CANCELADO de arriba
```

Las 3 unidades del pedido cancelado siguen reservadas y su fila de reserva sigue viva. **Eso es la Fase 4 en una frase, y es `4.4`.**

### 5. Escenario 1 — feliz: `Order.Status` por fin se mueve

```
POST -> id=8502f562-5c42-4f0f-a78b-b6f60c667d0d status=Pending total=298
GET  -> status=Confirmed
```

En `4.2` esa segunda línea decía `Pending` y no cambiaba nunca. Y la tabla nueva, con **dos `ConsumerName` distintos** (`OrdersDb.ProcessedMessages`):

```
MessageId                            | ConsumerName
2C3E0000-DCE1-6046-D182-08DF0899F829 | OrderCancelledConsumer
2C3E0000-DCE1-6046-2027-08DF089ADCFC | OrderCancelledConsumer
2C3E0000-DCE1-6046-CB4A-08DF089B634C | OrderConfirmedConsumer
```

### 6. Escenario 4 — duplicado, por las dos guardas

Reenviando a mano el `OrderCancelled` del pedido rechazado, **con el mismo `message_id`** (envelope completo: `content_type: application/vnd.masstransit+json`, `messageType` con URN, `message_id` en `properties`, JSON sin BOM):

```
{"routed":true}
El mensaje 2c3e0000-dce1-6046-2027-08df089adcfc ya lo procesó OrderCancelledConsumer (pedido 65fbee58-…); se descarta.
```

Y el mismo pedido con un `message_id` **nuevo**, que la guarda de transporte no ve:

```
El pedido 65fbee58-… ya estaba en Cancelled; no se vuelve a mover.
```

La fila nueva aparece en `ProcessedMessages` (`8B051AC6-…|OrderCancelledConsumer`) y `Orders.Status` sigue en `3` (`Cancelled`). Las dos guardas son distintas y las dos hacen falta: una reconoce la misma **entrega**, la otra el mismo **pedido**.

### 7. Las guardas terminales, comprobadas en vez de supuestas

`During(Cancelled, …)` no tiene ninguna transición, así que parece código muerto. Reenviando un `StockReserved` del pedido ya cancelado:

```
{"routed":true}
[Payments] El pedido 65fbee58-… ya se había cobrado … con resultado Failed; no se vuelve a cobrar y se reenvía el desenlace guardado.

order-created_error          messages=0
order-state_error            messages=0
```

Un solo reenvío ejercitó **dos** guardas de `Cancelled` —`StockReserved` directo y `PaymentFailed` por el reenvío de Payments— y ninguna faulteó. Sin esas líneas, los dos habrían acabado en `order-state_error`.

### 8. Ya no queda nada publicándose al vacío

```
order-cancelled                  messages=0
order-confirmed                  messages=0
order-created                    messages=0
order-created_error              messages=0
order-state                      messages=0
order-state_error                messages=0
stock-reserved                   messages=0

Shop133.Contracts.Events:OrderCancelled   -> order-cancelled
Shop133.Contracts.Events:OrderConfirmed   -> order-confirmed
Shop133.Contracts.Events:OrderCreated     -> order-created
Shop133.Contracts.Events:OrderCreated     -> order-state
Shop133.Contracts.Events:PaymentCompleted -> order-state
Shop133.Contracts.Events:PaymentFailed    -> order-state
Shop133.Contracts.Events:StockRejected    -> order-state
Shop133.Contracts.Events:StockReserved    -> order-state
Shop133.Contracts.Events:StockReserved    -> stock-reserved
```

**Los siete eventos de `Shop133.Contracts` tienen ya al menos una cola enlazada.** `StockRejected` y `PaymentFailed` llevaban desde `3.4`/`3.5` publicándose al vacío.

### 9. La suite existente, sin tocar

```
   Orders.Tests  Total: 12, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 61.177s
```

La migración nueva entra sola por el `MigrateAsync` de `OrdersApiFactory`, y los dos consumers nuevos no rompen el desmontaje del bus de `3.7`.

---

## Pendiente

- **`4.4`** — `Publish(ReleaseStock)` desde el camino de `PaymentFailed`, el consumer de Inventory que lo atiende, y con ello **la decisión 1 de este documento se relee**: si Inventory contesta algo, `CompensatingStock` aparece ahí con su espera de verdad; si no, la transición directa se queda como está. También decide si `ReleaseStock` conserva sus `Lines` (la PK de `StockReservations` *es* el `OrderId`, decisión 7 de [fase_3_4.md](fase_3_4.md)). **Hasta entonces, el stock de un pedido rechazado sigue reservado — verificación 4.**
- **`4.5`** — la tabla de la instancia, el token de concurrencia optimista, si comparte `OrdersDbContext`, si los dos estados terminales pasan a `Finalize()`, y el outbox transaccional. El outbox cierra además la doble escritura de estos dos consumers: hoy el `SaveChangesAsync` y el `Publish` de la saga no son atómicos.
- **`4.6`** — Notifications.API consume `OrderConfirmed` y `OrderCancelled`, que desde hoy tienen ya una cola cada uno pero solo la de Orders. Se llevará su propia guarda de `3.6`.
- **`4.7`** — los cuatro escenarios con el harness. Este punto se verificó **a mano**, como `3.4`, `3.5`, `4.1` y `4.2`; para automatizarlo, `OrdersApiFactory` tendrá que dejar de desmontar la saga junto con el resto de MassTransit.
- **`4.9` obliga a releer la lista de estados** una vez más: `PricingPending` entra *antes* de `StockPending`, y su rechazo cancela sin nada que compensar — o sea que estrena un tercer camino hacia `Cancelled`, con sus guardas.
- **Sin dueño** — guardar en el pedido *por qué* se canceló (decisión 4); la concurrencia optimista sobre `StockItem` que anotó `3.4`; que ninguna reserva confirmada baje nunca `QuantityOnHand`; y `ReserveStock`, que sigue sin usar desde la decisión 2 de `4.1`.
- **Entorno** — los 2 avisos `xUnit1051` de `CreateOrderTests.cs` vienen de `3.7` y siguen ahí. `dotnet test` sigue roto desde el SDK 10.0.400.
