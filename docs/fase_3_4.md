# Fase 3.4 — Inventory.API consume `OrderCreated` y reserva stock contra `InventoryDb`

**Fecha:** 2026-08-28 · **Estado:** completado · **Roadmap:** [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md)

---

## Objetivo

Poner a alguien al otro lado del mensaje. Desde `3.3`, Orders.API publica `OrderCreated` en un exchange fanout **sin colas ligadas**: el mensaje se publicaba al vacío. Este punto crea el primer `IConsumer<T>` del proyecto y, con él, la primera cola, el primer binding y la primera escritura provocada por un evento en vez de por una petición HTTP.

Además vencían aquí tres deudas concretas:

1. **La mitad de existencia** del hueco que abrió la decisión 2 de [fase_3_3.md](fase_3_3.md). Orders ya no comprueba que el producto exista; quien lo descubre es Inventory, y su respuesta no es un `404` sino un `StockRejected` que **cancela** el pedido.
2. **Reenviar `OrderCreated.Total` como `StockReserved.Amount`**. Inventory no usa ese campo; lo transporta porque Payments no puede preguntárselo a nadie. Si se olvida, **nada falla de forma visible y el pedido se cobra 0** (decisión 1 de [fase_3_2.md](fase_3_2.md)).
3. **La pregunta del `StockLine`**, aplazada tres veces (`0.3` dos veces, `3.2` una) con el compromiso explícito de decidirla "con el consumidor delante".

**Fuera de alcance deliberadamente:**

| Queda fuera | Entra en |
|---|---|
| Idempotencia por `MessageId` del sobre | `3.6` — aquí solo hay idempotencia de negocio, ver decisión 7 |
| Tests del consumer con `AddMassTransitTestHarness` | `3.7` (`Inventory.Tests`) |
| `StockItem.Release()` y el consumer de `ReleaseStock` | `4.4` |
| Que la reserva se convierta alguna vez en una baja de stock físico | Ningún punto del roadmap — ver *Pendiente* |
| Concurrencia optimista (`rowversion`) sobre `StockItems` | Sin dueño; se anota en *Pendiente* |
| Validar el **importe** de la foto de precios | `4.8` / `4.9` — Inventory guarda cantidades, no precios |
| `Dockerfile` y servicio de compose para Inventory | Sin fecha; hoy solo Catalog está contenerizado |
| Endpoints HTTP en Inventory.API | No hay ninguno, y no hace falta: este servicio solo consume mensajes |

---

## Decisiones

### 1. Proyecto nuevo `Inventory.Infrastructure`, y sin `Inventory.Domain`

El *Solution layout* de [CLAUDE.md](../CLAUDE.md) solo listaba `Inventory.API`, así que crear un proyecto exigía preguntar. Se preguntó y se creó.

No era opcional: dos tests de arquitectura lo imponen. `EfCorePackages_LiveOnlyIn_InfrastructureProjects` prohíbe `Microsoft.EntityFrameworkCore.SqlServer` en un `.API`, y `DbContextFiles_LiveOnlyIn_OwningServiceInfrastructure` exige que un `*DbContext.cs` viva en `src/Services/<S>/<S>.Infrastructure/`. Meter la persistencia en `Inventory.API` habría puesto la suite en rojo el mismo día.

**Descartado — tres proyectos como Orders**, con un `Inventory.Domain` para las entidades. Orders tiene capa de dominio porque allí vive la `OrderStateMachine`; Inventory suma y resta cantidades. El precedente que aplica es el de Catalog, que desde la decisión 1 de [fase_1_1.md](fase_1_1.md) no tiene dominio por ser un CRUD — tres capas para mover un `int` de la base al mensaje.

`Inventory.Infrastructure` no tiene **ningún** `ProjectReference`, ni siquiera a `Shop133.Contracts`: quien traduce un `OrderCreated` a una reserva es el consumer, y el consumer vive en la API. El día que una entidad de aquí necesite un tipo de Contracts, esa será la señal de que la traducción se coló en la capa equivocada.

