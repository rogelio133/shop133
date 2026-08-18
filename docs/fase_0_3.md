# Fase 0.3 — Crear proyecto `Shop133.Contracts` con eventos base

**Fecha:** 2026-08-17 · **Estado:** completado · **Roadmap:** [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md)

---

## Objetivo

Fijar **el vocabulario del sistema antes de que exista código que dependa de él**.

El proyecto `Shop133.Contracts` ya existía desde 0.1 y seis proyectos ya lo referenciaban, pero estaba vacío: cero archivos `.cs`. Lo que faltaba era el contenido — los tipos de mensaje que a partir de la Fase 3 cruzan RabbitMQ.

Está en este punto del roadmap y no en la Fase 3 por una razón concreta: cambiar un contrato es un breaking change **simultáneo** en varios servicios (regla 4 de CLAUDE.md). Discutir la forma de `OrderCreated` ahora, con los archivos vacíos, cuesta una tarde. Discutirla en la Fase 4, con una saga a medias y tres consumers escritos, cuesta reescribirlos todos a la vez.

Se definen 9 tipos de mensaje (7 eventos + 2 comandos) y 1 DTO compartido.

**Fuera de alcance deliberadamente:**

- **MassTransit.** No se instala aquí y no se instalará nunca *en este proyecto* — Contracts no referencia nada (regla 4). Entra en 3.1, en los servicios.
- **Consumers, exchanges, configuración de transporte.** Fase 3.
- **La `OrderStateMachine`.** Fase 4.1, en `Orders.Domain`.
- **Las bases de datos.** 0.4.

Este punto entrega tipos y nada más. No hay una sola línea ejecutable en él.

---

## Decisiones

### 1. Alcance: los 9 mensajes completos, no solo los 5 de la Fase 3.2

El roadmap menciona los eventos en dos sitios con listas distintas: el punto **3.2** nombra cinco (`OrderCreated`, `StockReserved`, `StockRejected`, `PaymentCompleted`, `PaymentFailed`), mientras que las convenciones de CLAUDE.md nombran siete eventos más dos comandos.

**Descartado:** entregar solo los cinco de 3.2 y añadir el resto cuando hicieran falta. Es lo defendible desde YAGNI, pero aquí el argumento se da la vuelta: el valor de este punto es *ver el flujo completo escrito de golpe*, incluido el camino de compensación. Con solo cinco eventos no se ve que `ReleaseStock` existe porque `PaymentFailed` llega **después** de `StockReserved` — que es la idea central del proyecto entero.

**Elegido:** los 9. La saga de la Fase 4 no tendrá que tocar Contracts para existir.

### 2. `Events/` y `Commands/` en namespaces separados — el namespace es el nombre del exchange

MassTransit deriva el nombre del exchange de RabbitMQ del **nombre completo del tipo**: `Shop133.Contracts.Events:OrderCreated`.

**Descartado:** una carpeta plana con los 10 tipos en `Shop133.Contracts`. Más simple de escribir, y es lo que uno hace por defecto con 10 archivos.

**Elegido:** `Events/` y `Commands/` con namespaces anidados. Dos motivos:

1. Mover un tipo de namespace más adelante **no es un refactor** — es renombrar un exchange de RabbitMQ y dejar huérfanos los mensajes que estuvieran en vuelo. Es de las pocas decisiones de este punto que salen caras si se cambian después.
2. La separación evento/comando no es cosmética. Un **evento** anuncia un hecho consumado en pasado y no le importa quién escuche; un **comando** va dirigido a un destinatario concreto y le pide que haga algo. `ReserveStock` no es "algo que pasó", es una orden a Inventory.API. Tenerlos en carpetas distintas obliga a elegir conscientemente al añadir uno nuevo.

`OrderLine` se queda en la raíz `Shop133.Contracts` porque lo usan ambos lados. Al estar los otros namespaces anidados dentro, se resuelve sin `using` adicional — verificado.

### 3. `CustomerEmail` viaja dentro de `OrderConfirmed` y `OrderCancelled`

**Descartado:** que estos eventos lleven solo el `OrderId` y que Notifications.API busque el email.

Pero, ¿dónde lo busca? Notifications.API no tiene base de datos propia y **no puede** abrir `OrdersDb` — regla 1, una base de datos por servicio. Las únicas salidas serían una llamada HTTP de vuelta a Orders.API (justo el acoplamiento síncrono que la Fase 3 existe para eliminar) o darle una base de datos y un consumidor de `OrderCreated` para mantener su propia copia del email.

**Elegido:** el dato viaja en el evento.

