# Fase 4.8 — Catalog.API estrena MassTransit y valida la foto de precios

**Fecha:** 2026-09-04 · **Estado:** completado · [Roadmap](../plan-desarrollo-shop133.md)

---

## Objetivo

Darle dueño al importe del pedido.

La decisión 2 de [fase_3_3.md](fase_3_3.md) dejó que el cuerpo del `POST /orders` traiga el precio, y dio por hecho que la comprobación "se mudaba a Inventory". Su propia corrección 2b admitió que solo se mudó la de **existencia**: Inventory guarda cantidades, no importes. Así que un pedido de un producto **que sí existe** a `0.01` devolvía `201`, pasaba la reserva de Inventory, pasaba el umbral de Payments (0.01 no supera 1000) y **se cobraba un céntimo**, sin que ningún punto del roadmap se enterara.

Este punto pone la validación en el único servicio que puede firmar ese dato —Catalog— y lo hace **asíncronamente**: consume `OrderCreated` y contesta `OrderPricingValidated` / `OrderPricingRejected`. Con Catalog caído el `POST` sigue devolviendo `201` y el pedido espera; lo que ganó la Fase 3 no se devuelve.

Es además el punto en el que **Catalog deja de ser el único de los cinco servicios sin mensajería**. Desde 1.6 solo se le podía hablar por HTTP, y 3.3 borró la última llamada síncrona que alguien le hacía.

**Fuera de alcance, deliberadamente: 4.9.** La saga no gana su estado `PricingPending` aquí, así que los dos eventos nuevos se publican **al vacío** —exchange con cero colas ligadas—, exactamente como les pasó a `StockRejected` y `PaymentFailed` entre 3.4 y 4.3.

---

## Decisiones

### 1. La autenticidad se valida contra el precio anterior más una ventana, no contra el precio de hoy

Es la decisión que define el punto, y el roadmap ya avisaba de la trampa: **comparar la foto contra el precio actual está mal**, porque rechazaría un pedido legítimo cuyo precio cambió a mitad del checkout. Congelar el precio que el cliente vio es el comportamiento correcto — todo el `///` de [`OrderLine`](../src/Shared/Shop133.Contracts/OrderLine.cs) existe para afirmarlo.

Pero sostener esa frase cuesta esquema: hasta hoy `Product` guardaba **un** precio y ningún historial, así que no había forma de distinguir "un precio que este catálogo llegó a ofrecer" de "un precio inventado". `Product` gana por eso dos columnas nullable, `PreviousPrice` y `PriceChangedAt`, y un predicado: la foto es auténtica si coincide con `Price`, **o** si coincide con `PreviousPrice` y el cambio ocurrió dentro de la ventana de checkout.

*Descartada* la igualdad con el precio actual a secas: sin esquema nuevo y cerrando el agujero por completo, pero es justo lo que el roadmap llama incorrecto, y habría que haberlo escrito como una reversión razonada de esa frase.

*Descartada* una tabla `ProductPriceHistory` con vigencias, que es la respuesta **completa** a "autenticidad": entidad, configuración, migración, 50 filas de seed, escritura desde `Update` y una política de purga, para ensanchar una ventana de la que el proyecto no tiene ninguna evidencia de que sea estrecha.

**La limitación que eso deja, dicha en voz alta: hay exactamente UN paso de historia.** Dos cambios seguidos (249 → 199 → 179) invalidan una foto legítima de 249 tomada hace dos minutos, y el cliente recibe una cancelación por un pedido correcto. El día que eso duela, la tabla de historial es la forma de arreglarlo.

### 2. Se validan tres cosas, y las dos que NO se validan importan igual

Se comprueba que **(a)** el producto existe en `CatalogDb`, **(b)** cada `UnitPrice` es auténtico según la decisión 1, y **(c)** que `OrderCreated.Total` cuadra con la suma de `Quantity * UnitPrice`.

La **(c)** no estaba en el título del punto y es un agujero mayor que el del precio unitario: `OrderCreated.Total` es lo que Payments cobra —Inventory lo reenvía tal cual en `StockReserved.Amount` desde 3.4, y 3.5 lo compara con su umbral y lo persiste—, y hasta hoy **nadie comprobaba que cuadrara con las líneas**. Un cuerpo con líneas auténticas por 1000.00 y un `Total` de 0.01 pasaba entero, sin mentir sobre ningún precio. Es aritmética pura y Catalog es quien tiene los precios delante.

Un detalle de esa comprobación que parece un descuido y no lo es: la suma se calcula **sobre lo que trae el mensaje**, no sobre los precios del catálogo. Recalcular con el precio de hoy convertiría este check en la comparación por igualdad que la decisión 1 descartó, por la puerta de atrás — un pedido con la foto legítima del precio anterior fallaría el total justo después de haber pasado la validación de precios.

**No se validan `ProductSku` ni `ProductName`**, los otros dos campos congelados de `OrderLine`. `Product.Update` puede cambiar el `Sku` desde 1.3 (decisión 9 de [fase_1_1.md](fase_1_1.md): el código de negocio se corrige y se renumera, la clave sustituta no) y renombrar un producto es una operación de catálogo normal, así que compararlos daría falsos rechazos **exactamente igual** que compararía el precio contra el de hoy — el modo de fallo que la ventana existe para evitar. Los tests lo dejan observable: su ayudante `Line(...)` rellena esos dos campos con `NOMATCH-xxx` y ningún test se entera.

**Tampoco se recomprueba `Quantity > 0`**: lo garantiza el constructor de `Order` en Orders.Domain, y esto es una validación de la *autenticidad* de la foto, no un segundo validador de formato.