### 2. El seed son 50 filas con los ids de Catalog, y con cantidades distintas de las suyas

`InventoryDb` arrancaba vacía, y con la tabla vacía **todo pedido se rechaza**: no habría camino feliz que enseñar en `3.4` ni entrada para `3.5`.

Los ids 1–50 coinciden con los que `1.4` fijó en el catálogo. Que Inventory los conozca no rompe la regla 1: son datos de arranque escritos en una migración, no una consulta a `CatalogDb`.

**Las cantidades, en cambio, no coinciden a propósito.** `Product.Stock` es el número que el catálogo *muestra*; este es el reservable. Son dos columnas con dos dueños y ninguna sincronización entre ellas — sembrarlas con el mismo valor sugeriría una relación que no existe y que nadie mantendría al día. La primera vez que alguien compare las dos tablas y vea que difieren, la respuesta correcta es "claro, son cosas distintas".

**Descartado — sembrar con huecos y ceros** para tener un rechazo reproducible sin tocar la base. No hace falta: pedir más unidades de las que hay produce el mismo rechazo, y un producto dado de alta por `POST /products` después del seed **no tiene fila aquí**, así que el rechazo por inexistencia también sale gratis. Ninguna de las 50 cantidades es 0 y las 50 son distintas entre sí, por el mismo criterio que `1.4`: que siempre haya algo comprable y que un copia-pega mal hecho se note al leer la tabla.

**Descartado — no sembrar nada** y dar de alta el stock a mano. Honesto con "Inventory es dueño de sus cantidades", pero deja el camino feliz dependiendo de un `INSERT` manual que nadie versiona, y le quita a la fixture de `3.7` los datos que `MigrateAsync()` le daría gratis — que es exactamente cómo `Catalog.Tests` obtiene los suyos desde `1.7`.

### 3. `ReserveStock`/`ReleaseStock` se quedan con `OrderLine`. La pregunta se cierra.

Aplazada en `0.3` (dos veces) y en `3.2`, siempre con el mismo motivo: "no existe el consumidor". Ya existe.

**Elegido: no partir el tipo.** Y el argumento que decide no es el que se venía repitiendo (que Inventory pueda querer el nombre para sus logs — al final no lo quiere: usa `ProductId` en todos los mensajes). Es este:

En la Fase 3 Inventory **no consume `ReserveStock`**. Consume `OrderCreated`, cuyo `Lines` es un `IReadOnlyList<OrderLine>` y no está en discusión — es un evento, la foto de lo que pasó, y ahí las cinco columnas tienen sentido. Partir solo los comandos dejaría a Inventory con **dos formas para la misma aritmética**: el consumer de evento leyendo `OrderLine` y el consumer de comando de la Fase 4 leyendo `StockLine`, haciendo lo mismo con los mismos dos campos. Eso es peor que la redundancia que se quería quitar.

Y hay un segundo motivo que apareció al escribir la tabla de reservas: **`4.4` puede quitarle `Lines` a `ReleaseStock` entero**. La PK de `StockReservations` es el `OrderId` (decisión 5), así que soltar el stock de un pedido es un `SELECT` por clave primaria. Si eso se confirma, el `StockLine` que se estaba considerando habría nacido para un solo mensaje.

**Descartado — partirlo ahora.** Añadiría un décimo tipo a los nueve que fijó la decisión 1 de `0.3`, obligaría a la saga a mapear `OrderLine → StockLine` y podría quedar huérfano en `4.4`.

La redundancia que se acepta queda medida y es menor de lo que decía la nota: de los cinco campos, el consumer usa **dos** (`ProductId`, `Quantity`) y la tabla de reservas guarda **esos mismos dos**. Sku, nombre y precio son la foto que congeló el pedido, y su dueño es Orders — copiarlos a `InventoryDb` sería una tercera copia del mismo dato.

### 4. Dos columnas (`QuantityOnHand` + `QuantityReserved`), no una que se decrementa

`Reserve()` sube `QuantityReserved` y **no toca `QuantityOnHand`**. `QuantityAvailable` es la resta, calculada y no persistida.

