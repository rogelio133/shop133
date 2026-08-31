# Fase 3.6 — Idempotencia por `MessageId` del sobre en Inventory y Payments

**Fecha:** 2026-08-31 · **Estado:** completado · **Roadmap:** [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md)

---

## Objetivo

RabbitMQ garantiza *al menos* una entrega. No es una advertencia teórica de la documentación: es el contrato del transporte, y significa que los duplicados **van a pasar**. La regla 6 de CLAUDE.md lo traduce a una instrucción sin matices — *"Persist processed `MessageId`s and skip repeats"* — y hasta hoy esa regla vivía solo en prosa. Un `grep` de `MessageId`, `Idempot` o `InboxState` sobre `src/` no devolvía una línea de código; devolvía seis comentarios prometiendo este punto.

Lo que sí existía eran **dos guardas de negocio por `OrderId`**: la PK de `StockReservations` (decisión 7 de [fase_3_4.md](fase_3_4.md)) y la de `Payments` (decisión 3 de [fase_3_5.md](fase_3_5.md)). Los dos documentos dejaron escrito, en su sección Pendiente, que no sustituyen a ésta y por qué. El motivo concreto, y es el que da sentido al punto:

> **El camino de rechazo de Inventory no escribe nada.** Publica `StockRejected` sin tocar el `ChangeTracker`, así que no deja rastro con el que reconocer un duplicado. Un `OrderCreated` reentregado de un pedido sin stock volvía a validar y publicaba un **segundo** `StockRejected`.

Eso no reventaba nada en la Fase 3 porque nadie consume `StockRejected` todavía. Con la saga de `4.3` delante, sí.

Lo que entra: una tabla `ProcessedMessages` por base de servicio, clave `(MessageId, ConsumerName)`, consultada al entrar en cada consumer y escrita **en la misma transacción que el trabajo**. El identificador sale del **sobre** de MassTransit y nunca de un campo de contrato — comprometido por escrito en `0.3`, repetido en `2.1` y en `3.2`.

**Fuera de alcance deliberadamente:**

| Queda fuera | Entra en |
|---|---|
| El test automatizado de idempotencia (mismo `MessageId` dos veces → un solo efecto) | `3.7`, con el harness en memoria. El roadmap lo llama "la única verificación fiable de 3.6" |
| Retry, redelivery y configuración de colas `_error` | Fase 4 — ver Pendiente, donde se cierra la promesa que `3.1` dejó abierta para el rango 3.4–3.6 |
| El agujero de la doble escritura (`SaveChanges` hecho, `Publish` perdido) | `4.5`, el outbox transaccional. Este punto lo **agranda**, y está documentado en la decisión 3 |
| Orders.API | No tiene consumer hasta `4.1`. Sin consumer no hay nada que deduplicar |
| Concurrencia optimista en `StockItem` | Sin dueño desde `3.4`. Ver Pendiente |
| `MassTransit.EntityFrameworkCore` y su `InboxState` | Pre-reservado a `4.5` y solo para Orders — ver la decisión 2 |

---

## Decisiones

### 1. La guarda vive **dentro del consumer**, no en un filtro de MassTransit

La alternativa era un `IFilter<ConsumeContext<T>>` registrado una vez por servicio con `cfg.UseConsumeFilter(...)`, envolviendo a todos los consumers presentes y futuros. Es lo que la prosa de `3.4` y `3.5` parecía prometer al decir que la idempotencia de 3.6 "vale para cualquier consumer", y tiene una ventaja real: nadie puede olvidarse de ponerla en un consumer nuevo.

**Descartado — el filtro parte la transacción en dos.** El filtro tiene que confirmar la marca en su propio `SaveChangesAsync`, porque no sabe nada del trabajo que hace el consumer que envuelve. Eso abre un estado que hoy no existe: **mensaje marcado como procesado y trabajo sin hacer**. Si el consumer revienta después de que el filtro escriba, la reentrega encuentra la marca y se salta un trabajo que nunca ocurrió — en silencio, que es la peor forma. La variante "el filtro hace `Add` y deja que el consumer guarde" tampoco cierra: depende de que el consumer llame a `SaveChanges`, y precisamente la rama que este punto viene a arreglar no lo llamaba.

