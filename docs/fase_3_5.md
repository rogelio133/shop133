# Fase 3.5 — Payments.API consume `StockReserved`, simula el cobro y publica `PaymentCompleted`/`PaymentFailed`

**Fecha:** 2026-08-31 · **Estado:** completado · **Roadmap:** [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md)

---

## Objetivo

Cerrar la cadena de coreografía de la Fase 3. Desde `3.4`, Inventory.API publica `StockReserved` en un exchange fanout **sin colas ligadas** — el mismo vacío en el que caía `OrderCreated` antes de `3.4`. Este punto pone a alguien al otro lado y con eso el recorrido queda entero:

```
POST /orders → OrderCreated → StockReserved → PaymentCompleted / PaymentFailed
```

Cuatro servicios, tres saltos, y **ni una sola llamada HTTP entre ellos**. Es el contraste que la Fase 2 dejó preparado: en `2.3` un Catalog caído devolvía `502` y no se creaba el pedido.

Además vencían aquí dos deudas concretas:

1. **La comprobación de que la decisión 1 de [fase_3_2.md](fase_3_2.md) era correcta.** Aquel punto le añadió un campo `Amount` a `StockReserved` que Inventory no usa, sostenido sobre la afirmación de que Payments no tendría de dónde sacar el importe. Su sección *Pendiente* lo dejó escrito: "`3.5` — Payments.API consume `StockReserved` y ya tiene de dónde sacar el importe. **Es la comprobación de que la decisión 1 era correcta.**" Lo es: el consumer no tiene ningún otro sitio del que leerlo.
2. **La revisión del bloque `AddMassTransit` triplicado**, que la decisión 7 de [fase_3_1.md](fase_3_1.md) aplazó y la decisión 8 de [fase_3_4.md](fase_3_4.md) agendó explícitamente aquí, "con la copia de Payments ya tocada". Se cierra — ver decisión 7.

**Fuera de alcance deliberadamente:**

| Queda fuera | Entra en |
|---|---|
| Idempotencia por `MessageId` del sobre | `3.6` — aquí solo hay idempotencia de negocio, ver decisión 3 |
| Tests del consumer con `AddMassTransitTestHarness` | `3.7` (`Payments.Tests`) |
| Consumir `PaymentCompleted`/`PaymentFailed` | La saga, `4.2`/`4.3` — hoy se publican al vacío |
| Liberar el stock cuando el cobro se rechaza | `4.4` — ver el detalle medido en *Verificación 6* |
| Validar la **autenticidad** del importe | `4.8`/`4.9` — la guarda de `Amount <= 0` no es eso |
| Devolver un cobro (`Refund`) | Ningún punto del roadmap: la compensación de la Fase 4 libera stock, no dinero |
| Concurrencia optimista (`rowversion`) | Sin dueño; mismo hueco que `StockItems` desde `3.4` |
| `Dockerfile` y servicio de compose para Payments | Sin fecha; hoy solo Catalog está contenerizado |
| Endpoints HTTP en Payments.API | No hay ninguno, y no hace falta |

---

## Decisiones

### 1. Payments estrena base de datos: proyecto nuevo `Payments.Infrastructure` y `PaymentsDb`

El *Solution layout* de [CLAUDE.md](../CLAUDE.md) solo listaba `Payments.API`, así que crear un proyecto exigía preguntar. Se preguntó y se creó, con el mismo trámite que `Inventory.Infrastructure` en `3.4`.

Y como allí, no era una elección libre una vez decidido que hubiera persistencia: `EfCorePackages_LiveOnlyIn_InfrastructureProjects` prohíbe `Microsoft.EntityFrameworkCore.SqlServer` en un `.API`, y `DbContextFiles_LiveOnlyIn_OwningServiceInfrastructure` exige que un `*DbContext.cs` viva en `src/Services/<S>/<S>.Infrastructure/`. Meter la persistencia en `Payments.API` habría puesto la suite en rojo el mismo día.