### 3. La contabilidad del precio anterior va en `Update`, no en `Apply`

`Apply` es el método privado que comparten el constructor público y `Update`, y su contrato —validar en locales y asignar en bloque, para que un `Update` fallido no deje la entidad medio mutada en el `ChangeTracker`— es lo más fácil de romper aquí. No se toca.

La contabilidad va en `Update`, capturando el precio **antes** de llamar a `Apply` y escribiendo las dos columnas **después** de que vuelva:

```csharp
var priceBefore = Price;
Apply(sku, name, description, price, stock, categoryId, imageUrl);   // lanza si algo no valida
if (price != priceBefore) { PreviousPrice = priceBefore; PriceChangedAt = DateTimeOffset.UtcNow; }
```

Tres propiedades que compra esa forma. El constructor público no cambia, así que **un producto nuevo nace con las dos columnas a `null` gratis**, que es exactamente lo que quiere decir "este producto nunca ha cambiado de precio" — sin flag ni segunda vía. La escritura queda estrictamente **después del último punto de throw**, así que hereda la garantía de todo-o-nada de `Apply` en vez de debilitarla: un `Update` que lance no puede dejar `PreviousPrice` movido con el `Price` viejo, que sería la mitad de un cambio y volvería auténtico un precio que nunca existió. Y `decimal !=` compara valor y no escala, así que un `PUT` que reenvía `249.0` sobre un `249.00` almacenado **no** es un cambio de precio y no quema la ventana — relevante porque 3.3 midió que los ceros finales se pierden en tránsito.

*Descartado* pasarle un flag a `Apply` para distinguir alta de modificación: le daría un parámetro cuyo significado es "quién me llamó".

### 4. El predicado vive en la entidad

`Product.IsAuthenticPrice(decimal unitPrice, TimeSpan window)`, con el precedente de `StockItem.CanReserve`: un predicado puro que nunca lanza y cuyo llamante decide qué publicar. Aquí compra algo que aquel caso no tenía — el código que **escribe** las dos columnas y el que las **lee** acaban en el mismo archivo, así que las dos mitades de la ventana no pueden divergir. Escrito como cuatro cláusulas en el consumer, sí podrían.

No tiene gemelo que lance al estilo de `StockItem.Reserve`: allí el par existía porque después de comprobar había que mutar; aquí validar un precio es una lectura.

### 5. La ventana es configuración sin guarda, y va en minutos

`PricingValidationOptions` en la raíz de `Catalog.API` (donde vive `PaymentSimulationOptions` en Payments), sección `Catalog` de `appsettings.json`, valor por defecto **30 minutos**, y **sin guarda que reviente al arrancar** — el criterio literal de `Payments:DeclineAmountAbove`: no es un secreto, y a diferencia de cualquier clave de `ConnectionStrings` su ausencia no deja el servicio a medias.

*Descartada* una constante en el consumer o en `Product`: la ventana no es una invariante de un producto —un producto no sabe cuánto dura un checkout— ni un detalle de un consumer. Es una **política**, de la misma especie que "rechaza por encima de 1000", y la pregunta que llegará algún día ("el checkout se nos ha vuelto más lento, ensánchala") no debería ser una recompilación.

**Minutos y no un `TimeSpan`**, y el motivo se enlaza con la falta de guarda: `IConfiguration` sabe enlazar `"00:30:00"`, pero un formato mal escrito no falla al arrancar — falla al leer `IOptions.Value` por primera vez, o sea **dentro del consumer**, con lo que el mensaje acabaría en `order-created-pricing_error` a varios saltos de la causa. Eso anularía en silencio el argumento de "sin guarda no pasa nada". Un `int` no se puede teclear en esa trampa. El `TimeSpan` se expone como propiedad calculada, así que la configuración se declara en la unidad segura de escribir y el código trabaja con la que expresa la intención.

### 6. Los dos contratos nuevos: 10 → 12 mensajes

`OrderPricingValidated` lleva **solo el `OrderId`**, con el precedente de `StockReleased` (cuyo `///` tiene una sección entera titulada "Por qué solo lleva el OrderId"). La excepción del proyecto —`StockReserved.Amount`— existe porque Payments no tiene a quién preguntar; aquí el único consumidor es la saga, que ya tiene el pedido en `OrderState`. Un importe sería una segunda fuente para un hecho que `OrderCreated.Total` ya lleva: el argumento exacto por el que 4.4 le quitó las `Lines` a `ReleaseStock`.

`OrderPricingRejected` lleva `Reason`, con la plantilla de `StockRejected`, y es obligatorio porque tiene destinatario: 4.9 lo arrastrará a `OrderCancelled.Reason`, que Notifications pone en el cuerpo del correo desde 4.6. Sin él el cliente recibiría un aviso de cancelación que no dice nada — el mismo modo de fallo que 4.4 arregló añadiendo `StockReleased`.

**Y una cosa que el `///` de `OrderPricingRejected` deliberadamente NO afirma.** El de `StockRejected` dice "no hay nada que compensar", y puede decirlo porque la reserva de Inventory es atómica. Éste no puede prometer lo mismo: **Inventory sigue consumiendo `OrderCreated` del mismo exchange fanout** (decisión 2 de [fase_4_1.md](fase_4_1.md), sin cambios), así que esta validación y la reserva de stock corren **en paralelo** y no en secuencia, y un rechazo de precio puede llegar a la saga con el stock ya reservado. El título de 4.9 en el roadmap dice "sin nada que compensar"; **puede ser falso**, y es 4.9 quien tiene que releerlo con la máquina de estados delante — el mismo movimiento con el que 4.4 corrigió la nota de 4.3 sobre `CompensatingStock`. Lo que este punto no hace es escribir en un contrato una promesa que el diseño no sostiene.