Dentro del consumer, en cambio, la marca es una línea más del `Add` que ya había: **o entran las dos cosas o no entra ninguna**. Ese es todo el argumento, y es el mismo por el que `3.4` metió el incremento del stock y el alta de la reserva en un solo `SaveChanges`.

El precio se paga y se nombra: hay que acordarse en cada consumer nuevo (`4.1`, `4.4`, `4.6`). Lo que lo hace asumible es que `3.7` y `4.7` traen un test de idempotencia **por consumer**, que es exactamente el olvido que el filtro pretendía evitar. Y CLAUDE.md tiene una preferencia explícita para este empate: entre listo y que-se-explica-solo, gana el segundo.

### 2. Tabla propia, no el `InboxState` de `MassTransit.EntityFrameworkCore`

MassTransit trae su propio inbox y habría salido casi gratis: un paquete, un `AddEntityFrameworkOutbox`, cero código de deduplicación.

**Descartado por dos motivos, y el primero es de calendario.** `3.5` dejó escrito en el `Program.cs` de Payments que el próximo punto de relectura del bloque `AddMassTransit` es **`4.5`**, "y esta vez con una divergencia real: el outbox transaccional mete `MassTransit.EntityFrameworkCore` y una configuración de persistencia **solo en Orders**". Traerse ese paquete aquí, a los dos servicios que `4.5` no toca, es gastarse esa decisión antes de tener delante el caso que la motiva.

El segundo es pedagógico y pesa más en este proyecto: el `InboxState` de MassTransit resuelve la regla 6 **escondiéndola**. La tabla queda, el `Consume` no cambia y no hay ni una línea donde leer qué significa "ya procesado". Cincuenta líneas de tabla y guarda explícita son justo lo que el proyecto existe para hacer legible. El día que `4.5` traiga el outbox de verdad, la comparación entre las dos cosas estará escrita en el repo en vez de en una nota.

### 3. Ante un duplicado se sale **en silencio**, y eso quita el reenvío curativo que había

Es "skip repeats" al pie de la letra: no se hace el trabajo y **no se publica nada**.

La consecuencia no es neutra y no se disimula. Las guardas de negocio de `3.4` y `3.5` republicaban el desenlace, con un razonamiento explícito: *"si el mensaje se repite es que algo se perdió, y puede haber sido la respuesta"*. Ese reenvío curaba, de rebote, el agujero de la doble escritura — si el proceso moría entre el `COMMIT` y el `Publish`, la reentrega volvía a publicar y el sistema se recomponía solo.

**Con la guarda de transporte delante, eso deja de pasar.** Una reentrega de RabbitMQ conserva el `MessageId`, así que es exactamente el caso que ahora se descarta. Muerto el proceso entre el `COMMIT` y el `Publish`, ese mensaje se pierde para siempre.

**Descartado — reenviar el desenlace guardado también en la rama de transporte.** Se puede, y en Payments sería fácil (la fila tiene el desenlace entero). En Inventory no: el camino de rechazo no guarda el `Reason`, así que habría que persistirlo — meter texto de negocio en una tabla que a propósito no sabe de negocio, o darle un `Status` a `StockReservation` que hoy no tiene. Dos cambios de modelo para tapar un caso que **ya tiene dueño**.

Porque el diagnóstico correcto es ése: esto no es un defecto de este punto, es **un inbox sin outbox**, que se comporta exactamente así. Lo cierra `4.5`. Curarlo aquí con un reenvío sería tapar con suerte lo que allí se arregla con una transacción, y de paso borrar la razón por la que `4.5` existe.

Las dos guardas conviven y el orden importa: la de transporte reconoce la misma **entrega**, la de negocio reconoce el mismo **pedido**. Un `OrderCreated` reacuñado con `MessageId` nuevo pasa por la primera sin enterarse y lo para la segunda, que sí republica. Medido en la verificación 5.

### 4. La clave primaria es **compuesta**: `(MessageId, ConsumerName)`

Cada consumer de MassTransit tiene su propia cola, así que un mismo mensaje se entrega a **todos** los consumers del servicio que lo escuchen, con el mismo `MessageId`. Con la PK en el `MessageId` a secas, el segundo consumer encontraría la fila del primero y creería que ya lo procesó: se saltaría un trabajo que nunca hizo, sin error y sin log.