Esto es el principio general y conviene tenerlo escrito: **un evento debe llevar todo lo que su consumidor necesita para actuar**, porque no puede ir a buscar el resto. La duplicación de datos entre servicios no es un defecto del diseño, es el precio de no compartir base de datos. Mismo motivo por el que `ReserveStock` lleva las líneas completas y no solo el `OrderId`.

### 4. `Guid` para `OrderId` y `ProductId` — y esto condiciona la Fase 1.1

**Descartado:** `int` autoincremental, que es lo natural con EF Core y SQL Server, y lo que la mayoría escribiría en 1.1 sin pensarlo.

**Elegido:** `Guid`. El productor puede generar el identificador **sin consultar a nadie**. Con un `int` autoincremental el id no existe hasta que la base de datos hace el `INSERT`, así que Orders.API tendría que ir a la base, esperar el id y solo entonces publicar `OrderCreated`. Con `Guid` puede generarlo, publicarlo y persistir, o incluso correlacionar antes de tocar la base.

**Consecuencia que hay que recordar:** el punto **1.1** tiene que definir `Product.Id` como `Guid`, no como `int`. Es una decisión tomada aquí que se cobra en otra fase, y por eso está escrita.

> **⚠️ Revisado en 1.1 (2026-08-18): la mitad de esta decisión se revirtió.** `OrderId` sigue siendo `Guid`; **`ProductId` pasó a `int`** y `OrderLine.ProductId` se cambió en consecuencia.
>
> El fallo está en el párrafo de arriba: justifica **los dos** ids con un solo argumento, y el argumento —que el productor acuña el id sin consultar a nadie— solo es decisivo para `OrderId`, que es la clave de correlación de la saga y tiene que existir antes de tocar la base. Un producto lo crea Catalog con un `POST` síncrono sobre su propia base; no hay nada que adelantar. El razonamiento se extendió a `ProductId` por arrastre, no por comprobarlo.
>
> El texto original se conserva tal cual: es el registro de lo que se decidió y con qué motivo. El desarrollo completo, con las alternativas descartadas, está en la **decisión 2 de [fase_1_1.md](fase_1_1.md)**.

### 5. Sin `CorrelationId` ni `OccurredAt` en los contratos

**Descartado:** añadir `Guid CorrelationId` a cada mensaje. Es lo que MassTransit detecta por convención sin configurar nada, así que es la opción de menor fricción.

**Elegido:** `OrderId` es la clave de correlación. La saga lo configura explícitamente:

```csharp
Event(() => OrderCreated, x => x.CorrelateById(m => m.Message.OrderId));
```

Un `CorrelationId` al lado de un `OrderId` que siempre valdría lo mismo son dos fuentes de verdad para el mismo dato. Y la correlación de esta saga *es* un dato de negocio: lo que agrupa los mensajes es que pertenecen al mismo pedido, no un identificador técnico que se arrastra. Se paga una línea de configuración en la Fase 4 a cambio de que los contratos no tengan campos de infraestructura.

Lo mismo con un `DateTime OccurredAt`: MassTransit ya pone `MessageId`, `ConversationId` y `SentTime` en el **sobre** del mensaje, fuera del payload. La idempotencia de la regla 6 usa el `MessageId` del sobre, no un campo del contrato.

### 6. `ReserveStock`/`ReleaseStock` reutilizan `OrderLine` aunque Inventory ignore `UnitPrice`

Inventory.API no necesita saber precios. Con rigor, un comando debería llevar exactamente lo que su destinatario necesita.

**Descartado:** un `StockLine { ProductId, Quantity }` aparte. Es más correcto, pero deja dos tipos casi idénticos que hay que mantener en paralelo y traducir uno al otro en la saga.

**Elegido:** un solo `OrderLine`. Es una concesión consciente, no un descuido: se acepta enviar un campo que un consumidor no mira a cambio de no duplicar el tipo. Queda escrito aquí para que dentro de tres meses no parezca un olvido.

### 7. Propiedades `required` + `init`, no records posicionales

**Descartado:** `public sealed record OrderCreated(Guid OrderId, string CustomerEmail, ...)`. Una línea por evento, mucho más compacto.

**Elegido:** el cuerpo con propiedades. Dos motivos:

- En el sitio de construcción se ve el **nombre** de cada campo. Un `new PaymentCompleted(orderId, 39.98m, "tx-1")` obliga a ir a la definición para saber qué es cada valor.
- Reordenar parámetros posicionales compila sin avisar y cambia el significado. Con nombres, no hay orden que romper.

El coste son cinco líneas por archivo en vez de una. En un proyecto cuyo objetivo declarado es leerse, sale a cuenta.

---

## Cambios