**Sin `Payments.Domain`**, por el criterio de la decisión 1 de [fase_1_1.md](fase_1_1.md) y de la decisión 1 de [fase_3_4.md](fase_3_4.md): la capa de dominio existe donde vive la saga, que es `Orders.Domain`. Aquí hay un registro de cobros. Y `Payments.Infrastructure` no tiene **ningún** `ProjectReference`, ni siquiera a `Shop133.Contracts` — quien traduce un `StockReserved` en un cobro es el consumer, y el consumer vive en la API.

**Descartado — el consumer sin estado**, que es la lectura literal del roadmap ("consume `StockReserved`, simula cobro, publica") y el punto más pequeño posible: ni proyecto, ni base, ni paquetes.

Lo tumba una sola frase: **sin fila que consultar, el consumer no puede ser idempotente de ninguna forma.** RabbitMQ garantiza *al menos* una entrega, así que un `StockReserved` reentregado cobraría el pedido dos veces y publicaría dos `PaymentCompleted` con `TransactionId` distinto. Es el duplicado más caro que este sistema puede producir, y no es hipotético: es el escenario 4 obligatorio de la Fase 4. Inventory se libró gratis porque la PK de `StockReservations` es el `OrderId` (decisión 7 de `3.4`); aquí había que escribirlo.

El segundo motivo es que da un sitio donde vive el `TransactionId`. El `///` de `PaymentCompleted` dice desde `0.3` que ese campo existe "porque es lo que permitiría emitir la devolución si hubiera que compensar el pago" — y un identificador de cobro que no se guarda en ninguna parte no permite nada. Era una promesa que el código no cumplía.

### 2. Y esto **no** contradice la decisión 2 de `3.2`, que descartó darle base de datos a Payments

Conviene leerlo con cuidado porque a primera vista es una marcha atrás, y no lo es.

Lo que [fase_3_2.md](fase_3_2.md) descartó fue una base de datos **para conseguir el importe**: un consumer de `OrderCreated` en Payments que persistiera `(OrderId, Total)` y lo releyera al llegar `StockReserved`. La tumbó una carrera real — RabbitMQ no ordena entre colas distintas, así que `StockReserved` puede llegar **antes** de que Payments haya procesado `OrderCreated`, y el consumer tendría que aplazar el mensaje.

**Ese descarte sigue en pie palabra por palabra.** No hay ningún consumer de `OrderCreated` en Payments, y el importe sigue llegando dentro de `StockReserved.Amount`, exactamente como `3.2` decidió. Esta tabla no lo cambia: se escribe *después* de resolver el cobro, con el importe que traía el mensaje, y nunca se lee para averiguar cuánto vale un pedido.

La base entra por un motivo distinto —la idempotencia— que `3.2` no estaba evaluando. Se deja escrito así, y no disimulado como si siempre hubiera estado previsto.

### 3. La clave primaria **es** el `OrderId`, y eso es idempotencia de negocio — que no es la de `3.6`

`Payment` no tiene identidad propia: la PK es el `OrderId` que acuñó Orders.API, viajó en `OrderCreated`, Inventory copió a `StockReserved` y aquí llega por tercera vez. Calcado de `StockReservation` en `3.4`, y por las mismas dos razones: la única forma en que alguien va a buscar un cobro es por su pedido, y así **dos `StockReserved` del mismo pedido no caben en la tabla**.

El consumer comprueba la fila *antes* de cobrar y, si existe, **reenvía el desenlace guardado** en vez de salir en silencio — si el mensaje se repite es que algo se perdió, y puede haber sido la respuesta.

**Lo que hace que el reenvío tenga que salir de la fila y no de una variable local**: el `TransactionId` que se republica es el que ya estaba guardado. Acuñar uno nuevo daría dos identificadores para un mismo cobro, que es exactamente lo que la tabla viene a impedir. Medido en la verificación 7.

**Esto no es `3.6` y no lo sustituye.** `3.6` va por el `MessageId` del sobre, vale para cualquier consumer y cubre también los caminos que no escriben nada. Esta guarda es más estrecha: solo sabe de pedidos. Coexisten, igual que en Inventory.