### 7. El nombre del consumer rompe la convención a propósito, y es el riesgo entero del punto

La clase se llama **`OrderCreatedPricingConsumer`**, no `OrderCreatedConsumer`. El `SetKebabCaseEndpointNameFormatter()` deriva la cola del nombre del tipo menos el sufijo `Consumer`, e **`Inventory.API` es dueño de `OrderCreatedConsumer` —y por tanto de la cola `order-created`— desde 3.4**.

Con clases homónimas, los dos servicios no serían dos suscriptores del fanout: serían **consumidores COMPETIDORES de una sola cola**, y cada `OrderCreated` llegaría a uno de los dos al azar. La mitad de los pedidos se quedaría sin validar el precio y la otra mitad sin reservar stock, **sin un solo error en ningún log**. Es exactamente la trampa que 4.6 documentó para Notifications, un servicio después — y allí el peligro era *renombrar* un consumer, aquí es *no* renombrarlo.

*Descartado* un `.Endpoint(e => e.Name = ...)` explícito: dejaría dos clases homónimas en el repositorio y un log que solo imprime el nombre corto. *Descartado* un formatter con prefijo de servicio: dejaría a Catalog con una convención de nombres distinta a la de los otros cuatro, y el problema real —que dos clases homónimas colisionan— seguiría ahí para el siguiente.

**Ningún test puede ver esto**: los de arquitectura leen `.csproj` y rutas de archivo, nunca la topología de un broker. Se verifica contra RabbitMQ mirando que `order-created` siga teniendo **un solo** consumidor (verificación 3). Ese hueco es de 8.2 por escrito desde 4.6.

### 8. Solo hay guarda de idempotencia de transporte, y no hay de negocio

`Catalog.Infrastructure` gana `ProcessedMessage` y su configuración — la **quinta copia literal**, idéntica a las de Inventory, Payments, Orders y Notifications. Sigue sin extraerse por lo mismo de siempre: los cinco `.Infrastructure` tienen cero `ProjectReference` a propósito y no hay un proyecto de infraestructura común.

Lo nuevo es lo que **falta**. En los otros cuatro servicios esta guarda convive con una de **negocio** que reconoce el mismo *pedido* en vez de la misma *entrega*, y todas salieron de una fila que el consumer tenía que escribir de todas formas: la PK de `StockReservations` (3.4, gratis), la fila de `Payments` (3.5, escrita a mano), la clave `(OrderId, Kind)` de `Notifications` (4.6, gratis). **Este consumer no escribe nada de negocio** —validar precios es una lectura pura—, así que no hay artefacto del que sacar la otra mitad.

El razonamiento que lo resuelve es el de 3.6 aplicado a un consumer entero en vez de a una rama: la rama de rechazo de `OrderCreatedConsumer` "no escribía nada… se salía sin tocar el `ChangeTracker`", y por eso una reentrega publicaba un **segundo** `StockRejected`. Aquí eso pasa en *todos* los caminos, así que **la marca es la única escritura** y lleva su propio `SaveChangesAsync`. La ventaja secundaria es que el consumer tiene un solo punto de marcado en vez de los tres de Inventory, que es donde 3.4 se olvidó de uno.

*Descartado* argumentar que la guarda sobra porque la operación es una lectura: sería saltarse la regla 6, y 4.6 ya rechazó esa forma dos veces (una guarda en memoria se pierde al reiniciar, que es cuando una reentrega es más probable; y documentar el agujero vale para un hueco sin dueño, no para saltarse una regla en el servicio más barato de arreglar).

*Descartada* una tabla `PricingValidations` que diera la guarda de negocio: sería `CatalogDb` guardando datos de pedidos que no le pertenecen —el espíritu de la regla 1— en una tabla que nadie lee ni purga.

**La consecuencia, aceptada y con test propio:** el mismo pedido reacuñado con un `MessageId` nuevo vuelve a leer y vuelve a contestar. Se acepta porque la respuesta es función pura del mensaje y de `CatalogDb`, no un segundo efecto como sería un segundo cobro o una segunda reserva.

**Y una sutileza que parece un argumento en contra.** La guarda de negocio de Inventory republica el desenlace **guardado**, así que su segunda respuesta es idéntica por construcción. Aquí se **recalcula**, así que una reentrega tardía —con la ventana ya vencida— puede contestar `OrderPricingRejected` donde la primera contestó `OrderPricingValidated`. No es un fallo de la guarda: es lo que significa que la autenticidad tenga fecha de caducidad. Es también una razón real por la que alguien podría querer la tabla algún día, y queda dicho que hoy no se paga.

### 9. Dos migraciones, y una editada a mano

Se parten como 4.5 partió `AddOrderStateSaga` de `AddTransactionalOutbox`: dos migraciones de esquema en una subfase, porque son dos decisiones independientes y una es fontanería. `AddProductPriceHistory` es negocio; `AddProcessedMessages` es entrega, y queda como el **quinto archivo con ese nombre**, reconocible al lado de los de Orders, Inventory y Payments.

**La primera se editó a mano, y eso hay que justificarlo.** Tal y como la generó `dotnet ef`, su `Up()` traía además **50 `UpdateData`** —uno por fila del seed de 1.4— poniendo las dos columnas nuevas a `NULL`. Son no-ops: la columna acaba de crearse con `AddColumn` y ya vale `NULL` en todas las filas. Se borraron; el detalle de por qué EF los emite está abajo.