Hoy cada servicio tiene un consumer y la clave compuesta parece de más. Entra ahora porque la Fase 4 trae el consumidor de `ReleaseStock` (`4.4`) y para entonces la tabla ya tendría filas — **cambiar una PK con datos dentro es una migración, no una línea**.

**Descartado — un `Id` propio identity con índice único sobre el par.** Es la misma restricción escrita dos veces, y aquí no hay ninguna FK apuntando a esta tabla que agradezca una clave estrecha.

`ConsumerName` se guarda como `nameof(OrderCreatedConsumer)`, sin espacio de nombres: la tabla vive en `InventoryDb` y ahí no puede haber dos consumers homónimos. Lo que eso implica está escrito en el `///` de la constante: **renombrar un consumer es un cambio de esquema disfrazado**, porque las filas viejas pasan a verse como no procesadas.

### 5. Un sobre **sin `MessageId` revienta**, no pasa de largo

Un consumer que no puede deduplicar no puede cumplir la regla 6, así que no sigue: lanza `InvalidOperationException` y el mensaje acaba en la cola `_error`, donde se ve.

**Descartado — un `warning` y procesar igual.** Deja una puerta abierta que no avisa: el mensaje se procesa sin guarda, el log queda enterrado entre los `info` de EF Core y la regla 6 se incumple en silencio. Exactamente el modo de fallo que este proyecto está escrito para no tener.

MassTransit siempre rellena el `MessageId`, así que esta rama solo la pisa un mensaje inyectado a mano. **Y eso tiene un coste real: la receta de reposteo de CLAUDE.md necesita ahora `message_id` en `properties`.** Es un cambio en un flujo de trabajo que se usa en cada verificación de esta fase, así que se actualiza en CLAUDE.md y no solo aquí.

### 6. La entidad está **duplicada** en los dos `.Infrastructure`, y no se extrae

`ProcessedMessage` y su configuración son idénticas en Inventory y en Payments salvo el `namespace` y las referencias de los comentarios.

**Descartado — un proyecto `Shop133.Infrastructure` compartido.** No es una preferencia estética: los dos `.Infrastructure` tienen **cero `ProjectReference`** por diseño, y su `.csproj` lo dice por escrito, ni siquiera a `Shop133.Contracts`. Un proyecto común sería la primera grieta en esa regla, y además requiere permiso (CLAUDE.md: preguntar antes de añadir un proyecto). `Shop133.Contracts` tampoco vale — es para mensajes que viajan por RabbitMQ y la regla 4 lo mantiene sin EF Core.

Precedente directo: el bloque `AddMassTransit` (tres copias, revisado en `3.1`, `3.4` y `3.5` y confirmado como duplicación buena) y `SqlServerContainerFixture` (`2.4`). Y hay un argumento propio: las dos tablas viven en **bases distintas con dueños distintos**, así que compartir el tipo daría una sola cara a dos esquemas que la regla 1 obliga a poder evolucionar por separado.

### 7. La tabla vive en la base del servicio, no en una común

Marcar el mensaje y hacer el trabajo tienen que caber en la **misma transacción** (decisión 1). Dos bases no dan eso sin transacción distribuida, y una base transversal rompería la regla 1 de frente. Es la misma tabla en dos sitios porque es el único sitio donde puede estar.

Consecuencia que conviene tener escrita: **`ProcessedMessages` es la única tabla de esas bases que no es de negocio**. Inventory no gestiona mensajes, los recibe.

### 8. Ninguna regla de arquitectura nueva. La suite se queda en **15**

`0.6` ya dejó clasificadas las reglas 2, 6 y 7 como **de comportamiento**: no se pueden expresar sobre el grafo de referencias, y su verificación real es el harness de MassTransit en `3.7` y `4.7`. Una regla estructural del tipo "todo `*Consumer.cs` menciona `ProcessedMessages`" sería un `grep` disfrazado de test: pasa con un comentario y falla con un refactor legítimo.

Se dice por escrito en vez de inventar una, siguiendo el precedente explícito de `3.3` y `3.5`. Inventar una regla para subir el contador es exactamente el "filtro que nunca casa" contra el que avisó `3.2`.

---

## Cambios

### Nuevos — Inventory