### 4. El rechazo es determinista y por importe, no aleatorio

`Amount > Payments:DeclineAmountAbove` (por defecto `1000.00`) → `PaymentFailed`. El umbral vive en `appsettings.json` y se enlaza a `PaymentSimulationOptions`.

El criterio que decide es la Fase 4: el **escenario 3 obligatorio** —stock reservado y pago rechazado, o sea la compensación, que es el núcleo del proyecto— tiene que poder **forzarse a demanda**. Con este umbral, forzarlo es "pide más caro". El valor por defecto está elegido contra el catálogo de `1.4`, cuyo producto más caro son `399.00`: un pedido normal pasa solo y hay que quererlo para llegar al rechazo.

**Descartado — un porcentaje de fallo aleatorio** (`Payments:FailureRate`), que es lo que más se parece a una pasarela real. Haría que el escenario 3 llegara **por suerte** en vez de a demanda, y un test del harness sobre un consumer aleatorio o inyecta el `Random` detrás de una interfaz —infraestructura para simular una simulación— o es intermitente, que es la peor clase de test que se puede dejar en un repo.

**Descartado — un interruptor global** `Payments:AlwaysDecline`. Determinista y aún más simple, pero no depende del pedido: no permite que en la misma ejecución del sistema convivan un pedido que pasa y otro que falla, que es justo lo que la página de estado de `6.5` y la traza de `7.4` existen para enseñar. Con el umbral, los dos caminos se ven a la vez sin tocar la configuración.

**En `appsettings.json` y no en User Secrets**, con el precedente explícito de `Services:CatalogBaseUrl` en `2.3`: no es un secreto, y es una regla de negocio de mentira que interesa que se lea de un vistazo. Y **sin guarda que reviente al arrancar**, al contrario que todas las claves de `ConnectionStrings` de este repo: la diferencia no es de estilo — sin connection string el servicio no puede hacer nada y conviene que muera diciendo qué falta, mientras que aquí hay un valor por defecto sensato y una guarda convertiría un archivo opcional en obligatorio a cambio de nada.

### 5. La segunda guarda, `Amount <= 0`, y lo que **no** es

El consumer también rechaza un importe no positivo, con su propio motivo.

Es alcanzable hoy, y no como curiosidad: la decisión 2 de [fase_3_3.md](fase_3_3.md) dejó que el cuerpo del `POST` traiga el precio, así que un cliente puede pedir a `0`. Que Payments se niegue a cobrar cero es lo mínimo razonable.

**Y hay que decir lo que no hace, porque es fácil de confundir con el arreglo del agujero:** esto **no** valida la autenticidad de la foto de precios. Un pedido de un producto que existe a `0.01` supera esta guarda, supera el umbral y **se cobra un céntimo**, exactamente como la corrección 2b de `3.3` dejó anotado. El dueño de ese hueco sigue siendo `4.8`/`4.9`. Aquí solo se rechaza el caso más obvio, no el caso real.

### 6. Dos factorías estáticas en vez de un constructor público

`Payment.Completed(orderId, amount, transactionId)` y `Payment.Declined(orderId, amount, reason)`, con el constructor privado.

Es lo que hace **imposible** una fila con `Status = Failed` y `TransactionId` relleno, o una `Completed` sin él. Un constructor con los cinco argumentos deja esa invariante en manos de quien llama, y aquí hay exactamente dos llamantes que no deben poder equivocarse.

**Descartado — un `CHECK` constraint en la tabla** que ate `Status` con las dos columnas anulables. Diría lo mismo una segunda vez y en otro idioma, y el día que las dos versiones divergieran habría que leer las dos para saber cuál manda. La invariante ya la sostiene el único camino que existe para crear una fila.

**Sin `Refund()` ni `Retry()`**, siguiendo el precedente de `Product` sin `Update()` hasta `1.3`, de `Order` sin `Confirm()` y de `StockItem` sin `Release()`: no se inventa una firma antes de que exista su llamante. Aquí ni siquiera está claro que vaya a existir — la compensación de la Fase 4 libera stock, no dinero.