Lo que **no** se toca nunca a mano es `CatalogDbContextModelSnapshot.cs`. Editar el cuerpo de un `Up()`/`Down()` generado es legítimo; reescribir el snapshot es perder la referencia contra la que se compara el modelo.

### 10. Sin regla de arquitectura nueva, y se dice por escrito

Precedente de 3.3, 3.5, 4.5, 4.6 y 4.7: *nunca inventar un filtro que no casa nunca*. Todo lo que introduce el punto ya está vigilado — `MassTransitPackages_StayOnMajorVersion8` cubre el pin `8.5.10` de `Catalog.API` (y cuenta un `Version` ausente o vacío como violación, así que la versión tiene que estar escrita), `ConsumerFiles_LiveOnlyIn_ServiceApiConsumersFolder` cubre la ruta del consumer, `Contracts_PublicTypes_LiveInEventsOrCommandsNamespace` cubre los dos records, y `EfCorePackages_LiveOnlyIn_InfrastructureProjects` sigue igual. **La suite se queda en 16.**

Y la regla que sí valdría —"dos servicios no comparten nombre de cola"— **no se puede escribir**: esos tests leen `.csproj` y rutas de archivo, nunca la topología de un broker. Ese hueco ya es de 8.2 desde 4.6.

### 11. `CatalogApiFactory` gana ~25 líneas que ningún test usa

La guarda nueva pone los **19** tests de 1.7 en rojo en el constructor: `WebApplicationFactory<Program>` ejecuta el `Program.cs` real y la guarda lanza *antes* de `app.Build()`, así que `ConfigureTestServices` no llega a tener turno. Es la regla que escribió 3.1 y que ya cobró una vez en Orders.

Se le añade el `UseSetting` con un host **inventado a propósito** (`amqp://el-harness-sustituye-esto:5672`) y el desmontaje de MassTransit con el harness en su lugar, copiado de `OrdersApiFactory`.

**Ser preciso sobre por qué hace falta el desmontaje, porque "si no, los tests fallan" es FALSO.** 3.1 midió que un bus apuntando a un host inexistente no revienta: loguea `warn: Connection Failed` y reintenta con backoff. Con solo el `UseSetting`, los 19 tests probablemente pasarían — cada uno con un bucle de reconexión de fondo. Las razones honestas son otras: elimina la *posibilidad* de tocar un broker real, evita que el consumer quede registrado-pero-inerte dentro del host de test, y mantiene lo que `CLAUDE.md` afirma desde 3.7 (verificación 9).

**Y la verdad incómoda:** en Orders el desmontaje se pagó solo, porque permitió por fin afirmar que `OrderCreated` se publicaba. Aquí **no habilita ni una aserción nueva** — Catalog.API no publica nada por HTTP, ningún controller toca `IPublishEndpoint`, y 4.8 no lo cambia. Es puramente un satisfactor de guardas.

`IsMassTransit`/`BelongsToMassTransit` se **copian, no se extraen**: es la segunda copia (la otra está en `OrdersApiFactory` desde 3.7) y la regla de 2.4 es que dos ocurrencias no son un patrón. Hay además un obstáculo mecánico: `Shop133.TestUtilities` declara por escrito que solo entra "lo que las cuatro suites usan igual" y tiene cero `ProjectReference`, mientras que estos helpers necesitan `ServiceDescriptor` —que no viene en el reference pack de `Microsoft.NETCore.App`— y solo los usan dos de las cuatro suites. Una tercera copia obliga a releerlo.

### 12. La suite del consumer no usa `WebApplicationFactory`, aunque podría

`CatalogConsumerHost` es un `ServiceCollection` pelado con el consumer, el `DbContext` real y el harness — el patrón de `Inventory.Tests`/`Payments.Tests`, no el de `CatalogApiFactory`. Y eso aunque Catalog.API **sí** tenga API y **sí** tenga el `public partial class Program { }` desde 1.7, que es lo que faltaba en Inventory y Payments.

*Descartado* reutilizar `CatalogApiFactory`, que desde este punto ya trae un harness dentro y por tanto **podría** servir. Se descarta por lo que arrastra: esa fábrica existe para probar endpoints y sus 19 tests dependen de que nadie toque una fila del seed. Los tests del consumer necesitan lo contrario —cambiar precios y retrasar fechas—, así que compartirla sería mezclar dos disciplinas de datos opuestas en el mismo host.

**El precio, el mismo que aceptó 3.7:** nada comprueba que `Program.cs` registre de verdad el consumer, ni —lo que aquí es peor— que el nombre de la cola sea el que se cree. Si alguien renombrara la clase a `OrderCreatedConsumer`, los 10 tests seguirían verdes y en producción Catalog e Inventory se convertirían en consumidores competidores. Es de 8.2.

Se **reutiliza la colección `CatalogApiCollection`**: su fixture es el *contenedor*, no la API, y mantener todas las clases en una sola colección es lo que las serializa — xUnit paraleliza entre colecciones, nunca dentro de una, y esa serialización es deliberada desde 1.7 porque SQL Server empieza a dar timeouts con varios `CREATE DATABASE` + migración a la vez. El prefijo de base sí es distinto (`CatalogConsumerTests_NNN`), porque el contador es un `static` por clase y con el mismo prefijo las dos podrían acuñar el mismo nombre.

---

## Cambios

### Nuevos