| Archivo | Rol |
|---|---|
| [Inventory.Infrastructure/Entities/ProcessedMessage.cs](../src/Services/Inventory/Inventory.Infrastructure/Entities/ProcessedMessage.cs) | La entidad. `sealed`, setters privados, constructor con guardas y constructor privado para EF |
| [Inventory.Infrastructure/Persistence/Configurations/ProcessedMessageConfiguration.cs](../src/Services/Inventory/Inventory.Infrastructure/Persistence/Configurations/ProcessedMessageConfiguration.cs) | El mapeo: PK compuesta, `ValueGeneratedNever()`, longitudes desde las constantes |
| `Inventory.Infrastructure/Migrations/20260831194717_AddProcessedMessages.cs` (+ `.Designer.cs`, snapshot) | La tabla. Sin `HasData`, así que una sola migración |

### Nuevos — Payments

| Archivo | Rol |
|---|---|
| [Payments.Infrastructure/Entities/ProcessedMessage.cs](../src/Services/Payments/Payments.Infrastructure/Entities/ProcessedMessage.cs) | Copia literal de la de Inventory salvo `namespace` y comentarios — ver decisión 6 |
| [Payments.Infrastructure/Persistence/Configurations/ProcessedMessageConfiguration.cs](../src/Services/Payments/Payments.Infrastructure/Persistence/Configurations/ProcessedMessageConfiguration.cs) | Ídem |
| `Payments.Infrastructure/Migrations/20260831195426_AddProcessedMessages.cs` (+ `.Designer.cs`, snapshot) | La tabla en `PaymentsDb` |

### Modificados

| Archivo | Qué cambió |
|---|---|
| [Inventory.Infrastructure/Persistence/InventoryDbContext.cs](../src/Services/Inventory/Inventory.Infrastructure/Persistence/InventoryDbContext.cs) | `DbSet<ProcessedMessage>` + un `ApplyConfiguration` explícito |
| [Payments.Infrastructure/Persistence/PaymentsDbContext.cs](../src/Services/Payments/Payments.Infrastructure/Persistence/PaymentsDbContext.cs) | Ídem |
| [Inventory.API/Consumers/OrderCreatedConsumer.cs](../src/Services/Inventory/Inventory.API/Consumers/OrderCreatedConsumer.cs) | Guarda de transporte al entrar; `MarkProcessed(...)` en **los tres** caminos de salida; `MarkProcessed` extraído. El comentario "nada que guardar" de la rama de rechazo dejó de ser cierto y se reescribió |
| [Payments.API/Consumers/StockReservedConsumer.cs](../src/Services/Payments/Payments.API/Consumers/StockReservedConsumer.cs) | Lo mismo con **dos** caminos de salida |

### Lo que no se tocó

- **Ningún `.csproj` y ningún paquete NuGet.** Por eso la suite de arquitectura se queda en 15 sin tener que razonar nada más.
- **`Shop133.Contracts`.** El `MessageId` es del sobre; un campo de contrato rompería `ContractsRulesTests` y tres promesas escritas.
- **`Orders.API`** — sin consumer hasta `4.1`.
- **`Program.cs` de nadie.** La guarda no necesita registro: es código dentro del consumer (decisión 1). El bloque `AddMassTransit` sigue siendo tres copias y su próxima relectura sigue siendo `4.5`.
- **`tests/`** — el test de idempotencia es `3.7`.

---

## Detalles que cuestan tiempo

**El camino que no escribe nada necesita su propio `SaveChangesAsync`, y es fácil no verlo.** En las ramas normales la marca se cuela en el `Add` que ya había y viaja gratis en el `SaveChanges` existente. En la rama de rechazo de Inventory **la marca es la única escritura**, así que hay que guardar explícitamente. Si se copia el patrón de las otras ramas y se olvida el `SaveChanges`, no falla nada: el `Add` se queda en el `ChangeTracker`, el `DbContext` se descarta al acabar el scope del consumer y la marca no llega nunca a la base. El síntoma es que la deduplicación no funciona **solo en el camino de rechazo**, que es justo el que este punto venía a arreglar.