### 7. El bloque `AddMassTransit` **no se extrae**, y la revisión se cierra

La decisión 7 de [fase_3_1.md](fase_3_1.md) dejó tres copias literales y aplazó la pregunta; la decisión 8 de [fase_3_4.md](fase_3_4.md) la volvió a aplazar con una cita textual: *"Se vuelve a mirar en `3.5`, con la copia de Payments ya tocada."* Ya está tocada, y la respuesta con las tres delante es la misma que con dos.

**No se extrae, y esta vez la revisión se da por cerrada en lugar de reprogramarse.** Lo único que ha divergido entre las tres copias es el `x.AddConsumer<...>()` — precisamente la parte que no se puede compartir. Sacar a un método común lo que sí es idéntico (el host y el formatter) dejaría la única línea que distingue a cada servicio suelta fuera de él, y eso se lee peor que la duplicación. Además exigiría un proyecto nuevo que los tres referencien y que cargue el paquete de MassTransit, cosa que `Shop133.Contracts` no puede hacer sin romper la regla 4.

**El siguiente punto de relectura es `4.5`, y esta vez con una divergencia de verdad**: el outbox transaccional mete `MassTransit.EntityFrameworkCore` y una configuración de persistencia **solo en Orders**. Eso sí es una diferencia estructural entre las copias, no una línea.

### 8. Ninguna regla de arquitectura nueva. La suite se queda en 15.

`3.1` la subió a 13, `3.2` a 14, `3.4` a 15. `3.3` no añadió ninguna y lo dijo por escrito; este punto tampoco.

Todo lo que `3.5` introduce ya está vigilado: el sitio del consumer por `ConsumerFiles_LiveOnlyIn_ServiceApiConsumersFolder` (la regla que estrenó `3.4`, que aquí atrapa su segundo caso real), el paquete de EF Core por `EfCorePackages_LiveOnlyIn_InfrastructureProjects`, la ubicación del `DbContext` por `DbContextFiles_LiveOnlyIn_OwningServiceInfrastructure` y la versión 8.x de MassTransit por `PackageRulesTests`. `ProjectGraph` descubre `Payments.Infrastructure.csproj` solo, sin registrarlo en ninguna parte.

**Se descarta inventar una regla para subir el número.** El criterio que `3.2` dejó escrito es que una regla se verifica rompiéndola a propósito, porque *un filtro que nunca casa pasa en verde para siempre*; una regla escrita para que el contador suba es exactamente eso.

---

## Cambios

### Nuevos — Payments.Infrastructure

| Archivo | Rol |
|---|---|
| [Payments.Infrastructure.csproj](../src/Services/Payments/Payments.Infrastructure/Payments.Infrastructure.csproj) | Cero `ProjectReference`; `Microsoft.EntityFrameworkCore.SqlServer` 10.0.8 |
| [Entities/Payment.cs](../src/Services/Payments/Payments.Infrastructure/Entities/Payment.cs) | La entidad. PK = `OrderId`, dos factorías, sin `Refund()` |
| [Entities/PaymentStatus.cs](../src/Services/Payments/Payments.Infrastructure/Entities/PaymentStatus.cs) | `enum` con valores explícitos (`Completed = 1`, `Failed = 2`) |
| [Persistence/PaymentsDbContext.cs](../src/Services/Payments/Payments.Infrastructure/Persistence/PaymentsDbContext.cs) | La sesión con `PaymentsDb`, como `payments_user` |
| [Persistence/Configurations/PaymentConfiguration.cs](../src/Services/Payments/Payments.Infrastructure/Persistence/Configurations/PaymentConfiguration.cs) | `ValueGeneratedNever()`, `HasPrecision(18,2)`, sin índices |
| `Migrations/20260831175642_InitialCreate.cs` (+ `.Designer.cs`, snapshot) | La tabla `Payments`. **Sin `HasData`** |