| Archivo | Rol |
|---|---|
| [`src/Shared/Shop133.Contracts/Events/OrderPricingValidated.cs`](../src/Shared/Shop133.Contracts/Events/OrderPricingValidated.cs) | El undécimo mensaje. Solo `OrderId`. |
| [`src/Shared/Shop133.Contracts/Events/OrderPricingRejected.cs`](../src/Shared/Shop133.Contracts/Events/OrderPricingRejected.cs) | El duodécimo. `OrderId` + `Reason`. |
| [`src/Services/Catalog/Catalog.Infrastructure/Entities/ProcessedMessage.cs`](../src/Services/Catalog/Catalog.Infrastructure/Entities/ProcessedMessage.cs) | La bitácora de idempotencia de 3.6, quinta copia. |
| [`src/Services/Catalog/Catalog.Infrastructure/Persistence/Configurations/ProcessedMessageConfiguration.cs`](../src/Services/Catalog/Catalog.Infrastructure/Persistence/Configurations/ProcessedMessageConfiguration.cs) | Su mapeo, con la PK compuesta. |
| `Migrations/20260904185251_AddProductPriceHistory.cs` | Las dos columnas de `Product`. **Editada a mano.** |
| `Migrations/20260904190208_AddProcessedMessages.cs` | La tabla del inbox. |
| [`src/Services/Catalog/Catalog.API/PricingValidationOptions.cs`](../src/Services/Catalog/Catalog.API/PricingValidationOptions.cs) | La ventana de checkout, sección `Catalog`. |
| [`src/Services/Catalog/Catalog.API/Consumers/OrderCreatedPricingConsumer.cs`](../src/Services/Catalog/Catalog.API/Consumers/OrderCreatedPricingConsumer.cs) | El consumer. Cola `order-created-pricing`. |
| [`tests/Services/Catalog/Catalog.Tests/Infrastructure/CatalogConsumerHost.cs`](../tests/Services/Catalog/Catalog.Tests/Infrastructure/CatalogConsumerHost.cs) | El host del consumer, con los ayudantes de siembra. |
| [`tests/Services/Catalog/Catalog.Tests/OrderCreatedPricingConsumerTests.cs`](../tests/Services/Catalog/Catalog.Tests/OrderCreatedPricingConsumerTests.cs) | Los 10 tests nuevos. |
| `docs/fase_4_8.md` | Este documento. |

### Modificados

| Archivo | Cambio |
|---|---|
| `Catalog.Infrastructure/Entities/Product.cs` | `PreviousPrice`, `PriceChangedAt`, la contabilidad en `Update` y `IsAuthenticPrice`. |
| `Catalog.Infrastructure/Persistence/Configurations/ProductConfiguration.cs` | Las dos columnas, con la misma precisión que `Price`. |
| `Catalog.Infrastructure/Persistence/CatalogDbContext.cs` | `DbSet<ProcessedMessage>` y el tercer `ApplyConfiguration`. |
| `Catalog.Infrastructure/Migrations/CatalogDbContextModelSnapshot.cs` | Regenerado por `migrations add`. |
| `Catalog.API/Program.cs` | El `Configure<PricingValidationOptions>`, la guarda de `ConnectionStrings:RabbitMq` y el sexto bloque `AddMassTransit`. |
| `Catalog.API/Catalog.API.csproj` | `MassTransit.RabbitMQ` **8.5.10**. |
| `Catalog.API/appsettings.json` | La sección `Catalog`. |
| `tests/.../Catalog.Tests/Infrastructure/CatalogApiFactory.cs` | El `UseSetting` falso y el desmontaje de MassTransit. |
| `docker-compose.yml` | `ConnectionStrings__RabbitMq` y `depends_on: rabbitmq`. |

**Explícitamente NO tocados:** el `Dockerfile` (copia `src/Services/Catalog/` entero tras la capa de restore, así que la carpeta `Consumers/` entra gratis) · `.env` y `.env.example` · `db/init/01-create-databases.sql` · `docker-compose.override.yml` · `Catalog.Infrastructure.csproj` (MassTransit **no** va ahí) · `Catalog.Tests.csproj` (el harness llega transitivamente) · `CatalogSeedData.cs` · cualquier migración existente o `.Designer.cs` · `ProductsController` y sus DTOs · `tests/Shop133.ArchitectureTests/*` · los otros cuatro servicios.

**`ProductResponse` deliberadamente no expone `PreviousPrice` ni `PriceChangedAt`:** publicarlas le daría a un cliente la cifra exacta que necesita para forjar una foto que pase por auténtica, que es lo contrario del propósito del punto.

---

## Detalles que cuestan tiempo

**`HasData` emite 50 `UpdateData` no-ops al añadir una columna nullable, y el plan de este punto predijo lo contrario.** El razonamiento era que si las columnas son nullable, omitirlas en el seed es legal y no se genera nada. La primera mitad es cierta —el seed son objetos anónimos y no hubo que tocarlos— pero la segunda es falsa: EF compara la forma **completa** de cada fila sembrada contra el snapshot anterior, así que al ganar la entidad dos propiedades las 50 filas cuentan como "cambiadas" y salen 50 `UPDATE … SET PreviousPrice = NULL, PriceChangedAt = NULL`. No hay flag para evitarlo. Y es lo que hace que el comando avise **"An operation was scaffolded that may result in the loss of data"**, un aviso que aquí no significa nada y que asusta al leerlo. Se borraron a mano; borrarlos es seguro e idempotente, porque el snapshot ya registra las columnas a `null` en las filas sembradas y el siguiente `migrations add` no las regenera.