**`ConsumerName` es parte de la clave, y eso lo hace un dato de esquema.** `nameof(OrderCreatedConsumer)` parece una constante inofensiva. No lo es: renombrar la clase cambia el valor, las filas antiguas dejan de encontrarse y todos los mensajes ya procesados vuelven a parecer nuevos. Un renombrado de consumer necesita, como mínimo, pensarlo.

**Smart App Control volvió a bloquear, y esta vez el reintento sí bastó.** `Payments.API.exe` recién reconstruido falló al arrancar con *"An Application Control policy has blocked this file"* — el `0x800711C7` de siempre, porque el binario tiene bytes nuevos y por tanto hash nuevo. **Lanzarlo otra vez lo resolvió al primer intento**, sin `-c Release`. Es la contraparte de lo que midió `3.5`, donde ocho reintentos no lo despejaron: las dos cosas son ciertas y el orden correcto es reintentar primero y escalar a Release después.

**`sqlcmd -P $env:VARIABLE` con la variable vacía se come el flag siguiente.** `-P $env:MSSQL_SA_PASSWORD -C` con la variable sin definir en esa sesión de PowerShell hace que `-C` se interprete como la contraseña, así que el `-C` desaparece y el error que sale es **`SSL Provider: certificate verify failed: self-signed certificate`**. Es decir: el mensaje culpa al certificado cuando el problema es una variable de entorno vacía. Se pierde un rato buscando por el lado equivocado.

**El `max_length` de `sys.columns` está en bytes.** Un `nvarchar(200)` sale como `400` y un `nvarchar(250)` como `500`. Comprobar la migración contra la constante de la entidad sin traducir la unidad hace pensar que hay un error de mapeo donde no lo hay.

**Las estadísticas de la API de gestión de RabbitMQ van con retraso.** Un `DELETE .../contents` devuelve `204` y el siguiente `GET /api/queues/%2F` todavía dice `messages=1`. Parece que la purga falló y no falló; un segundo después el contador es correcto. Mismo efecto al mirar una cola justo después de publicar.

**Una cola espía sigue teniendo que ser `durable:true`** (RabbitMQ 4.x rechaza `transient_nonexcl_queues`) y su exchange tiene que existir ya como `fanout`/`durable`. Sin la cola espía este punto no se puede verificar de verdad: contar `StockRejected` publicados es la única forma de distinguir "se descartó el duplicado" de "se procesó otra vez y dio el mismo resultado", porque **el estado de la base es idéntico en los dos casos**.

**Y la trampa nueva de este punto: el sobre reposteado a mano necesita `message_id`.** A las tres conocidas —JSON sin BOM, vhost en la ruta, `content_type: application/vnd.masstransit+json` con el `messageType` en URN completo— se suma ésta desde hoy. Sin ella el broker sigue contestando `{"routed":true}` y el mensaje acaba en `order-created_error` por la decisión 5. Se ha actualizado la sección Commands de CLAUDE.md.

---

## Verificación

Ejecutado el 2026-08-31 contra un broker y cuatro bases reales. Salidas reales.

### 1. Build limpio

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### 2. La suite de arquitectura sigue en 15

```
=== TEST EXECUTION SUMMARY ===
   Shop133.ArchitectureTests  Total: 15, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 1.214s
```

### 3. Las dos migraciones, y la tabla que generan

```
src\Services\Inventory\Inventory.Infrastructure\Migrations\20260831194717_AddProcessedMessages.cs
src\Services\Payments\Payments.Infrastructure\Migrations\20260831195426_AddProcessedMessages.cs
```

```csharp
migrationBuilder.CreateTable(
    name: "ProcessedMessages",
    columns: table => new
    {
        MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
        ConsumerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
        MessageType = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
        ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
    },
    constraints: table =>
    {
        table.PrimaryKey("PK_ProcessedMessages", x => new { x.MessageId, x.ConsumerName });
    });
```

La PK compuesta está, y `MessageId` sale **sin `DEFAULT`** — el `ValueGeneratedNever()` hizo su trabajo. Aplicadas con `dotnet ef database update` y comprobadas contra `InventoryDb` conectando como `inventory_user`, nunca `sa`:

```
col               type              len  nullable
----------------- ----------------- ---- --------
MessageId         uniqueidentifier    16        0
ConsumerName      nvarchar           400        0
MessageType       nvarchar           500        0
ProcessedAt       datetimeoffset      10        0
```