### Nuevos — Payments.API

| Archivo | Rol |
|---|---|
| [Consumers/StockReservedConsumer.cs](../src/Services/Payments/Payments.API/Consumers/StockReservedConsumer.cs) | El consumer. Cola `stock-reserved` |
| [PaymentSimulationOptions.cs](../src/Services/Payments/Payments.API/PaymentSimulationOptions.cs) | `DeclineAmountAbove`, con el porqué de que sea determinista |

### Modificados

| Archivo | Qué cambió |
|---|---|
| [Payments.API/Program.cs](../src/Services/Payments/Payments.API/Program.cs) | Guarda de `ConnectionStrings:PaymentsDb` + `AddDbContext` + `Configure<...>` + `AddConsumer`. Reescritos los comentarios de `3.1` que ya eran falsos |
| [Payments.API/Payments.API.csproj](../src/Services/Payments/Payments.API/Payments.API.csproj) | `Microsoft.EntityFrameworkCore.Design` 10.0.8 `PrivateAssets="all"` + `ProjectReference` a `Payments.Infrastructure` |
| [Payments.API/appsettings.json](../src/Services/Payments/Payments.API/appsettings.json) | Sección `Payments` con `DeclineAmountAbove: 1000.00` |
| [shop133.slnx](../shop133.slnx) | El proyecto nuevo, bajo `/src/Services/Payments/` |

### Lo que no se tocó

**`Shop133.Contracts` no cambió ni una línea.** `StockReserved`, `PaymentCompleted` y `PaymentFailed` existen desde `0.3` y se revisaron en `3.2`; este punto es su **primer uso**, no su revisión — que es precisamente lo que convierte a `3.5` en la prueba de que aquella revisión acertó. Tampoco `db/init/01-create-databases.sql` ni `.env.example`: `PaymentsDb`, `payments_user` y `PAYMENTS_DB_PASSWORD` existen desde `0.4`. Ni los dos `docker-compose*.yml` (Payments no se contenedoriza aquí), ni `Inventory.*`, `Orders.*` o `Catalog.*`, ni un solo archivo de `tests/`.

---

## Detalles que cuestan tiempo

**Smart App Control bloqueó los ensamblados propios del repo, no un paquete de NuGet, y reintentar no lo arregló — pero compilar en Release sí.** `dotnet ef migrations add` falló con `Could not load assembly 'Payments.Infrastructure'`, un mensaje que apunta a un `ProjectReference` que falta y que era perfectamente correcto. Con `-v` aparece la causa real: `FileLoadException … An Application Control policy has blocked this file. (0x800711C7)`. Ocho reintentos espaciados 20 s no lo levantaron, y después el bloqueo se extendió a `Payments.API.dll`, de modo que ni `dotnet run` arrancaba.

Dos cosas que este episodio añade a lo que [fase_1_7.md](fase_1_7.md) dejó escrito. Primera: **el bloqueo no es cosa de los paquetes restaurados** — aquí las víctimas fueron dos DLL escritas por el propio repo, sin firmar como todas las demás. La diferencia es que su *contenido* era nuevo, y SAC decide por hash. Segunda, y es el remedio: **`dotnet build -c Release` produce bytes distintos, o sea un hash distinto, y eso fuerza una evaluación nueva que pasó a la primera.** A partir de ahí, `dotnet ef … --configuration Release` funcionó sin más. Sigue sin ser aceptable desactivar Smart App Control: es irreversible sin reinstalar Windows.

**`ValueGeneratedNever()` se comprueba leyendo el SQL de la migración, no confiando.** La columna tiene que salir `OrderId uniqueidentifier NOT NULL` **sin `DEFAULT`**. Si la línea faltara, EF le pondría un valor generado a una clave que acuña otro servicio, y el síntoma no sería un error sino filas con un `OrderId` que no corresponde a ningún pedido.