**Descartado — una sola columna `Quantity` que baja al reservar y sube al liberar.** Es más simple y, para el roadmap tal y como está escrito, hasta más correcto: no hay ningún paso que "confirme" una reserva contra el stock físico, así que la unidad reservada nunca vuelve a moverse. Pero hace **indistinguible "vendido" de "apartado para un pedido que todavía puede caerse"**, y esa distinción es justo la que la compensación de la Fase 4 existe para deshacer. Con dos columnas, un `SELECT` sobre `StockItems` enseña de un vistazo cuánto stock está en vuelo; con una, esa información no está en ninguna parte.

`QuantityAvailable` es calculada por el mismo criterio que `Order.Total` y `OrderItem.Subtotal`: una sola fuente de verdad, imposible de desincronizar. Necesita `Ignore()` en la configuración o **el modelo ni se construye** — es una propiedad pública sin setter ni campo de respaldo.

### 5. La clave primaria de `StockReservations` es el `OrderId`

La sección *Pendiente* de [fase_3_2.md](fase_3_2.md) dejó por escrito que `4.4` no podía decidir si `ReleaseStock` prescinde de `Lines` "antes de saber cómo quedó la tabla de reservas". Quedó así: **sin identificador propio**. La reserva *es* del pedido.

Eso le da a `4.4` todo lo que necesita para soltar stock con solo el `OrderId`, y menos datos en la compensación es menos superficie para el duplicado que el propio `///` de `ReleaseStock` advierte.

**Descartado — un `Id` propio de la reserva más un índice sobre `OrderId`.** Un identificador que nadie usa nunca para buscar, más un índice para volver a encontrar la fila por el único criterio con el que alguien la va a pedir.

Las líneas se mapean como tipo **owned** (`OwnsMany`), traducción literal de lo que `2.2` decidió para `OrderItem`: no tienen identidad fuera de su reserva, EF impide consultarlas sueltas, se cargan sin `Include` y el borrado en cascada sale del mapeo. Se paga la PK compuesta `(OrderId, Id)` con un `Id` IDENTITY que no existe en C# — así construye EF la clave de una colección owned, y no hay que "arreglarlo".

### 6. La lógica vive en el consumer, no en un servicio de `Inventory.Infrastructure`

Mismo criterio con el que `ProductsController` inyecta `CatalogDbContext` desde `1.3`: las invariantes que importan están en la entidad (`StockItem.Reserve` no deja bajar de cero), así que un `StockReservationService` sería un passthrough con una interfaz delante.

Y hay una razón que pesa más: **este método *es* el paso de la saga que el proyecto existe para hacer legible**. Esconderlo detrás de una capa sería enterrar la lección.

La reserva es **atómica**: se comprueban todas las líneas antes de tocar una sola, porque el `///` de `StockRejected` lo dice desde `0.3` — "o entra entera o no entra nada. No hay nada que compensar". Reservar sobre la marcha y abortar a mitad dejaría unidades comprometidas por un pedido que se va a cancelar: stock filtrado, que es lo que la regla 7 existe para impedir. Verificado con un pedido de dos líneas, una válida y otra imposible (ver *Verificación*).

Los problemas se acumulan en una lista y se juntan en un solo `Reason`: quien lea el mensaje quiere saber qué falló, no cuál falló primero. `Reason` es texto de diagnóstico y material para el email de `4.6`, **no un código que nadie deba parsear**.

### 7. Idempotencia de negocio por `OrderId` — que **no** es la de `3.6`

El consumer mira si ya existe una reserva para ese `OrderId` y, si la hay, no reserva otra vez y **vuelve a publicar `StockReserved`**.

No es adelantarle trabajo a `3.6`, es evitar un fallo que la decisión 5 introdujo: con el `OrderId` de clave primaria, un `OrderCreated` reentregado —RabbitMQ garantiza *al menos* una entrega— reventaría el `INSERT` y, tras los reintentos, acabaría en la cola `order-created_error`. Un pedido correcto en la cola de errores no es la lección de esta fase.