**Para partir una migración en dos hay que ocultarle la mitad del modelo a EF, y renombrar el `DbSet` no basta.** El primer intento fue renombrar `ProcessedMessages` a `ProcessedMessages_TEMP_DISABLED`; no sirve de nada, porque la propiedad sigue devolviendo `Set<ProcessedMessage>()` y EF descubre la entidad igual. Hay que comentar **las dos** líneas: el `DbSet` y el `ApplyConfiguration`. Es la misma técnica que 1.4 y 3.4 usaron para dejar un `HasData` fuera de `InitialCreate`, y tiene el mismo remedio manual porque tampoco hay flag.

**Añadir un `PackageReference` a un servicio contenedorizado puede tirar el contenedor, y `catalog-api` es el único caso del repo.** La guarda de `ConnectionStrings:RabbitMq` lanza antes de `app.Build()` y en `Production` los User Secrets **no** se cargan, así que sin la variable en `docker-compose.yml` el contenedor muere al arrancar en el próximo `up -d --build`. Es la segunda consecuencia de la regla de 3.1 —la primera es la fábrica de tests— y hay que recordar las dos juntas, porque la de los tests salta al instante y la del contenedor solo cuando alguien reconstruye la imagen.

**El aviso de `guest` que `CLAUDE.md` arrastra desde 3.2 no aplica a esta imagen, y se comprobó leyendo el archivo.** Aquella nota decía que "`guest` solo autentica desde localhost — un problema el día que los servicios tengan contenedores", y hoy era ese día. La imagen oficial de RabbitMQ trae `loopback_users.guest = false` en `/etc/rabbitmq/conf.d/10-defaults.conf` (con el comentario *"allow access to the guest user from anywhere on the network"*), así que `guest` autentica sobre `shop133-net`. El aviso describe una instalación de RabbitMQ de serie, no esta imagen. Verificado además por observación: el contenedor arranca con `Bus started: rabbitmq://rabbitmq/` y ni un `ACCESS_REFUSED`.

**Un test puede fallar porque su `bin/` tiene una copia vieja del servicio, y el síntoma parece un fallo real.** Después de restaurar las cuatro roturas deliberadas se reconstruyó `Catalog.API` (al ejecutarlo) pero **no** `Catalog.Tests`, cuyo `bin/` guarda su propia copia de `Catalog.API.dll`. Resultado: la suite dio 28/29 con el test del `Total` en rojo, exactamente como si la restauración no se hubiera hecho — y la restauración estaba perfectamente hecha en el código fuente. Es el mismo modo de fallo que 1.2 y 4.3 anotaron para `dotnet ef migrations add`: **nada que cargue un ensamblado es de fiar hasta reconstruir el proyecto que lo consume**, y aquí el proyecto que hay que reconstruir no es el que se editó.

**`curl.exe` con JSON en línea desde PowerShell devuelve `not_json`.** PowerShell se come las comillas de `-d '{"type":"fanout"}'` antes de que curl las vea, así que la API de gestión de RabbitMQ contesta `{"error":"bad_request","reason":"not_json"}` — el mismo error que `CLAUDE.md` atribuye al BOM, con otra causa. La solución que funciona es escribir el JSON a un archivo con `[System.IO.File]::WriteAllText` (que no pone BOM) y pasarlo con `--data-binary "@archivo"`.

**Una cola espía necesita que su exchange exista antes, y aquí eso importaba más que de costumbre.** Los dos eventos nuevos no tienen consumidor hasta 4.9, así que MassTransit no declara sus exchanges hasta el primer publish — y montar la espía después habría sido tarde. Hay que declararlos a mano como `fanout`/`durable` **antes** de que el servicio publique, o el publish real falla con `PRECONDITION_FAILED`. Las colas, `durable: true` (RabbitMQ 4.x rechaza las transitorias no exclusivas).

**El texto acentuado de un log redirigido se lee mal y el dato está bien.** `Foto de precios rechazada … se pidiÃ³ a 0.01` en el archivo, mientras el payload real en la cola espía dice `se pidió a 0.01` correctamente. Es el artefacto de codepage de consola que 4.6 ya anotó; no hay nada que arreglar, pero conviene no perseguirlo.

**Smart App Control no saltó ni una vez**, pese a que hubo un paquete nuevo descargado (`MassTransit.RabbitMQ` en Catalog.API) más ensamblados recién compilados — la combinación que lo disparó en 1.7, 3.5, 3.7 y 4.4. La escalada documentada sigue en pie; simplemente no hizo falta.

---

## Verificación

### 1. Las dos migraciones, y que el seed no se reescribió

```
Applying migration '20260904185251_AddProductPriceHistory'.
Applying migration '20260904190208_AddProcessedMessages'.
Done.
```

Recuento de operaciones de la primera, después de la edición a mano:

```
UpdateData remaining: 0
AddColumn remaining: 2
```

Y el esquema con las filas del seed intactas:

```
Id          Sku        Price     PreviousPrice   PriceChangedAt
----------- ---------- --------- --------------- -----------------------------------
       2006 TEST-901       80.00         100.00  2026-09-04 22:21:01.0915546 +00:00

SeedConPreviousPrice
--------------------
                   0
```

Las 50 filas de 1.4 siguen con `PreviousPrice` a `NULL`, que es lo que los `UpdateData` borrados debían preservar.

### 2. El consumer está enchufado, y el nombre de la cola sale del log

La línea que lo prueba, **antes** de `Bus started`:

```
Configured endpoint order-created-pricing, Consumer: Catalog.API.Consumers.OrderCreatedPricingConsumer
Now listening on: http://localhost:5124
Bus started: rabbitmq://localhost/
```