**El `decimal` pierde los ceros a la derecha por el camino, y se nota en los logs.** Un pedido de `1197.00` llega al consumer como `1197`: `SystemTextJsonMessageSerializer` lo serializa como cadena y el `.00` no sobrevive (ya medido en `3.3`). Por eso el log estructurado imprime `por 1197` mientras el motivo del rechazo, formateado con `:0.00`, dice `el importe 1197.00 supera…`. En la base no importa —la columna es `decimal(18,2)`— pero un log que enseña las dos formas del mismo número a dos líneas de distancia parece un error y no lo es.

**`SIM-` delante del `TransactionId` no es decoración.** En un log lleno de GUIDs, es lo único que distingue de un vistazo un identificador de cobro simulado de uno real. El día que haya una pasarela de verdad, el identificador lo devuelve ella y el método que lo acuña desaparece.

**Reenviar un mensaje a mano por la API de management: dos trampas ya conocidas y una nueva.** El JSON va **sin BOM** (`Out-File -Encoding utf8` mete uno y el broker responde `not_json`) y el vhost va en la ruta (`/api/exchanges/%2F/…`). La nueva es que el sobre tiene que llevar `content_type: application/vnd.masstransit+json` en `properties` y un `messageType` con la URN completa (`urn:message:Shop133.Contracts.Events:StockReserved`); sin eso el broker contesta `{"routed":true}` igual —el exchange existe y la cola está ligada— y el consumer no se entera de nada. **`routed:true` dice que el mensaje llegó a una cola, no que alguien haya podido leerlo.**

**El log de arranque es lo que prueba que el consumer está enchufado.** `Configured endpoint stock-reserved, Consumer: Payments.API.Consumers.StockReservedConsumer`, **antes** de `Bus started`. Sin esa línea, `AddConsumer` no llegó a `ConfigureEndpoints` y el mensaje se pierde en silencio — el fallo que `3.1` pre-empeñó dejando `cfg.ConfigureEndpoints(context)` puesta con cero consumers.

**User Secrets solo cargan en `Development`.** Ya mordió en `3.4` y vuelve a morder aquí, porque para esquivar el bloqueo de SAC hubo que arrancar el `.exe` de `bin/Release` directamente, saltándose `launchSettings.json` y con él la variable de entorno. Hay que ponerla a mano (`ASPNETCORE_ENVIRONMENT=Development`) o la guarda de `ConnectionStrings:PaymentsDb` salta con el secreto perfectamente puesto.

---

## Verificación

Ejecutado el 2026-08-31. Salidas reales.

### 1. Build limpio

```
Shop133.Contracts -> ...\Shop133.Contracts.dll
Payments.Infrastructure -> ...\Payments.Infrastructure.dll
Payments.API -> ...\Payments.API.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### 2. La suite de arquitectura sigue en 15, con el proyecto nuevo ya en su sitio

```
=== TEST EXECUTION SUMMARY ===
   Shop133.ArchitectureTests  Total: 15, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.513s
```

Es lo que prueba la decisión 8: el consumer está en `Consumers/`, el `DbContext` en el `.Infrastructure`, EF Core no se coló en el `.API` y MassTransit sigue en 8.x — todo verificado por reglas que ya existían.

### 3. El bloqueo de Smart App Control, y cómo se rodeó

```
Microsoft.EntityFrameworkCore.Design.OperationException: Could not load assembly 'Payments.Infrastructure'.
 ---> System.IO.FileLoadException: Could not load file or assembly
      '...\Payments.API\bin\Debug\net10.0\Payments.Infrastructure.dll'.
      An Application Control policy has blocked this file. (0x800711C7)
```

Ocho reintentos (`intentos=8 ok=False`). Después, `dotnet run` tampoco arrancaba:

```
Unhandled exception. System.IO.FileLoadException: Could not load file or assembly
'...\Payments.API\bin\Debug\net10.0\Payments.API.dll'. An Application Control policy has blocked this file. (0x800711C7)
```

Con `dotnet build -c Release` primero, a la primera:

```
--- ef en Release ---
Build started...
Build succeeded.
Done. To undo this action, use 'ef migrations remove'
```

### 4. La migración, y la comprobación de `ValueGeneratedNever()`

```csharp
migrationBuilder.CreateTable(
    name: "Payments",
    columns: table => new
    {
        OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
        Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
        Status = table.Column<int>(type: "int", nullable: false),
        TransactionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
        FailureReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
        ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
    },
    constraints: table => { table.PrimaryKey("PK_Payments", x => x.OrderId); });