### 4. Camino feliz: una marca por servicio

```
OrderId = 978e57d2-7365-4bdb-af63-05a9428b8d80
Status  = Pending
Total   = 149.00
```

```
=== InventoryDb.ProcessedMessages ===
6C5B0000-DCE1-6046-1140-08DF079E6ECD  OrderCreatedConsumer    Shop133.Contracts.Events.OrderCreated
=== PaymentsDb.ProcessedMessages ===
F0690000-DCE1-6046-68A1-08DF079E6FC2  StockReservedConsumer   Shop133.Contracts.Events.StockReserved
```

`MessageId` distintos, y es lo correcto: son dos mensajes distintos de la cadena, no el mismo viajando.

### 5. Reposteo del **mismo** `OrderCreated` con el **mismo** `message_id`

```
{"routed":true}
```

```
El mensaje 6c5b0000-dce1-6046-1140-08df079e6ecd ya lo procesó OrderCreatedConsumer
(pedido 978e57d2-7365-4bdb-af63-05a9428b8d80); se descarta.
```

Estado después, contra el de antes (`QuantityReserved = 5`, 7 reservas):

```
ProductId QuantityReserved      Reservas   Marcas
2         5                     7          1
=== PaymentsDb ===  Cobros 7   Marcas 1
```

Nada se movió. Y **Payments sigue con una sola marca**, que es la prueba de que no se republicó ningún `StockReserved`: la salida silenciosa de la decisión 3, observada.

### 6. El hueco que este punto existe para cerrar — el camino de rechazo

Cola espía ligada a `Shop133.Contracts.Events:StockRejected`, y el mismo mensaje de un producto inexistente publicado **dos veces con el mismo `message_id`**:

```
OrderId  = 7ae916ab-5026-4c15-95c4-aa02dfae254b
MessageId= 2e8d71e9-728e-4e53-bbcb-52ce9d6dd6dd
--- envio 1 ---  {"routed":true}
--- envio 2 (mismo message_id) ---  {"routed":true}

StockRejected publicados = 1   (antes de 3.6 habrian sido 2)
```

```
Stock rechazado para el pedido 7ae916ab-...: el producto 999999 no existe en el inventario.
El mensaje 2e8d71e9-... ya lo procesó OrderCreatedConsumer (pedido 7ae916ab-...); se descarta.
```

Y la fila que lo sostiene — una marca de un camino que **no escribió nada más**:

```
MessageId                             ConsumerName
2E8D71E9-728E-4E53-BBCB-52CE9D6DD6DD  OrderCreatedConsumer
ReservasDeEsePedido   0
MarcasTotales         2
```

Ésta es la verificación del punto. El estado de `StockItems` habría sido idéntico con o sin guarda; lo único que distingue los dos mundos es **cuántos `StockRejected` salieron**, y por eso hacía falta la cola espía.

### 7. Mismo pedido, `message_id` distinto: las dos guardas conviven

```
MessageId NUEVO = f6ac1c4b-5ea2-4d1d-8218-f405e16d5726  (pedido 978e57d2-... ya reservado)
{"routed":true}
ProductId QuantityReserved   ->  2  5      Marcas -> 3
```

```
=== inventory ===
El pedido 978e57d2-... ya tenía stock reservado (reserva del 08/31/2026 20:28:36 +00:00);
no se reserva de nuevo y se reenvía StockReserved.
=== payments ===
El pedido 978e57d2-... ya se había cobrado el 08/31/2026 20:28:38 +00:00 con resultado Completed;
no se vuelve a cobrar y se reenvía el desenlace guardado.
```

La cadena entera: la guarda de transporte **no** salta (es una entrega nueva), salta la de negocio, que no re-reserva —`QuantityReserved` sigue en 5— y republica; Payments recibe ese `StockReserved`, su guarda de transporte tampoco salta, y su guarda de negocio reenvía el desenlace guardado **con el `TransactionId` de la fila**:

```
OrderId                               Amount   TransactionId
978E57D2-7365-4BDB-AF63-05A9428B8D80  149.00   SIM-3B8CA1EA8FF3475CB922138BC36FF9D5
```

Un cobro, un identificador. La decisión 3 de `3.5` sigue en pie.

### 8. Reposteo del mismo `StockReserved` — la guarda de transporte de Payments