La diferencia con `3.6` importa y hay que tenerla clara: esto va por **clave de negocio** y solo protege a este consumer en su camino de éxito. La de `3.6` va por el **`MessageId` del sobre**, vale para cualquier consumer y cubre además lo que esta no cubre — el camino de rechazo, que no escribe nada y por tanto no deja rastro con el que detectar el duplicado.

Se **republica** en vez de salir en silencio: si el mensaje se repite es que algo se perdió, y bien pudo ser la respuesta.

### 8. El bloque `AddMassTransit` sigue sin extraerse — revisada la decisión 7 de `3.1`

`3.1` dejó tres copias literales y se comprometió a reevaluarlo "cuando `3.4` y `3.5` las hayan tocado". Tocada la primera, con el diff delante:

**No se extrae.** Lo único que ha divergido es el `x.AddConsumer<OrderCreatedConsumer>()`, que es precisamente la parte que no se puede compartir. Sacar a un método común lo que sí es idéntico —host y formatter— dejaría la línea que distingue a cada servicio suelta fuera de él, y eso se lee peor que la duplicación. Se vuelve a mirar en `3.5`, con la copia de Payments ya tocada.

### 9. Una regla de arquitectura nueva: los consumers viven en `Consumers/`

La suite pasa de 14 a **15**. `ConsumerFiles_LiveOnlyIn_ServiceApiConsumersFolder` convierte en ejecutable la convención que CLAUDE.md tenía escrita desde la Fase 3 — *"MassTransit consumers are not controllers"*—, con el mismo patrón de fichero que la regla del `DbContext`.

Merece test y no solo prosa porque el sitio de un consumer no es cosmético: es el único código del servicio que se ejecuta **sin que nadie haga una petición HTTP**, y mezclarlo con `Controllers/` hace que deje de verse.

Va en `ServiceBoundaryRulesTests` y no en un archivo nuevo: `3.1` separó `PackageRulesTests` porque una regla de licencia no es una regla de capas, no porque cada regla merezca archivo.

**Se rompió a propósito** para comprobar que el filtro casa — ver *Verificación*. Un filtro que nunca casa pasa en verde para siempre.

---

## Cambios

### Nuevos — `Inventory.Infrastructure`

| Archivo | Rol |
|---|---|
| [Inventory.Infrastructure.csproj](../src/Services/Inventory/Inventory.Infrastructure/Inventory.Infrastructure.csproj) | Proyecto nuevo. `Microsoft.EntityFrameworkCore.SqlServer` 10.0.8 y **cero** `ProjectReference`. |
| [Entities/StockItem.cs](../src/Services/Inventory/Inventory.Infrastructure/Entities/StockItem.cs) | El stock reservable de un producto. `CanReserve`/`Reserve`; sin `Release()`, que es de `4.4`. |
| [Entities/StockReservation.cs](../src/Services/Inventory/Inventory.Infrastructure/Entities/StockReservation.cs) | La reserva de un pedido. PK = `OrderId`, líneas con `AsReadOnly()`. |
| [Entities/StockReservationLine.cs](../src/Services/Inventory/Inventory.Infrastructure/Entities/StockReservationLine.cs) | `{ ProductId, Quantity }`. Sin `Id` ni `OrderId`: tipo owned. |
| [Persistence/InventoryDbContext.cs](../src/Services/Inventory/Inventory.Infrastructure/Persistence/InventoryDbContext.cs) | La sesión con `InventoryDb`. Dos `DbSet`, ninguno para las líneas. |
| [Persistence/Configurations/StockItemConfiguration.cs](../src/Services/Inventory/Inventory.Infrastructure/Persistence/Configurations/StockItemConfiguration.cs) | `ValueGeneratedNever()` sobre `ProductId`, `Ignore()` sobre `QuantityAvailable`, `HasData`. |
| [Persistence/Configurations/StockReservationConfiguration.cs](../src/Services/Inventory/Inventory.Infrastructure/Persistence/Configurations/StockReservationConfiguration.cs) | PK `OrderId`, `OwnsMany` de las líneas, acceso por campo `_lines`. |
| [Persistence/Seed/InventorySeedData.cs](../src/Services/Inventory/Inventory.Infrastructure/Persistence/Seed/InventorySeedData.cs) | Las 50 filas de stock inicial. |
| `Migrations/20260828235412_InitialCreate.cs` | Las tres tablas, sin datos. |
| `Migrations/20260828235436_SeedStockItems.cs` | Los 50 `InsertData`, en su propia migración. |