```

`uniqueidentifier NOT NULL` **sin `DEFAULT`**: el valor lo pone Orders. Aplicada y leída **como `payments_user`**, no como `sa` — la regla 1 comprobada de paso:

```
name           type              max_length  is_nullable
OrderId        uniqueidentifier  16          0
Amount         decimal            9          0
Status         int                4          0
TransactionId  nvarchar         200          1
FailureReason  nvarchar        1000          1
ProcessedAt    datetimeoffset    10          0
```

### 5. La cola y el binding

```
info: MassTransit[0]
      Configured endpoint stock-reserved, Consumer: Payments.API.Consumers.StockReservedConsumer
info: MassTransit[0]
      Bus started: rabbitmq://localhost/
```

```
--- COLAS ---
order-created                  messages=4
stock-reserved                 messages=0
--- BINDINGS ---
Shop133.Contracts.Events:OrderCreated         -> order-created
Shop133.Contracts.Events:StockReserved        -> stock-reserved
```

Los 4 mensajes en `order-created` eran pedidos anteriores esperando a que Inventory arrancara — y al arrancarlo dieron gratis el primer recorrido completo, con los dos caminos:

```
Stock rechazado para el pedido 8845aeb2-…: el producto 999999 no existe en el inventario.
Stock reservado para el pedido 246d6066-…: 1 línea(s) por un importe de 747.00.
Stock reservado para el pedido 876d57bd-…: 2 línea(s) por un importe de 587.50.
Stock reservado para el pedido aef40919-…: 2 línea(s) por un importe de 1334.50.
```

```
Cobro aceptado  para el pedido 246d6066-… por 747.00,  transacción SIM-B70EAF4AECA540DD8141667FEFEDD5B3.
Cobro rechazado para el pedido aef40919-… por 1334.50: el importe 1334.50 supera el límite autorizado de 1000.00.
Cobro aceptado  para el pedido 876d57bd-… por 587.50,  transacción SIM-175770D592684BCEAB5777A1B014E78B.
```

El pedido rechazado por Inventory (producto 999999) **no dejó fila en `PaymentsDb`**: publicó `StockRejected`, no `StockReserved`, así que Payments nunca se enteró. Correcto.

**Y aquí está la deuda 1 saldada:** `Amount` llega con el importe real del pedido, no `0`. `StockReserved.Amount` era el único sitio del que podía salir.

### 6. Camino feliz y camino de rechazo, de punta a punta

```
POST /orders  →  OK pedido = 2d326368-ed6f-4bae-ac57-4d00c9cbb505  total = 378
POST /orders  →  CARO pedido = b9f8bc72-9cbd-470f-9c03-07cd23126f26  total = 1197
```

```
Cobro aceptado  para el pedido 2d326368-… por 378, transacción SIM-7F61BFCB28794374939F2212697A72C8.
Cobro rechazado para el pedido b9f8bc72-… por 1197: el importe 1197.00 supera el límite autorizado de 1000.00.
```

Los cinco exchanges existen, y **nadie está escuchando los dos de pago**:

```
Shop133.Contracts.Events:OrderCreated       fanout
Shop133.Contracts.Events:PaymentCompleted   fanout
Shop133.Contracts.Events:PaymentFailed      fanout
Shop133.Contracts.Events:StockRejected      fanout
Shop133.Contracts.Events:StockReserved      fanout

--- Colas ligadas a los eventos de pago ---
NINGUNA. PaymentCompleted y PaymentFailed se publican al vacio (la saga llega en 4.2/4.3).
```

**Lo que no pasa es la mitad interesante.** El pedido de 1197 tiene el cobro rechazado y el stock **sigue reservado**:

```
Amount|Status|Txn   |FailureReason
1197.00|2    |(null)|el importe 1197.00 supera el límite autorizado de 1000.00