Cola espía en `Shop133.Contracts.Events:PaymentCompleted`:

```
--- repost StockReserved con el MISMO message_id ---
{"routed":true}

PaymentCompleted publicados tras el duplicado = 0   (0 = no se republico nada)
```

```
El mensaje f0690000-dce1-6046-68a1-08df079e6fc2 ya lo procesó StockReservedConsumer
(pedido 978e57d2-...); se descarta.
```

```
Cobros 7   Marcas 2
order-created         messages=0
order-created_error   messages=1
spy-payment-completed messages=0
stock-reserved        messages=0
```

Sin cola `stock-reserved_error`: el duplicado no falló, se descartó. Es el duplicado más caro que el sistema puede producir y no llegó a la pasarela.

### 9. Sobre sin `message_id` → cola de error

```
OrderId = efbd3159-8ee7-4bf0-b7bb-7500b8eea3c4  (sobre SIN messageId)
{"routed":true}

order-created         messages=0
order-created_error   messages=1
```

```
System.InvalidOperationException: El mensaje OrderCreated del pedido efbd3159-... llegó sin
MessageId en el sobre, así que no se puede deducir si es un duplicado. Todo mensaje publicado
por MassTransit lo lleva; si esto se ve, el mensaje se inyectó a mano sin la propiedad message_id.
```

Sin efectos: `ReservaDelPedidoSinId = 0` y el producto 3 sigue con `QuantityReserved = 0`. Nótese que `order-created_error` aparece **aquí por primera vez** — se crea de forma perezosa al primer fallo, así que su ausencia nunca prueba nada.

### 10. Nada de esto está automatizado

Igual que en `3.4` y `3.5`, todo lo de arriba se verificó a mano contra un broker y unas bases reales. **Automatizarlo con el harness en memoria es `3.7`**, y el roadmap ya dice que ese test es la única verificación fiable de este punto: a mano hay que republicar el mensaje y comparar estado de base de datos, que es exactamente lo que se acaba de hacer y lo que nadie va a repetir después de cada refactor.

Las colas espía se borraron y `order-created_error` se purgó al terminar.

---

## Pendiente

- **`3.7`** — `Inventory.Tests` y `Payments.Tests` con el harness en memoria, y el test de idempotencia por consumer: mismo `MessageId` dos veces, un solo efecto. Es lo que convierte todo lo verificado arriba en algo que sobrevive a un refactor. Ahí llegan además la cuarta y quinta copia de `SqlServerContainerFixture` y se decide por fin su extracción, y hay que quitarle a `Orders.Tests` la dependencia del broker real que estrenó `3.3`.
- **Retry, redelivery y colas `_error`** — `3.1` lo apuntó para el rango `3.4`–`3.6` y **no se ha gastado aquí**, deliberadamente. Sin la saga delante no hay caso con el que elegir número de reintentos ni backoff, y la única política que este punto ha necesitado es la de por defecto. Pasa a la Fase 4, con las compensaciones delante.
- **`4.5`** — el outbox transaccional. Además de lo que ya tenía asignado, ahora le toca **lo que la decisión 3 agrandó**: con la guarda de transporte, un proceso muerto entre el `COMMIT` y el `Publish` ya no se cura solo con la reentrega. Es el mismo agujero de siempre, pero sin la red de rebote que lo tapaba a medias.
- **Purga de `ProcessedMessages`** — sin dueño. La tabla crece una fila por mensaje y para siempre; nadie la limpia y no hay proceso que lo haga. Cuando aparezca, aparece con su índice sobre `ProcessedAt`, que hoy no existe a propósito.
- **Concurrencia** — dos entregas simultáneas del mismo `MessageId` pasan las dos la comprobación y chocan en el `INSERT` de la PK. No se captura el `DbUpdateException`: la reentrega de MassTransit encuentra la fila y se descarta, así que se autocura. Es la misma carrera que `3.4` y `3.5` dejaron anotada para `StockItems` sin concurrencia optimista, y ésa **sigue sin dueño**.
- **El precio de un pedido** — sin tocar aquí y sin relación con este punto. Sigue en `4.8`/`4.9`, tal como lo dejó la corrección 2b de [fase_3_3.md](fase_3_3.md).