El nombre se leyó de aquí y no se dedujo: es la fuente de verdad de la que depende toda la propiedad de seguridad de la decisión 7.

### 3. No hay colisión de colas — la verificación que de verdad importa

`OrderCreated` tiene ahora **tres** bindings, a tres colas distintas:

```
Shop133.Contracts.Events:OrderCreated  -> order-created            (Inventory, 3.4)
Shop133.Contracts.Events:OrderCreated  -> order-created-pricing    (Catalog, 4.8)
Shop133.Contracts.Events:OrderCreated  -> order-state              (la saga, 4.1)
```

Nueve colas de negocio en total (más dos `_error` creadas en su día). Y con Catalog.API arriba e Inventory parado, los consumidores caen donde deben:

```
order-created              consumers=0
order-created-pricing      consumers=1
order-created_error        consumers=0
```

Catalog se ligó a **su** cola y dejó `order-created` intacta. Si algún día `order-created` muestra `consumers = 2`, la colisión volvió.

### 4. De punta a punta: el pedido de un céntimo por fin se rechaza

Con Catalog.API y Orders.API arriba, producto 1 = `TAZA-001` a `249.00`:

```
PEDIDO A (0.01):      id=198e8a97-fb47-40ed-a378-01265586f729 status=Pending total=0.01
PEDIDO B (249.00 x2): id=59dd365a-db26-48e0-96f2-87a4a9fb8624 status=Pending total=498
```

Los dos devuelven `201` —Catalog no está en el camino síncrono, que es el punto— y el log de Catalog contesta:

```
Foto de precios rechazada para el pedido 198e8a97-…: el producto 1 (TAZA-001) se pidió a 0.01 y su precio es 249.00.
Foto de precios válida     para el pedido 59dd365a-…: 1 línea(s) por un total de 498.
```

**El pedido A es exactamente el escenario que la corrección 2b de 3.3 dejó medido y sin dueño**: hasta hoy llegaba a Payments y se cobraba un céntimo.

Los eventos salieron de verdad, leídos de las colas espía (una publicación cada una):

```
=== spy-OrderPricingValidated : 1 mensaje(s) ===
  "destinationAddress": "rabbitmq://localhost/Shop133.Contracts.Events:OrderPricingValidated",
  "message": { "orderId": "59dd365a-db26-48e0-96f2-87a4a9fb8624" }

=== spy-OrderPricingRejected : 1 mensaje(s) ===
  "message": {
    "orderId": "198e8a97-fb47-40ed-a378-01265586f729",
    "reason": "el producto 1 (TAZA-001) se pidió a 0.01 y su precio es 249.00"
  }
```

Nótese `sourceAddress: rabbitmq://localhost/order-created-pricing` en los dos: los publica el consumer nuevo.

### 5. La ventana, a mano y en las dos direcciones

Alta de `TEST-901` a `100.00` y un `PUT` que lo baja a `80.00`. La contabilidad la escribió `Product.Update`:

```
Id     Sku       Price   PreviousPrice   PriceChangedAt
------ --------- ------- --------------- -----------------------------------
  2006 TEST-901    80.00        100.00   2026-09-04 22:21:01.0915546 +00:00
```

Pedido al precio **anterior**, dentro de la ventana → aceptado, que es el caso legítimo del checkout a medias:

```
PEDIDO C: id=41fc58dc-…
Foto de precios válida para el pedido 41fc58dc-…: 1 línea(s) por un total de 100.
```

Luego `UPDATE Products SET PriceChangedAt = DATEADD(minute, -31, SYSDATETIMEOFFSET())` y **el mismo cuerpo otra vez** → rechazado:

```
PEDIDO D: id=955b9dc8-…
Foto de precios rechazada para el pedido 955b9dc8-…: el producto 2006 (TEST-901) se pidió a 100.00 y su precio es 80.00.
```

Un cuerpo idéntico, aceptado 30 segundos antes y rechazado ahora **solo porque la ventana venció**. Es lo que separa esta validación de "cualquier precio viejo vale para siempre".

### 6. Idempotencia a mano: los dos contadores divergen a propósito

Reposteo del sobre completo (`content_type: application/vnd.masstransit+json`, `messageType` con la URN, `message_id` en `properties`, JSON sin BOM). Partiendo de 4 filas en `ProcessedMessages`:

| Envío | Respuesta del broker | Log | `ProcessedMessages` |
|---|---|---|---|
| 1.º, `message_id` nuevo | `{"routed":true}` | `Foto de precios válida para el pedido 87c641bc-…` | 4 → **5** |
| 2.º, **mismo** `message_id` | `{"routed":true}` | `El mensaje 59c8ad16-… ya lo procesó OrderCreatedPricingConsumer …; se descarta.` | **5** (sin cambio) |
| 3.º, `message_id` **nuevo**, mismo pedido | `{"routed":true}` | `Foto de precios válida para el pedido 87c641bc-…` | 5 → **6** |

Y en la cola espía, de las **tres** publicaciones salieron **dos** eventos para ese pedido:

```
spy-OrderPricingValidated: 3 mensaje(s) desde el ultimo drenaje
   de ellos, del pedido reposteado (87c641bc-…): 2
```

La fila 2 es la guarda de transporte funcionando. La fila 3 es **la decisión 8 hecha observable**: sin guarda de negocio, un `MessageId` nuevo vuelve a contestar.

### 7. Los 10 tests nuevos, y las cuatro roturas deliberadas

```
Catalog.Tests  Total: 10, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 71.220s
```

Ninguno se dio por bueno sin verlo en rojo. Lo que enseña es **qué aserción** caza cada rotura:

| Rotura | Tests que caen | Aserción que falla |
|---|---|---|
| Guarda de transporte anulada | los **2** de idempotencia (líneas 235 y 261) | `Assert.Empty() Failure: Collection was not empty` — el `Fault<OrderCreated>` |
| Comprobación de ventana quitada de `IsAuthenticPrice` | `…PreviousPriceOutsideTheWindow…` | `Assert.Single() Failure: The collection was empty` |
| Rama del precio anterior muerta (`return false`) | `…PreviousPriceInsideTheWindow…` | `Assert.Single() Failure: The collection was empty` |
| Comprobación del `Total` anulada | `…TotalThatDoesNotMatchTheLines…` | `Assert.Single() Failure: The collection was empty` |

**La primera fila es la trampa 3 de [fase_3_7.md](fase_3_7.md) confirmada por cuarta vez** (3.7, 4.4, 4.7 y aquí): romper la idempotencia **no** cambia el recuento de eventos de negocio —sigue saliendo 1— porque el duplicado no se reprocesa, muere en el `INSERT` por clave duplicada de `ProcessedMessages` y por tanto tampoco publica. Sin el `Assert.Empty(Published<Fault<T>>())` los dos tests pasarían con la guarda borrada.

Las dos roturas de la ventana caen en tests **distintos**, lo que confirma que las dos mitades de la decisión 1 se prueban por separado.

### 8. El contenedor

```
tiempo de 'up -d --build catalog-api': 25 s

shop133-catalog-api   Up
shop133-rabbitmq      Up (healthy)

shop133-catalog-api  |  Configured endpoint order-created-pricing, Consumer: Catalog.API.Consumers.OrderCreatedPricingConsumer
shop133-catalog-api  |  Now listening on: http://[::]:8080
shop133-catalog-api  |  Bus started: rabbitmq://rabbitmq/
```

`rabbitmq://rabbitmq/` —el nombre del servicio, no `localhost`— y **sin `ACCESS_REFUSED`**, que es la pregunta del `guest` desde un contenedor contestada por observación. Sirviendo: `contenedor sirve: TAZA-001 249.00` en el puerto 5125.

El `depends_on` funcionó como se pedía: `Container shop133-rabbitmq Healthy` antes de `Container shop133-catalog-api Starting`.

### 9. Ninguna suite necesita RabbitMQ, y el repositorio no ha regresionado

Con `docker compose stop rabbitmq`:

```
Shop133.ArchitectureTests  Total: 16, Failed: 0, Time: 0.483s
Catalog.Tests              Total: 29, Failed: 0, Time: 134.579s
Orders.Tests               Total: 25, Failed: 0, Time:  67.478s
```

Y con el broker de vuelta:

```
Inventory.Tests  Total: 15, Failed: 0, Time: 100.148s
Payments.Tests   Total:  9, Failed: 0, Time:  62.206s
```

**Total del repositorio: 16 + 29 + 25 + 15 + 9 = 94** (84 antes de este punto). `Catalog.Tests` pasa de 19 a 29. La suite de arquitectura se queda en **16**, como dice la decisión 10.

### 10. Limpieza

Las dos colas espía borradas (`204` las dos) y el producto `TEST-901` borrado por el API; `SELECT COUNT(*) FROM Products` vuelve a **50**.

---

## Pendiente

- **4.9** consume estos dos eventos y le da a la saga su `PricingPending` **antes** de `StockPending`. Obliga a releer la lista de estados de 4.2, que no lo contempla. Hasta entonces los dos exchanges nuevos publican al vacío.
- **La afirmación de 4.9 "sin nada que compensar" puede ser falsa**, y es lo más importante que este punto deja anotado. Inventory sigue consumiendo `OrderCreated` del mismo fanout, así que la reserva de stock corre **en paralelo** a esta validación y un `OrderPricingRejected` puede llegar con el stock ya reservado. El `///` de ese contrato deliberadamente **no** promete lo contrario. Que 4.9 lo relea con la máquina de estados delante — es exactamente como 4.4 corrigió la nota de 4.3 sobre `CompensatingStock`.
- **Sin outbox en Catalog** (decisión 8): la marca de idempotencia se confirma *antes* del `Publish`, así que una muerte entre las dos deja el mensaje marcado y la respuesta sin enviar, y la reentrega se lo salta en silencio. En 4.9 eso significa una saga esperando en `PricingPending` **sin plazo** — el mismo hueco sin dueño que arrastra `CompensatingStock` desde 4.4. No se añade aquí porque 3.6 dejó por escrito que meter `MassTransit.EntityFrameworkCore` en servicios que 4.5 no toca gastaría la decisión antes de tiempo.
- **Un solo paso de historia de precios** (decisión 1): dos cambios seguidos invalidan una foto legítima. La tabla de vigencias es la forma de arreglarlo si algún día duele.
- **La respuesta se recalcula, no se guarda** (decisión 8): una reentrega tardía con la ventana vencida puede contestar distinto que la primera. Es correcto, y es la razón real por la que alguien podría querer una tabla de negocio aquí.
- **Nadie comprueba que `Program.cs` registre el consumer**, ni que el nombre de su cola no colisione con la de otro servicio (decisiones 7 y 12). Es de **8.2**, que prueba la topología real contra un RabbitMQ de verdad; hoy se verifica a mano con la verificación 3.
- **La ventana de 30 minutos no tiene ninguna medición detrás** — no existe todavía el checkout de la Fase 6 con el que medirla. Por eso está en configuración y no clavada en el código.