ProductId|QuantityOnHand|QuantityReserved
2        |65            |4
ReservaSigueViva
1
```

Nadie consume `PaymentFailed` hasta `4.3`, y nadie suelta las unidades hasta `4.4`. **Esa es la Fase 4 en una frase**, y también la razón de que la regla 7 exista escrita: hoy el stock reservado se filtra, y el proyecto lo sabe.

### 7. Idempotencia de negocio

Reenviado a mano el mismo `StockReserved` del pedido `2d326368-…` (`{"routed":true}`):

```
El pedido 2d326368-… ya se había cobrado el 08/31/2026 18:13:01 +00:00 con resultado Completed;
no se vuelve a cobrar y se reenvía el desenlace guardado.

Filas|Txn
1    |SIM-7F61BFCB28794374939F2212697A72C8
```

Una sola fila y **el mismo `TransactionId`** que la primera vez — que es lo que la decisión 3 quería garantizar. Sin cola `stock-reserved_error` a la vista:

```
order-created                  messages=0
stock-reserved                 messages=0
```

### 8. La guarda de importe no positivo

Publicado un `StockReserved` inventado con `amount: "0"`:

```
Amount|Status|FailureReason
.00   |2     |el importe 0.00 no es cobrable; un pedido tiene que valer algo
```

De paso queda demostrado algo que no se había dicho en voz alta: **Payments no comprueba que el pedido exista.** El `OrderId` de esta prueba no está en `OrdersDb` y aun así hay fila de cobro. No es un descuido — la regla 1 impide preguntarlo, y en coreografía cada servicio se cree el evento que le llega.

**No hay tests automatizados de nada de esto.** Todo lo de arriba se verificó a mano contra un broker y una base reales; automatizarlo con el harness en memoria es `3.7`.

---

## Pendiente

- **`3.6`** — idempotencia por `MessageId` del sobre. La de este punto va por `OrderId` y no la sustituye: no cubre un consumer que no escriba nada.
- **`3.7`** — `Payments.Tests` con `AddMassTransitTestHarness`, incluida la cuarta copia de `SqlServerContainerFixture` y la decisión sobre si extraerla. Falta además el `public partial class Program { }` al pie de `Payments.API/Program.cs`, que ese punto necesitará si monta `WebApplicationFactory`.
- **`4.2`/`4.3`** — la saga consume `PaymentCompleted`/`PaymentFailed`. Hoy se publican a exchanges sin colas y no falla ni avisa.
- **`4.4`** — liberar el stock que un cobro rechazado deja reservado. Medido y reproducible en la verificación 6.
- **`4.5`** — el outbox transaccional cierra el agujero de la doble escritura (`SaveChanges` hecho, `Publish` perdido), anotado en el consumer. **Y es el próximo punto de relectura del bloque `AddMassTransit`**, con la primera divergencia estructural real entre las tres copias.
- **`4.8`/`4.9`** — validar la autenticidad de la foto de precios. La guarda de `Amount <= 0` no lo hace: un producto real pedido a `0.01` sigue cobrándose un céntimo.
- **Concurrencia optimista** — sin dueño, igual que en `StockItems` desde `3.4`. Aquí el riesgo es menor (la PK impide la segunda fila), pero dos entregas simultáneas del mismo `StockReserved` pueden pasar las dos la comprobación de existencia y chocar en el `INSERT`, mandando una a `stock-reserved_error`.
- **Un cobro no se convierte nunca en nada más.** No hay `Refund`, y `PaymentCompleted` no descuenta stock físico — el hueco hermano del que `3.4` dejó anotado.
- **Contenedor para Payments** (y para Orders e Inventory). Sin fecha; hoy solo Catalog está contenerizado. Al hacerlo habrá que revisar el `UseHttpsRedirection()` sin guardar, como ya se anotó para Orders en `3.3`.