### Nuevos — `Inventory.API`

| Archivo | Rol |
|---|---|
| [Consumers/OrderCreatedConsumer.cs](../src/Services/Inventory/Inventory.API/Consumers/OrderCreatedConsumer.cs) | **El primer consumer del proyecto.** Reserva o rechaza, y publica. |

### Modificados

| Archivo | Qué cambió |
|---|---|
| [Inventory.API/Program.cs](../src/Services/Inventory/Inventory.API/Program.cs) | Guarda de `ConnectionStrings:InventoryDb` + `AddDbContext`; `x.AddConsumer<OrderCreatedConsumer>()`; reescritos los comentarios de `3.1` que ya eran falsos. |
| [Inventory.API.csproj](../src/Services/Inventory/Inventory.API/Inventory.API.csproj) | `Microsoft.EntityFrameworkCore.Design` 10.0.8 con `PrivateAssets="all"` y `ProjectReference` a `Inventory.Infrastructure`. |
| [shop133.slnx](../shop133.slnx) | El proyecto nuevo. |
| [ServiceBoundaryRulesTests.cs](../tests/Shop133.ArchitectureTests/ServiceBoundaryRulesTests.cs) | Regla nueva (decisión 9) y corregido un comentario de `0.6` que decía que no había ningún `DbContext`. |

### Lo que no se tocó

**Nada bajo `src/Shared/Shop133.Contracts`** — decisión 3. **Ningún `Program.cs` de Orders o Payments.** **`db/init/01-create-databases.sql` ni `.env.example`**: `InventoryDb`, `inventory_user` e `INVENTORY_DB_PASSWORD` existen desde `0.4`. **`docker-compose.yml`**: Inventory sigue sin contenedor.

---

## Detalles que cuestan tiempo

**`HasData` se cuela en `InitialCreate` y separarlo exige comentar la línea.** La primera `migrations add InitialCreate` salió con los 50 `InsertData` dentro, porque EF compara el modelo actual contra la última migración y no sabe de intenciones. Para conseguir la separación que `1.4` hizo entre `AddProductCategories` y `SeedSouvenirCatalog` hay que: `migrations remove`, **comentar el `HasData`**, `migrations add InitialCreate`, descomentarlo y `migrations add SeedStockItems`. No hay bandera que lo haga.

**Sin `ValueGeneratedNever()`, `StockItems.ProductId` habría salido IDENTITY, y el fallo no sería un error.** La convención de EF para una PK `int` es `ValueGeneratedOnAdd`. Con eso, insertar el `StockItem` del producto 7 crearía la fila con el número que le tocara al contador — stock apuntando al producto equivocado, en silencio. Comprobado en la migración generada: la columna sale `int NOT NULL` **sin** anotación `SqlServer:Identity`.

**EF emite `SET IDENTITY_INSERT` igualmente, envuelto en un `IF EXISTS`.** El SQL del seed lleva `IF EXISTS (SELECT * FROM sys.identity_columns WHERE ...) SET IDENTITY_INSERT [StockItems] OFF`. Es una guarda condicional, no una contradicción de lo anterior: como no hay columna identity, el `IF` no entra. Ver esa línea en la salida y concluir que el `ValueGeneratedNever()` no funcionó es un rato perdido.

**User Secrets solo se cargan en `Development`, y `--no-launch-profile` no es `Development`.** El primer arranque de Inventory.API murió con la guarda de `ConnectionStrings:InventoryDb` estando el secreto perfectamente puesto. Es el mismo mecanismo que CLAUDE.md documenta para `dotnet ef` (que fuerza `ASPNETCORE_ENVIRONMENT=Development` por su cuenta) visto desde el otro lado. La guarda hizo su trabajo — el mensaje decía exactamente qué clave faltaba— pero la causa no era la que parecía.