Diez archivos nuevos. **El `.csproj` no se tocó** — sigue sin un solo `PackageReference` ni `ProjectReference`, que es lo que hace verificable la regla 4.

| Archivo | Rol |
|---|---|
| [OrderLine.cs](../src/Shared/Shop133.Contracts/OrderLine.cs) | DTO compartido: `ProductId`, `Quantity`, `UnitPrice`. Namespace raíz. |
| [Events/OrderCreated.cs](../src/Shared/Shop133.Contracts/Events/OrderCreated.cs) | Orders.API acepta un pedido. Arranca la saga. |
| [Commands/ReserveStock.cs](../src/Shared/Shop133.Contracts/Commands/ReserveStock.cs) | La saga pide a Inventory que descuente stock. |
| [Events/StockReserved.cs](../src/Shared/Shop133.Contracts/Events/StockReserved.cs) | Inventory reservó. **Aquí empieza lo que hay que compensar.** |
| [Events/StockRejected.cs](../src/Shared/Shop133.Contracts/Events/StockRejected.cs) | Inventory no pudo. Nada que compensar. |
| [Events/PaymentCompleted.cs](../src/Shared/Shop133.Contracts/Events/PaymentCompleted.cs) | Payments cobró. |
| [Events/PaymentFailed.cs](../src/Shared/Shop133.Contracts/Events/PaymentFailed.cs) | Payments rechazó — **con el stock ya reservado**. |
| [Commands/ReleaseStock.cs](../src/Shared/Shop133.Contracts/Commands/ReleaseStock.cs) | La compensación. |
| [Events/OrderConfirmed.cs](../src/Shared/Shop133.Contracts/Events/OrderConfirmed.cs) | Estado final feliz. Lo consume Notifications. |
| [Events/OrderCancelled.cs](../src/Shared/Shop133.Contracts/Events/OrderCancelled.cs) | Estado final por cualquiera de los dos caminos de error. |

**El flujo completo, en el orden en que ocurre:**

```
OrderCreated → ReserveStock ─┬→ StockReserved → PaymentCompleted → OrderConfirmed
                             │                └→ PaymentFailed → ReleaseStock → OrderCancelled
                             └→ StockRejected ────────────────────────────────→ OrderCancelled
```

La segunda rama del medio es el proyecto entero: `PaymentFailed` llega cuando ya existe una reserva, y el único camino a `OrderCancelled` desde ahí pasa por `ReleaseStock`.

**Documentación en el propio código:** cada tipo lleva un `///` en español que dice **quién lo publica y quién lo consume**. Los campos ya se leen solos; el productor y el consumidor no aparecen en ninguna parte del tipo.

Otros archivos tocados: [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md) (checkbox 0.3), [docs/README.md](README.md) (fila del índice) y [CLAUDE.md](../CLAUDE.md) (nota de estado). De paso se corrigieron tres enlaces a `plan-desarrollo-ishop.md`, archivo que no existe — el nombre real es `plan-desarrollo-shop133.md`.

---

## Detalles que cuestan tiempo

Cuatro cosas, y dos de ellas salieron de la verificación, no de escribir el código.

**La igualdad por valor de los `record` NO entra en las colecciones.** Esto se descubrió al probarlo, y es contraintuitivo:

```csharp
var a = new OrderCreated { ..., Lines = [line] };
var b = JsonSerializer.Deserialize<OrderCreated>(JsonSerializer.Serialize(a));
a == b;   // False
```

Los `record` comparan cada miembro con `EqualityComparer<T>.Default`, y para `IReadOnlyList<OrderLine>` eso acaba en la igualdad por referencia de `List<T>`. Dos mensajes con contenido idéntico no son iguales si las listas son instancias distintas.

No afecta a la mensajería — MassTransit nunca compara mensajes por valor — pero **rompería cualquier assert de test** que dé por hecho que `record` significa igualdad estructural. Se decidió no arreglarlo: la alternativa sería un tipo de colección con igualdad estructural propia, y eso es lógica dentro de Contracts, que la regla 4 prohíbe.

**System.Text.Json sí valida `required` al deserializar.** Verificado con un payload al que le faltaban campos:

```
JSON deserialization for type 'Shop133.Contracts.Events.OrderCreated' was missing
required properties including: 'CustomerEmail', 'Lines', 'Total'.
```

Es el comportamiento deseado: un mensaje incompleto falla al entrar, no 200 líneas más adelante con un `null` inesperado. **Pendiente de confirmar en la Fase 3** que el serializador que MassTransit 8 configura por defecto conserva esta validación — lo verificado aquí es System.Text.Json a pelo.

