# Fase 4.4 — `ReleaseStock`: la compensación

**Fecha:** 2026-09-02 · **Estado:** completado · **Roadmap:** [4.4](../plan-desarrollo-shop133.md#fase-4--saga-completa-con-compensaciones)

---

## Objetivo

Cerrar el agujero que `4.3` dejó anotado con fecha de caducidad: **un pedido cancelado por pago rechazado se quedaba con sus unidades reservadas para siempre**. Estaba medido en la verificación de [fase_3_5.md](fase_3_5.md) y era la regla 7 de `CLAUDE.md` sin cumplir.

Lo que entra: la saga envía `ReleaseStock` al recibir `PaymentFailed`, Inventory.API estrena su segundo consumer para devolver las unidades, y contesta con `StockReleased` — contrato nuevo, el décimo. Con esa respuesta **`CompensatingStock` nace por fin como estado real**, y `OrderCancelled` solo se publica cuando el stock está de verdad suelto.

Es la línea del checklist del roadmap que dice *"puedes forzar un fallo de pago y ver la compensación liberar el stock sin intervención manual"*, y el punto por el que el proyecto existe.

**Fuera de alcance, deliberadamente:**

- **La persistencia de la saga** (`4.5`). Sigue en `InMemoryRepository()`, y este punto le sube el precio — ver Pendiente.
- **Un plazo para `CompensatingStock`**. Si Inventory no contesta, el pedido se queda ahí para siempre. Hueco real, anotado, sin dueño en el roadmap.
- **Los tests de la saga** (`4.7`, los cuatro escenarios obligatorios). Sí entran los del consumer de Inventory, por el motivo de la decisión 7.
- **La validación de precios** (`4.8`/`4.9`).

---

## Decisiones

### 1. Inventory contesta: entra `StockReleased`, el décimo contrato

`4.3` se negó explícitamente a añadirlo: *"es una decisión de 4.4, con el consumer de la compensación delante"*. Con el consumer delante, la respuesta es que sí.

El argumento que la decide no es la simetría, es una promesa escrita desde `0.3`. El `///` de `OrderCancelled` afirma que en el camino de `PaymentFailed` *"el pago se rechazó, **y el stock ya se soltó con ReleaseStock**"*. Sin respuesta de Inventory, la saga publicaría esa cancelación sin saber si la compensación llegó a ocurrir: la frase sería una promesa que el código no puede cumplir.

El efecto de segundo orden es el que da nombre al punto: **con una respuesta que esperar, `CompensatingStock` se gana su sitio**. La regla que `4.2` dejó escrita y `4.3` aplicó por segunda vez —*hay un estado por cada respuesta que se espera, no por cada hecho que ocurre*— no cambió; cambió el mundo que describe. Ése es el motivo de haberla escrito en vez de recortar el estado y callarse.

*Descartado* el disparo al aire (mandar `ReleaseStock` y pasar directo a `Cancelled`). Es más simple, no añade contrato y garantiza que el pedido alcanza estado terminal. Se rechazó porque convierte la compensación en algo que la saga no puede afirmar: si Inventory falla, el pedido queda `Cancelled` con el stock retenido y **nadie se entera**, que es exactamente el fallo silencioso que este punto viene a eliminar. Con el ida y vuelta, ese caso deja el mensaje en `release-stock_error` y el pedido visiblemente parado.

*Descartado* un `StockReleaseFailed` que le hiciera pareja, como sí lo tienen `StockReserved`/`StockRejected` y `PaymentCompleted`/`PaymentFailed`. No hay ningún caso de **negocio** en el que la liberación pueda fracasar: las unidades que se devuelven son exactamente las que este servicio comprometió. Todo lo que puede salir mal aquí es una incoherencia entre dos tablas del mismo servicio, y darle un evento sería darle a la saga una forma de terminar fingiendo que soltó lo que no soltó.

Sube los 9 mensajes que fijó `0.3` a 10. El precedente que lo autoriza es el de `3.2`: un contrato se revisa cuando aparece el consumidor que lo necesita.

### 2. `ReleaseStock` pierde sus `Lines`

La pregunta lleva aplazada desde `0.3` y por escrito desde la sección Pendiente de [fase_3_2.md](fase_3_2.md), que prometió decidirla *"cuando se supiera cómo quedó la tabla de reservas"*. La decisión 6 de [fase_3_4.md](fase_3_4.md) la dejó cerrada de hecho sin cerrarla de nombre: **la PK de `StockReservations` *es* el `OrderId`**. Soltar el stock de un pedido es un `SELECT` por clave primaria.

Tres motivos, en orden de peso:

1. **Sería una segunda fuente para el mismo dato.** Un `ReleaseStock` cuyas líneas no coincidieran con la reserva obligaría al consumer a decidir a cuál hace caso — una pregunta sin respuesta buena que desaparece si el dato no viaja.
2. **`OrderState` se habría llevado una colección.** Para mandar las líneas hay que tenerlas, y el sitio sería la instancia de la saga; `4.5` tendría entonces que persistir una colección (columna JSON o tipo owned) para devolverle a Inventory lo que Inventory le dijo a la saga. El comentario "fuera a propósito" de `OrderState` decía exactamente eso y ahora lo da por cerrado.
3. Menos datos en la compensación es menos superficie para el duplicado que el propio `///` del comando advierte.

*Descartado* conservarlas para que el consumer no dependiera de encontrar la fila. No es una ventaja: si la fila no está, el estado es incoherente, y soltar unidades guiándose por el mensaje sería inventarse una reserva que nadie registró.

**Es un cambio incompatible bajo la regla 4 de `CLAUDE.md`, y salió gratis porque `ReleaseStock` no tenía ni un consumidor en todo el repositorio** — ningún proyecto dejó de compilar. Era hoy o nunca. `ReserveStock` conserva sus `Lines` y sigue sin llamante, por la decisión 2 de [fase_4_1.md](fase_4_1.md).

### 3. Se manda con `Send`, no con `Publish` — el único `Send` del proyecto

Semántica de comando: un destinatario, punto a punto. Es lo que dice su propio `///` desde `0.3` (*"**Enviado** por la saga a Inventory.API"*).

Publicarlo habría funcionado —MassTransit liga un consumer al exchange de su tipo de mensaje sin mirar si es comando o evento— y tenía una ventaja real: Orders no sabría el nombre de ninguna cola ajena. Se rechazó por dos motivos:

1. **Un fanout deja la puerta abierta a que un segundo consumidor se ligue y suelte el stock dos veces**, sin tocar una línea ni de Orders ni de Inventory. Es justo lo que el `///` de `ReleaseStock` avisa que es peor que un duplicado de `ReserveStock`: éste solo bloquea de más, aquél *crea unidades de la nada*.
2. Si no, **la carpeta `Commands/` no tendría ninguna consecuencia observable**. `ReserveStock` se quedó sin llamante en `4.1`, así que éste es el único comando que el proyecto llega a mandar. La distinción evento/comando o se ve en el código o es decoración.

El precio, dicho en voz alta: `private static readonly Uri InventoryReleaseStockEndpoint = new("queue:release-stock")` en `Orders.Domain`. El nombre sale de aplicar `SetKebabCaseEndpointNameFormatter()` a `ReleaseStockConsumer`, y **si alguien lo cambia, esto no falla**: MassTransit crea la cola que se le nombre, así que los comandos se apilarían donde nadie lee, sin error y sin aviso. Por eso la verificación mira el broker y no solo los logs, y por eso el test manda a esa misma URI literal (decisión 7).

*Descartado* `EndpointConvention.Map<ReleaseStock>(...)` en el `Program.cs` de Orders.API, que sacaría la dirección del dominio y la dejaría en la raíz de composición — que es donde conceptualmente pertenece. Es estado estático global de proceso: cada host de test tendría que acordarse de mapearlo o el `Send` sin URI lanza, y el fallo sería de configuración del test, no del código. Una constante con nombre se lee entera donde se usa.

### 4. La reserva se **marca** como liberada, no se borra

`StockReservation` gana `ReleasedAt` (`datetimeoffset NULL`, donde `null` significa *reserva viva*) y un método `Release()`. Borrar la fila era más simple y daba idempotencia "por ausencia". Se rechazó por tres motivos:

1. **Destruye la evidencia de que la compensación ocurrió**, que es justo lo que esta fase existe para demostrar.
2. **Rompe la guarda de negocio de `OrderCreatedConsumer`.** Esa guarda mira si existe reserva; sin fila, un `OrderCreated` reentregado con `MessageId` nuevo **volvería a comprometer unidades para un pedido ya cancelado**. Por eso esa guarda mira la fila **sin** mirar su `ReleasedAt`: una reserva ya liberada sigue siendo motivo para no volver a reservar.
3. Sin fila no se puede distinguir *"ya se liberó"* de *"nunca se reservó"* — el caso normal y la incoherencia de la decisión 6.

Sin columna de estado además de la fecha: el sello ya dice las dos cosas, y un `enum` al lado obligaría a mantener los dos de acuerdo. Mismo criterio que `Payment`, que no lleva un `bool` además de su `Status`.

`Release()` **lanza si ya estaba liberada**, igual que `Order.Confirm()`/`Cancel()` de `4.3` lanzan al repetir la transición. Es intencionadamente hostil: reconocer el duplicado es trabajo del *consumer*, así que si la excepción salta, o falló aquella guarda o las dos tablas discrepan. Dejarla pasar mezclaría el duplicado legítimo con el fallo real, y aquí el fallo real significa haber soltado el stock dos veces.

`StockItem` gana `Release(int quantity)`, espejo exacto de `Reserve`. Recibe cantidad aunque el comando solo lleve el `OrderId`: quien localiza por pedido es el consumer, la aritmética sigue siendo por línea — el comentario que ocupaba ese sitio se había quedado a medias al suponer lo contrario. **Lanza si se piden más unidades de las reservadas** en vez de saturar en 0: saturar convertiría una incoherencia entre tablas en un número redondeado que nadie miraría.

### 5. Tres defensas superpuestas en el consumer, no una

Porque aquí la idempotencia importa más que en ningún otro consumer del proyecto: un `ReleaseStock` procesado dos veces devuelve el stock dos veces, **creando unidades de la nada**.

1. **Transporte**, por `MessageId` del sobre (`3.6`). Copia literal de la de `OrderCreatedConsumer`.
2. **Negocio**, por `ReleasedAt`. La hermana de la que en el otro consumer va por la existencia de la fila; aquí no puede ir por ahí porque la fila existe siempre. Republica `StockReleased` en vez de salir en silencio: un `MessageId` nuevo es alguien que ha vuelto a preguntar, y quien espera esa respuesta es una saga que sin ella no sale de `CompensatingStock`.
3. El **`throw` de `StockItem.Release`** si las cuentas no cuadran.

La marca de `3.6` entra en el **mismo `SaveChangesAsync`** que la devolución y el sello, por el motivo de siempre: un `IFilter` de MassTransit confirmaría la marca en otra transacción y entre las dos cabe el estado fatal de *marcado como procesado y sin hacer*, que la reentrega ya no repara porque se lo salta.

Es además el **segundo consumer de Inventory**, así que es la primera vez que `ProcessedMessages` tiene dos `ConsumerName` distintos: la PK compuesta que `3.6` puso "por si acaso" deja de ser hipotética.

### 6. Una reserva que falta **revienta**; no se contesta igualmente

La saga solo manda este comando desde `PaymentPending`, al que únicamente se llega por el `StockReserved` que publicó el propio Inventory. No encontrar la fila es una incoherencia real, no un caso de negocio — mismo criterio con el que los consumers de Orders revientan ante un pedido que no está en `OrdersDb` (`4.3`).

El precio, dicho en voz alta: el mensaje se queda en `release-stock_error` y **la saga se queda esperando en `CompensatingStock`**, sin plazo que la saque. Es el desenlace honesto de una incoherencia — visible en una cola de error, en vez de tapado con un `StockReleased` que mentiría.

### 7. Los tests del consumer entran aquí, no en `4.7`

El roadmap pone los tests de la saga en `4.7`, y ésos no cubren el consumer de Inventory. La regla 3 de "Testing" de `CLAUDE.md` es incondicional —*todo consumer tiene un test de idempotencia*— y con `Inventory.Tests` ya montado desde `3.7` sale barato. `Inventory.Tests` pasa de 9 a **15**, y el total del repositorio de 65 a **71**.

Se rompieron las dos guardas a propósito antes de confiar en ellos (ver Verificación). `4.7` sigue debiendo su mitad: el paso por `CompensatingStock` y el "exactamente un `ReleaseStock`" contra la máquina de estados.

### 8. En los tests, la reserva se siembra por base de datos — y el motivo se midió

La primera versión de `ReleaseStockConsumerTests` reservaba publicando un `OrderCreated` y luego enviaba el comando. **Dos de los cuatro tests susceptibles fallaron con cero `StockReleased` mientras los otros dos pasaban, en la misma ejecución.**

La causa es la trampa 1 de las tres que anotó `3.7`, estrellándose de verdad: `harness.InactivityTask` es **una sola tarea** que se completa la primera vez que el bus queda inactivo. Con dos etapas de bus por test, el `await GetSendEndpoint(...)` que va entre ellas abre un hueco en el que el bus se vacía, la tarea se completa, y el `await` del final vuelve al instante — los asserts corren antes de que el consumer haya trabajado. Hay además una segunda carrera: los dos consumers tienen endpoints distintos y `ConcurrentMessageLimit = 1` es **por endpoint**, así que nada ordenaba el `OrderCreated` antes del `ReleaseStock`.

La solución es dejar **una sola etapa de bus por test**: `InventoryConsumerHost.SeedReservationAsync` crea la reserva con las **mismas entidades y los mismos métodos** que usa el consumer (`StockItem.Reserve` y el constructor de `StockReservation`, con sus invariantes), sin pasar por el bus.

*Descartado* esperar con `Published.Any<StockReserved>()` entre las dos etapas. Ordenaría bien, pero no arregla el `InactivityTask` gastado, y sin él solo se puede afirmar *"al menos uno"*, nunca *"exactamente uno"* — que es lo que un test de idempotencia necesita decir.

**El precio:** la fila que estos tests liberan no la escribió `OrderCreatedConsumer`, así que una divergencia entre las dos formas de crearla no se notaría aquí. Lo compensa que `OrderCreatedConsumerTests` afirma la forma de esa fila por su cuenta.

### 9. Ninguna regla de arquitectura nueva — la suite se queda en 16

No se tocó ningún `.csproj` ni se añadió ningún paquete. Todas las formas que introduce este punto ya están cubiertas: el consumer nuevo por `ConsumerFiles_LiveOnlyIn_ServiceApiConsumersFolder`, y `StockReleased` por `Contracts_PublicTypes_LiveInEventsOrCommandsNamespace` y `Contracts_PublicTypes_AreSealedRecords`.

Se dice por escrito en vez de inventar una regla para subir el contador, con el precedente de `3.3` y `3.5`: una regla cuyo filtro no encuentra nada pasa en verde para siempre, que es lo que `3.2` advirtió al romper la suya a propósito.

---

## Cambios

### `src/Shared/Shop133.Contracts/`

| Archivo | Rol |
|---|---|
| `Commands/ReleaseStock.cs` | **Modificado.** Pierde `IReadOnlyList<OrderLine> Lines`; queda solo `OrderId`. El `///` explica la decisión 2 y que el cambio incompatible salió gratis. |
| `Events/StockReleased.cs` | **Nuevo.** El décimo mensaje. `sealed record` con `required Guid OrderId`. |

### `src/Services/Orders/Orders.Domain/Sagas/`

| Archivo | Rol |
|---|---|
| `OrderState.cs` | Gana `CancellationReason`. El bloque "fuera a propósito" da por cerrada la pregunta de las líneas. |
| `OrderStateMachine.cs` | El estado `CompensatingStock`, el evento `StockReleased`, la URI `queue:release-stock`, la transición `PaymentFailed` reescrita y el `During(CompensatingStock, ...)` nuevo. Los dos estados terminales pasan de cinco `Ignore` a seis. |

### `src/Services/Inventory/`

| Archivo | Rol |
|---|---|
| `Inventory.API/Consumers/ReleaseStockConsumer.cs` | **Nuevo.** El segundo consumer del servicio y el primero de un comando en todo el proyecto. |
| `Inventory.API/Program.cs` | `x.AddConsumer<ReleaseStockConsumer>()` → cola `release-stock`. |
| `Inventory.API/Consumers/OrderCreatedConsumer.cs` | Solo comentario: su guarda de negocio mira la fila **sin** mirar `ReleasedAt`, y ahora se explica por qué. |
| `Inventory.Infrastructure/Entities/StockItem.cs` | Gana `Release(int)`, sustituyendo al comentario "Sin Release()". |
| `Inventory.Infrastructure/Entities/StockReservation.cs` | Gana `ReleasedAt` y `Release()`. |
| `Inventory.Infrastructure/Persistence/Configurations/StockReservationConfiguration.cs` | Mapea `ReleasedAt`, **sin** `IsRequired()`. |
| `Inventory.Infrastructure/Migrations/20260902183754_AddReservationReleasedAt.cs` | **Nueva.** `ALTER TABLE [StockReservations] ADD [ReleasedAt] datetimeoffset NULL;` |

### `tests/Services/Inventory/Inventory.Tests/`

| Archivo | Rol |
|---|---|
| `ReleaseStockConsumerTests.cs` | **Nuevo.** 6 tests. |
| `Infrastructure/InventoryConsumerHost.cs` | Registra el consumer nuevo, fija `SetKebabCaseEndpointNameFormatter()` para que el endpoint se llame `release-stock`, y añade `SeedReservationAsync` y `ReleasedAtAsync`. |

**No se tocó** ningún `.csproj`, ningún paquete, ni `Payments`, ni `Catalog`, ni `Orders.API`.

---

## Detalles que cuestan tiempo

**El orden dentro de la transición importa, y no es el que parece.** `.TransitionTo(CompensatingStock)` va **antes** de `.Send(...)`: las actividades se ejecutan en el orden en que se encadenan, así que con el `Send` primero la respuesta de Inventory podría llegar con la instancia todavía en `PaymentPending`, donde `StockReleased` no está aceptado. Iría a `order-state_error` una de cada tantas veces.

**`.Send(Uri, ctx => new T{...})` existe en el DSL de la saga**, igual que `.Publish` — no hace falta la forma larga con `Init<T>`. Es la misma sorpresa que anotó `4.2` sobre `Publish`.

**Los `Ignore` pasan de 15 a 18, y lo que *no* se ignora es lo que hay que leer.** `During(CompensatingStock, ...)` ignora `OrderCreated`, `StockReserved` y `PaymentFailed` — los tres del camino recorrido. **No** ignora `PaymentCompleted` ni `StockRejected`: llegar ahí implica que cada servicio ya contestó, y publican uno u otro, nunca los dos, así que esos mensajes serían un servicio contradiciéndose. Mismo criterio literal que `During(PaymentPending)` en `4.3`.

**El `Ignore(StockReleased)` de `Confirmed` es inalcanzable en teoría** —al camino feliz no se le manda nunca `ReleaseStock`— y se pone igual: la disciplina de los estados terminales es deliberadamente roma, porque una excepción obligaría a razonar caso por caso cada vez que se añade un evento, que es como se olvida uno.

**Un estado intermedio obliga a guardar el motivo.** `OrderCancelled` ya no se publica en la transición que recibe el `Reason`, sino una después, al recibir un `StockReleased` que no lleva texto. De ahí `OrderState.CancellationReason`, que **solo escribe uno de los dos caminos** — el de `StockRejected` sigue leyendo el mensaje que entra, y añadirlo "por simetría" sería un campo escrito para no leerse nunca.

**`InactivityTask` gastado a mitad de test**: ver la decisión 8. Es la trampa que `3.7` documentó y la primera vez que se cobra.

### Smart App Control: cuatro remedios documentados agotados, y el que funcionó

Al arrancar los servicios, `Orders.API` murió con `An Application Control policy has blocked this file (0x800711C7)` sobre `Orders.Infrastructure.dll`. Se probó, en orden:

| Remedio | Resultado |
|---|---|
| Reintentar (×3, luego ×4, luego ×6) | **No** |
| `dotnet build -c Release` + ejecutar en Release (`3.5`) | **No** — el bloqueo se mudó a `Orders.API.dll` |
| Lanzar el `.dll` con `dotnet` en vez del `.exe` (`3.7`) | **No** — el bloqueo era sobre una biblioteca, no sobre el apphost |
| `dotnet build --no-incremental` | **No** |
| **`dotnet build -p:Deterministic=false --no-incremental`** | **Sí** |

**La razón por la que los reintentos y las reconstrucciones no servían es nueva y explica los episodios anteriores: las compilaciones de .NET son deterministas por defecto**, así que reconstruir el mismo código con las mismas referencias produce **bytes idénticos**, mismo hash y por tanto el mismo veredicto. Reintentar evaluaba una y otra vez el mismo archivo. `-p:Deterministic=false` cambia el MVID, y con él el hash, sin tocar una línea de código fuente ni un `.csproj`.

Corolario práctico: hubo que aplicarlo **por proyecto**. Con `Orders.Infrastructure` desbloqueado el bloqueo saltó a `Orders.Domain`, y hizo falta repetir la operación sobre él. Eso confirma que el bloqueo es **por archivo**, como ya observó `3.7`.

**Y el segundo corolario, que es el que sorprende: una compilación normal después lo devuelve.** El mismo bloqueo apareció luego en `Orders.Tests` —los 12 tests fallando idénticamente en el constructor de la fixture, que es la firma documentada— y siguió el mismo guion: reintentar no sirvió, `-p:Deterministic=false` lo dejó en **12/12 verde**, y **un `dotnet build` corriente lo devolvió a 12/12 rojo**, porque el determinismo reproduce exactamente los bytes que Windows ya rechazó. La bandera hay que reaplicarla a ese proyecto tras cada compilación ordinaria hasta que Windows cambie de opinión. Al repositorio no le afecta: `bin/` está en `.gitignore`.

Y sigue en pie: nunca desactivar Smart App Control, que es irreversible sin reinstalar Windows.

### Otros

**`docker compose exec ... sqlcmd` desde el Bash tool sigue rompiéndose** (Git Bash reescribe `/opt/...`); todo lo de abajo se ejecutó desde PowerShell.

**El log de la consola sale con la UTF-8 mal decodificada** (`procesÃ³`, `lÃ­nea`) al redirigir a fichero con `Start-Process`. Es cosmético y no afecta a lo que se guarda en base de datos.

---

## Verificación

### 1. Compilación y suites

```
> dotnet build
Build succeeded.
    0 Warning(s)
    0 Error(s)

> dotnet tests\Shop133.ArchitectureTests\bin\Debug\net10.0\Shop133.ArchitectureTests.dll
   Shop133.ArchitectureTests  Total: 16, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.606s

> dotnet tests\Services\Inventory\Inventory.Tests\bin\Debug\net10.0\Inventory.Tests.dll
   Inventory.Tests  Total: 15, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 96.721s
```

### 2. Las dos guardas, rotas a propósito

**Guarda de negocio** (`reservation.ReleasedAt is not null`, anulada con `&& DateTimeOffset.UtcNow.Year < 2000`):

```
Inventory.Tests.ReleaseStockConsumerTests.Consume_SameOrderWithANewMessageId_RepublishesStockReleasedWithoutReleasingTwice [FAIL]
  Assert.Equal() Failure: Values differ
   Inventory.Tests  Total: 6, Failed: 1
```

**Guarda de transporte** (`alreadyProcessed`, anulada igual):

```
Inventory.Tests.ReleaseStockConsumerTests.Consume_SameMessageIdTwice_ReleasesOnceAndPublishesASingleStockReleased [FAIL]
  Assert.Empty() Failure: Collection was not empty
   Inventory.Tests  Total: 6, Failed: 1
```

**Nótese cuál de los dos asserts falló**: el `Assert.Empty(Published<Fault<ReleaseStock>>())`, no el `Assert.Single(StockReleased)`. Sin la guarda, la segunda entrega tampoco publica —muere antes— así que el recuento sale 1 en los dos casos. Es exactamente la trampa 3 de `3.7`, confirmada aquí: **contar eventos de negocio no distingue "descartado en silencio" de "explotó"**.

### 3. La migración

```sql
ALTER TABLE [StockReservations] ADD [ReleasedAt] datetimeoffset NULL;
INSERT INTO [__EFMigrationsHistory] ... VALUES (N'20260902183754_AddReservationReleasedAt', N'10.0.8');
```

### 4. Topología del broker

Con Inventory.API arrancado, `Configured endpoint` confirma las dos colas y —lo que importa— que el nombre coincide con la URI que escribe la saga:

```
Configured endpoint order-created,  Consumer: Inventory.API.Consumers.OrderCreatedConsumer
Configured endpoint release-stock,  Consumer: Inventory.API.Consumers.ReleaseStockConsumer
```

```
Shop133.Contracts.Commands:ReleaseStock  -> release-stock
```

Con Orders.API arrancado:

```
Configured endpoint order-confirmed, Consumer: Orders.API.Consumers.OrderConfirmedConsumer
Configured endpoint order-cancelled, Consumer: Orders.API.Consumers.OrderCancelledConsumer
Configured endpoint order-state, Saga: Orders.Domain.Sagas.OrderState, State Machine: Orders.Domain.Sagas.OrderStateMachine
```

Ocho colas (siete más `order-created_error`), frente a las cinco de `4.3`.

### 5. La compensación a mano contra el broker real

Con una **cola espía** `spy-stock-released` ligada al exchange de `StockReleased` (`durable:true`; RabbitMQ 4.x rechaza las transitorias no exclusivas). Es imprescindible: la base de datos queda idéntica se descarte o se reprocese un duplicado, así que la única diferencia observable es **cuántos eventos salieron**.

`OrderCreated` a mano (producto 1, 3 unidades) → Inventory reserva:

```
QuantityOnHand  QuantityReserved
            42                11        ← venía de 8
OrderId                               ReleasedAt
2FB2AA74-A6F2-4CFE-ACE1-553612EFB9F7  NULL
```

**Ése es exactamente el estado en que `4.3` dejaba un pedido cancelado para siempre.**

`ReleaseStock` a `Shop133.Contracts.Commands:ReleaseStock` con `message_id` fijo:

```
QuantityOnHand  QuantityReserved
            42                 8        ← las 3 unidades, de vuelta
ReleasedAt
2026-09-02 19:07:20.5427311 +00:00
ConsumerName          MessageType
ReleaseStockConsumer  Shop133.Contracts.Commands.ReleaseStock

release-stock        messages=0
spy-stock-released   messages=1
```

`release-stock_error` ni siquiera llegó a crearse.

### 6. Idempotencia contra el broker real: el mismo `message_id`, otra vez

```
{"routed":true}
QuantityReserved
               8        ← sigue en 8. Soltar dos veces habría dado 5.

release-stock        messages=0
spy-stock-released   messages=1        ← sigue en 1: se descartó, no se reprocesó

info: Inventory.API.Consumers.ReleaseStockConsumer[0]
      El mensaje 839a347a-d186-4900-9d03-8c748ebd346a ya lo procesó ReleaseStockConsumer
      (pedido 2fb2aa74-a6f2-4cfe-ace1-553612efb9f7); se descarta.
```

**Bajar a 5 habría sido crear tres unidades de la nada**, que es el daño concreto que este consumer existe para evitar.

### 7. El punto entero, de punta a punta

Pedido de 3 unidades del producto 5 a `399.00` = **`1197.00`**, por encima del umbral `Payments:DeclineAmountAbove` de `1000.00`, que fuerza el rechazo de forma determinista.

```
=== ANTES ===  QuantityOnHand 31, QuantityReserved 2
=== 201    ===  orderId = c7cd409c-...  status = Pending  total = 1197
```

Cinco segundos después, sin tocar nada:

```
=== DESPUES: inventario ===
QuantityOnHand  QuantityReserved
            31                 2        ← reservó 3 y devolvió 3
ReleasedAt
2026-09-02 19:11:21.6527887 +00:00

=== DESPUES: el pedido ===
status = Cancelled
```

Y la traza de la saga, que es el punto entero en cinco líneas:

```
Saga arrancada para el pedido c7cd409c-... ; pasa a StockPending.
Pedido c7cd409c-...: stock reservado por 1197; pasa a PaymentPending.
Pedido c7cd409c-...: cobro rechazado (el importe 1197.00 supera el límite autorizado de 1000.00);
  pasa a CompensatingStock y se envía ReleaseStock a queue:release-stock.
  El pedido NO se cancela hasta que Inventory conteste StockReleased.
Pedido c7cd409c-...: stock liberado por Inventory; pasa a Cancelled y se publica OrderCancelled
  (el importe 1197.00 supera el límite autorizado de 1000.00). La compensación está completa.
Pedido c7cd409c-... cancelado en OrdersDb (...); su estado pasa de Pending a Cancelled.
```

Inventory, al otro lado del cable:

```
Stock reservado para el pedido c7cd409c-...: 1 línea(s) por un importe de 1197.
Stock liberado  para el pedido c7cd409c-...: 1 línea(s) devuelta(s) al inventario.
```

**Contraste con `4.3`, que es lo que da valor al número:** el mismo pedido dejaba `QuantityReserved = 3` y `StockReservations` vivo para siempre, con el pedido en `Cancelled`. Ahora vuelve a 0 sin intervención manual, y el pedido no se cancela hasta que eso ha ocurrido.

### 8. La guarda del estado terminal

Reposteando un `StockReleased` del pedido ya cancelado:

```
{"routed":true}
order-state_error messages=2   (seguía en 2 antes del reposteo)
status del pedido = Cancelled
```

El `Ignore(StockReleased)` de `During(Cancelled, ...)` lo absorbe. Sin él, un pedido perfectamente terminado habría acabado en la cola de error.

### 9. Los dos mensajes que sí hay en `order-state_error`

No son de este punto y conviene dejar dicho por qué están:

```
urn:message:Shop133.Contracts.Events:StockReserved     pedido 2fb2aa74-...
  Saga exception ...: An existing saga instance was not found
urn:message:Shop133.Contracts.Events:PaymentCompleted  pedido 2fb2aa74-...
  Saga exception ...: An existing saga instance was not found
```

Son del `OrderCreated` que se publicó a mano en la verificación 5 **mientras Orders.API estaba caído por el bloqueo de Smart App Control**: sin instancia de saga viva, los eventos correlacionados de ese pedido caen ahí. Es exactamente el `OnMissingInstance(missing => missing.Fault())` que `4.2` puso a propósito, funcionando — y una demostración accidental de por qué `4.5` hace falta.

---

## Pendiente

- **`4.5` — persistir la saga.** Este punto le sube el precio: un reinicio de Orders.API con un pedido en `CompensatingStock` pierde la instancia y el `StockReleased` que llegue después va a `order-state_error`. **El stock sí se suelta** —Inventory ya recibió el comando y trabajó— pero el pedido se queda en `Pending` en `OrdersDb` para siempre, con su reserva marcada como liberada. Es el mismo agujero de antes, ahora con una consecuencia visible repartida en dos bases de datos.
- **Sin plazo en `CompensatingStock`.** Si Inventory no contesta nunca, el pedido no alcanza estado terminal. La cura es un `Schedule`/`Request` con timeout de MassTransit, y **no tiene dueño en el roadmap**.
- **`4.6`** — Notifications consume `OrderCancelled`, que desde este punto sale *después* de la compensación, así que su texto ya puede afirmar con verdad que el stock se devolvió.
- **`4.7`** — la mitad de la saga: el escenario 3 obligatorio (*exactamente un `ReleaseStock`, estado final `Cancelled`*) y el paso por `CompensatingStock`, contra el harness. Los tests del consumer de Inventory ya están hechos aquí (decisión 7).
- **Sigue sin dueño desde `3.4`**: nada convierte una reserva confirmada en una bajada de `QuantityOnHand`, así que un pedido pagado deja sus unidades reservadas para siempre — el gemelo de este agujero, en el camino feliz. El sitio natural sería un consumer de `OrderConfirmed` en Inventory.
- **Sigue sin dueño desde `3.7`**: `StockItem` no tiene concurrencia optimista, y nada comprueba que los `Program.cs` de Inventory y Payments registren de verdad sus consumers (hueco de `8.2`).
- **`ReserveStock` sigue sin llamante** — el único de los diez mensajes sin usar, por la decisión 2 de `4.1`.