**En este servicio la guarda vale más que en Orders.** Orders.API sirve HTTP: sin connection string, el fallo aparece en la primera petición. Inventory.API no tiene un solo endpoint; sin la guarda, el `DbContext` se resolvería dentro del consumer y el fallo aparecería como **un mensaje en `order-created_error`**, que es varios saltos más lejos de la causa.

**`Invoke-RestMethod` sobre la API de RabbitMQ devuelve un objeto envuelto en PowerShell 5.1.** `Select-Object source, destination` sobre `/api/bindings/%2F` imprimió una fila en blanco, y un `ForEach-Object` sobre `/api/exchanges/%2F` imprimió `System.Object[]`: el pipeline recibe **un** elemento que es el array entero. Es primo del problema que `3.3` midió con el filtro `Where-Object`. Lo que funciona es `curl.exe -s -u guest:guest ... | ConvertFrom-Json` y recorrerlo con `foreach`.

**Publicar a mano por la API de management: el JSON no puede llevar BOM y las comillas hay que escaparlas.** `Out-File -Encoding utf8` en PS 5.1 escribe BOM y RabbitMQ responde `{"error":"bad_request","reason":"not_json"}` — un mensaje que culpa al JSON, que es correcto. Se arregla con `[System.IO.File]::WriteAllText($path, $json, (New-Object System.Text.UTF8Encoding($false)))`. Y en el `-d` en línea, las comillas van como `\"`.

**Una cola espía necesita también que exista su exchange.** `Shop133.Contracts.Events:StockReserved` no existe hasta que alguien publica, así que hay que declararlo antes de ligar la cola — **`fanout`, `durable:true`, `auto_delete:false`**, que es exactamente lo que MassTransit declara. Con otras propiedades, la publicación posterior fallaría con `PRECONDITION_FAILED`. La cola, además, `durable:true`, porque RabbitMQ 4.x rechaza las transitorias no exclusivas (medido en `3.3`).

**`decimal` viaja como cadena JSON y pierde los ceros a la derecha, confirmado otra vez.** El total del pedido de prueba era `974.50` y el mensaje real dice `"amount": "974.5"`. Inofensivo entre servicios .NET; digno de recordar el día que lea la cola algo que no sea .NET.

**El sobre encadena la traza sin que nadie lo configure.** El `StockReserved` publicado lleva `conversationId` heredado del `OrderCreated` (`c01e0000-…`) y `sourceAddress: rabbitmq://localhost/order-created`. El `correlationId` sigue en `null` — la correlación por `OrderId` la configura la saga en `4.1`, no es un campo de contrato.

**`Configured endpoint order-created` es la línea que confirma que el consumer está vivo.** Aparece en el arranque, antes de `Bus started`. Si no está, el `AddConsumer` no llegó al `ConfigureEndpoints` y **el mensaje se pierde en silencio** — el fallo que `3.1` dejó prevenido dejando la línea puesta con cero consumers.

**MassTransit crea `order-created_error` de forma perezosa.** Tras arrancar solo aparece `order-created`. La ausencia de la cola de errores no significa nada; se crea con el primer fallo.

---

## Verificación

Ejecutado el 2026-08-28. Salidas reales.

### 1. Build y tests

```
dotnet build shop133.slnx
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

```
tests\Shop133.ArchitectureTests\bin\Debug\net10.0\Shop133.ArchitectureTests.exe
   Shop133.ArchitectureTests  Total: 15, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 1.424s

tests\Services\Catalog\Catalog.Tests\bin\Debug\net10.0\Catalog.Tests.exe
   Catalog.Tests  Total: 19, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 99.623s

tests\Services\Orders\Orders.Tests\bin\Debug\net10.0\Orders.Tests.exe
   Orders.Tests  Total: 10, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 62.600s