**PowerShell 5.1 no puede cargar un ensamblado `net10.0`.** El primer intento de listar los tipos por reflexión desde PowerShell falló:

```
Could not load file or assembly 'System.Runtime, Version=10.0.0.0'
```

No es un problema del ensamblado: PowerShell 5.1 corre sobre .NET Framework y no puede cargar assemblies de .NET moderno. Para inspeccionar un binario de este repo hay que hacerlo desde un proceso .NET 10 (ver *Verificación*), o desde PowerShell 7.

**Los namespaces anidados evitan un `using`.** `Shop133.Contracts.Commands.ReserveStock` ve `Shop133.Contracts.OrderLine` sin declarar nada, porque el compilador busca hacia fuera por los namespaces contenedores. Es lo esperado, pero conviene saberlo antes de añadir un `using Shop133.Contracts;` que el compilador marcaría como innecesario.

---

## Verificación

Ejecutado el 2026-08-17. Salidas reales:

| Check | Resultado |
|---|---|
| `dotnet build shop133.slnx` | **Build succeeded. 0 Warning(s), 0 Error(s)** — los 11 proyectos |
| Archivos `.cs` en Contracts (sin `obj`/`bin`) | 10 |
| `PackageReference`/`ProjectReference` en el `.csproj` | ninguno |
| Ensamblados referenciados por el `.dll` compilado | `System.Runtime`, `System.Collections` — y nada más |
| Tipos públicos exportados | 10, todos `sealed` |
| Round-trip System.Text.Json de `OrderCreated` | correcto |
| Deserializar con campos `required` ausentes | lanza `JsonException` (correcto) |
| Igualdad por valor tras round-trip | **`False`** — ver *Detalles* |
| `docker compose config` | válido — la Fase 0.2 sigue intacta |

**Cómo se verificó.** Que `dotnet build` pase no demuestra gran cosa aquí: **ningún proyecto usa todavía estos tipos**, así que compilarían igual estando mal. Se creó un proyecto de consola desechable **fuera del repo** (en el scratchpad, no versionado), con una `ProjectReference` a `Shop133.Contracts`, que hace lo que ningún proyecto del repo hace aún: construir los mensajes, serializarlos y listar los tipos por reflexión desde un proceso .NET 10.

Salida real del listado de tipos:

```
Shop133.Contracts.Commands.ReleaseStock       sealed=True  [OrderId, Lines]
Shop133.Contracts.Commands.ReserveStock       sealed=True  [OrderId, Lines]
Shop133.Contracts.Events.OrderCancelled       sealed=True  [OrderId, CustomerEmail, Reason]
Shop133.Contracts.Events.OrderConfirmed       sealed=True  [OrderId, CustomerEmail]
Shop133.Contracts.Events.OrderCreated         sealed=True  [OrderId, CustomerEmail, Lines, Total]
Shop133.Contracts.Events.PaymentCompleted     sealed=True  [OrderId, Amount, TransactionId]
Shop133.Contracts.Events.PaymentFailed        sealed=True  [OrderId, Reason]
Shop133.Contracts.Events.StockRejected        sealed=True  [OrderId, Reason]
Shop133.Contracts.Events.StockReserved        sealed=True  [OrderId]
Shop133.Contracts.OrderLine                   sealed=True  [ProductId, Quantity, UnitPrice]
total: 10
```

La lista de ensamblados referenciados (`System.Runtime` y `System.Collections`, nada más) es la prueba concreta de la regla 4: no es que el `.csproj` esté limpio, es que el binario compilado no depende de nada.

El proyecto de prueba **no se añadió al repo**. `git status` tras el punto muestra solo los tres paths de Contracts. El proyecto de tests de verdad es 8.2.

---

## Pendiente

De la Fase 0 quedan:

- **0.4** — crear `CatalogDb`, `OrdersDb`, `InventoryDb`, `PaymentsDb`.
- **0.5** — convención de branches.

Consecuencias de este punto en fases posteriores, para no perderlas de vista:

- ~~**Fase 1.1** — `Product.Id` tiene que ser `Guid`, por la decisión 4.~~ **Revertido en 1.1:** `Product.Id` es `int` y `OrderLine.ProductId` también. Ver la nota de la decisión 4 y [fase_1_1.md](fase_1_1.md).
- **Fase 3.1** — confirmar que el serializador de MassTransit 8 conserva la validación de `required`.
- **Fase 3.6** — la idempotencia usa el `MessageId` del sobre de MassTransit, no un campo de estos contratos.
- **Fase 4.1** — la saga correlaciona con `.CorrelateById(x => x.Message.OrderId)`; sin esa línea no hay correlación, porque no hay `CorrelationId` en los mensajes.