```

**44 tests en total** (15 `Fast` + 29 `Docker`). Ninguna regresión. Smart App Control no bloqueó nada esta vez.

### 2. La regla nueva rota a propósito

Con un `ThrowawayConsumer.cs` colocado en `Inventory.API/Controllers/`:

```
Shop133.ArchitectureTests.ServiceBoundaryRulesTests.ConsumerFiles_LiveOnlyIn_ServiceApiConsumersFolder [FAIL]
  Un consumer de MassTransit vive en Consumers/, dentro del .API de su propio servicio, y no es
  un controller. Fuera de lugar: src/Services/Inventory/Inventory.API/Controllers/ThrowawayConsumer.cs

   Shop133.ArchitectureTests  Total: 15, Errors: 0, Failed: 1
```

Borrado el archivo, vuelve a 15/15. El filtro casa y el mensaje nombra al culpable.

### 3. Esquema y seed, leídos con `inventory_user` (nunca `sa`)

```
name
---------------------
__EFMigrationsHistory
StockItems
StockReservationLines
StockReservations

StockRows   TotalOnHand TotalReserved
----------- ----------- -------------
         50        4503             0
```

### 4. La primera cola y el primer binding del sistema

Arranque de Inventory.API:

```
info: MassTransit[0]
      Configured endpoint order-created, Consumer: Inventory.API.Consumers.OrderCreatedConsumer
info: MassTransit[0]
      Bus started: rabbitmq://localhost/
```

Topología (vía `curl.exe`, ver *Detalles*):

```
=== BINDINGS ===
                                              -> order-created             [queue]
Shop133.Contracts.Events:OrderCreated         -> order-created             [exchange]
order-created                                 -> order-created             [queue]

=== EXCHANGES (no amq.*) ===
Shop133.Contracts.Events:OrderCreated              fanout
order-created                                      fanout
```

El fanout que `3.3` dejó sin colas ya tiene una detrás.

### 5. Camino feliz, y el `Amount` que no se podía olvidar

`POST /orders` con producto 1 (×3, 249.00) y producto 31 (×5, 45.50) → `201`, `total: 974.5`.

```
info: Inventory.API.Consumers.OrderCreatedConsumer[0]
      Stock reservado para el pedido 0c84bf35-f9b2-4615-a702-1e0bc694a29f: 2 línea(s)
      por un importe de 974.5.
```

Mensaje real leído de la cola espía:

```json
{
  "messageId": "94670000-dce1-6046-bc4b-08df056043dd",
  "correlationId": null,
  "conversationId": "c01e0000-dce1-6046-2f2a-08df056042ac",
  "sourceAddress": "rabbitmq://localhost/order-created",
  "messageType": ["urn:message:Shop133.Contracts.Events:StockReserved"],
  "message": { "orderId": "0c84bf35-f9b2-4615-a702-1e0bc694a29f", "amount": "974.5" }
}
```

**`amount` es 974.5 y no 0.** Estado en la base:

```
ProductId   QuantityOnHand QuantityReserved Available
----------- -------------- ---------------- -----------
          1             42                3          39
         31            210                5         205

OrderId                              CreatedAt
------------------------------------ ---------------------------------
0C84BF35-F9B2-4615-A702-1E0BC694A29F 2026-08-28 23:58:32.0488894 +00:00

OrderId                              Id  ProductId   Quantity
------------------------------------ --- ----------- -----------
0C84BF35-F9B2-4615-A702-1E0BC694A29F   1           1           3
0C84BF35-F9B2-4615-A702-1E0BC694A29F   2          31           5
```

`QuantityOnHand` intacto, `QuantityReserved` subido, reserva y líneas persistidas.

### 6. Producto inexistente — la mitad de existencia, cerrada

`POST /orders` con `productId: 999999` → **`HTTP 201`**. El pedido se crea igual; Orders ya no valida existencia.

```json
{ "orderId": "f0ba8caa-d536-4021-a555-749f5c87ae80",
  "reason": "el producto 999999 no existe en el inventario" }
```

En `2.3` esto era un `400`. Ahora es un `201` seguido de una cancelación: la validación síncrona convertida en un estado del pedido.

### 7. Stock insuficiente y atomicidad

Producto 9 tiene 12 unidades; se piden 99 → `201` y `StockRejected`:

```
el producto 9 tiene 12 unidad(es) disponible(s) y se piden 99
```

**Atomicidad**, que es el caso que importa: pedido con línea válida (producto 2, ×4, hay 65) **y** línea imposible (producto 9, ×99).

```
ProductId   QuantityOnHand QuantityReserved
----------- -------------- ----------------
          2             65                0
          9             12                0

Reservas
-----------
          1
```

**El producto 2 quedó en 0 reservado** aunque su línea era perfectamente servible, y no se creó reserva. O entra entera o no entra nada.

### 8. Duplicado por `OrderId`

Republicado a mano el mismo `OrderCreated` del pedido feliz:

```
info: Inventory.API.Consumers.OrderCreatedConsumer[0]
      El pedido 0c84bf35-f9b2-4615-a702-1e0bc694a29f ya tenía stock reservado
      (reserva del 08/28/2026 23:58:32 +00:00); no se reserva de nuevo y se reenvía StockReserved.

ProductId   QuantityReserved        Reservas    Lineas
----------- ----------------        --------    ------
          1                3               1         2
         31                5
```

No hubo doble reserva, y salió un `StockReserved` nuevo con el importe correcto:

```json
{"orderId":"0c84bf35-f9b2-4615-a702-1e0bc694a29f","amount":"974.5"}
```

### 9. Lo que se publica al vacío, y es correcto

`StockReserved` y `StockRejected` **no tienen consumidor**: Payments llega en `3.5` y Notifications en `4.6`. Sin las colas espía de esta verificación, los dos exchanges no existirían siquiera. No es un fallo aunque lo parezca — es la misma situación en la que `3.3` dejó a `OrderCreated`. Las colas espía se borraron al terminar; queda solo `order-created`.

---

## Pendiente

- **Concurrencia.** No hay token de concurrencia optimista (`rowversion`) sobre `StockItem`. Dos `OrderCreated` del mismo producto procesados a la vez pueden sobre-reservar: los dos leen el mismo `QuantityAvailable` y los dos pasan la comprobación. Hueco real, sin punto asignado; `8.2` menciona la concurrencia optimista pero para la saga. El prefetch por defecto de MassTransit lo hace alcanzable.
- **Una reserva nunca baja el stock físico.** `QuantityReserved` sube y solo bajará con el `ReleaseStock` de `4.4`. Ningún paso del roadmap convierte una reserva confirmada en una baja de `QuantityOnHand`, así que tras un `PaymentCompleted` las unidades quedan reservadas para siempre. Es coherente con lo que hay escrito, pero conviene decidirlo — el sitio natural sería un consumer de `OrderConfirmed` en la Fase 4.
- **`3.5`** — Payments consume el `StockReserved` que este punto publica y usa su `Amount`.
- **`3.6`** — la tabla de `MessageId` procesados. La idempotencia de negocio de la decisión 7 no la sustituye: no cubre el camino de rechazo, que no escribe nada.
- **`3.7`** — `Inventory.Tests` con el harness en memoria, y el test de idempotencia. Ahí llega la cuarta copia de `SqlServerContainerFixture` y se decide su extracción. También llegará entonces la pregunta de si `Inventory.API` necesita `public partial class Program { }`: hoy no lo tiene porque no hay `WebApplicationFactory`.
- **`4.4`** — con `StockReservations` sobre la mesa, decidir si `ReleaseStock` prescinde de `Lines`. `StockItem.Release()` se escribe allí.
- **`4.8` / `4.9`** — el importe. Inventory cierra la existencia y no toca el precio: un pedido de un producto que existe a `0.01` atraviesa esta reserva sin objeción.
- **Contenedor.** Inventory.API sigue sin `Dockerfile` ni servicio en compose. Cuando llegue, `ConnectionStrings__InventoryDb` se construye con `${INVENTORY_DB_PASSWORD}` y host `sqlserver`, y habrá que releer el `UseHttpsRedirection()` sin guardar que este `Program.cs` todavía arrastra.
