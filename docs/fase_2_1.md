# Fase 2.1 — Modelo `Order`, `OrderItem`

**Fecha:** 2026-08-20 · **Estado:** completado · **Roadmap:** [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md)

---

## Objetivo

Arrancar la Fase 2 escribiendo **el agregado y nada más**. Es el equivalente exacto de [1.1](fase_1_1.md) en el servicio de pedidos: hasta aquí `src/Services/Orders/` no tenía un solo `.cs` propio más allá del `Program.cs` de plantilla.

Está en primer lugar de la fase por el mismo motivo que 1.1 lo estaba en la suya: lo que este punto fija —el tipo del id, qué es un pedido válido, qué congela una línea— lo hereda todo lo demás. `2.2` lo mapea a `OrdersDb`, `2.3` lo crea desde `POST /orders`, `3.3` lo publica como `OrderCreated` y la saga de la Fase 4 le mueve el estado. Cambiar el tipo del id en `2.3`, con migraciones aplicadas, es rehacer la fase.

**Fuera de alcance deliberadamente:**

- **EF Core.** `Orders.Domain` no tiene ni un `PackageReference` y no puede tenerlo: la regla 5 lo prohíbe y `LayeringRulesTests.EfCorePackages_LiveOnlyIn_InfrastructureProjects` lo comprueba. `OrdersDbContext`, la configuración y las migraciones son **2.2**.
- **DTOs HTTP y el endpoint.** `2.3`.
- **Publicar `OrderCreated`.** `3.3`. Este punto no construye ni un solo mensaje de `Shop133.Contracts`.
- **Métodos de transición de estado** (`Confirm()`, `Cancel()`). Decisión 4.

---

## Decisiones

### 1. Las entidades viven en `Orders.Domain`, no en `Orders.Infrastructure`

Es la decisión opuesta a la que tomó 1.1 con `Product`, que vive en `Catalog.Infrastructure`. La asimetría no es un descuido: es la misma que ya explicaba la decisión 1 de [fase_1_1.md](fase_1_1.md) desde el otro lado. Catalog **no tiene** proyecto de dominio porque es un CRUD — tres capas para mover un `nvarchar` de la base al JSON. Orders sí lo tiene, porque ahí vive la `OrderStateMachine` de la Fase 4, y un pedido con su máquina de estados al lado es exactamente lo que ese proyecto existe para contener.

**Descartado:** ponerlas en `Orders.Infrastructure` por simetría con `Product`. Dejaría a `Orders.Domain` conteniendo una máquina de estados que razona sobre un pedido que no puede ver, porque la flecha va `.Infrastructure → .Domain` y no al revés.

No hace falta discutirlo mucho más porque **ya estaba decidido por escrito**: [OrderLine.cs](../src/Shared/Shop133.Contracts/OrderLine.cs), desde 0.3, dice literalmente *"No es la entidad OrderItem de Orders.Domain — es su representación de transporte"*. Este punto se limita a cumplirlo.

### 2. `enum` para el estado, no tabla de catálogo — al revés que `Category` en 1.4

**Descartado:** una tabla `OrderStatuses` con FK desde `Orders`, por simetría con lo que 1.4 hizo con `Category`.

**Elegido:** un `enum` en `Orders.Domain`. El contraste con 1.4 es lo que da valor a la decisión, porque el criterio es el mismo aplicado a dos casos que caen de distinto lado:

| | `Category` (1.4) | `OrderStatus` (2.1) |
|---|---|---|
| Qué es | Texto de interfaz | Una rama de la máquina de estados |
| Quién lo añade | Quien administra el catálogo, con un `INSERT` | Quien escribe la transición y el evento |
| ¿Crece sin tocar código? | **Sí** | **No** |
| Coste de la tabla | Una FK y un viaje extra | Una FK y un `JOIN`, a cambio de nada |

La regla que queda es: **la tabla gana cuando el conjunto puede crecer sin recompilar**. Añadir un estado de pedido obliga a escribir la transición que lleva a él y el mensaje que lo publica, o sea a desplegar `Orders.Domain` de todas formas; la tabla no ahorra ese despliegue.

**Los valores son explícitos (`Pending = 1`, …)** y eso no es estilo. EF Core persiste un `enum` como su valor numérico (2.2), y sin los números escritos ese valor depende del *orden de declaración*: insertar un estado nuevo en medio de la lista renumeraría en silencio todas las filas ya guardadas. Con los números a la vista, el contrato con la base de datos se ve y no se puede romper reordenando.

Los estados intermedios que nombra 4.2 (`StockPending`, `PaymentPending`, `CompensatingStock`) **no** están aquí: son estados de la *instancia de saga*, no del pedido, y van en el tipo que persiste 4.5.

### 3. `Total` calculado, no persistido

**Descartado:** una columna `Total` calculada en el constructor. Permite listar pedidos sin cargar las líneas, y congelaría el número aunque la fórmula cambiara algún día (impuestos, envío).

**Elegido:** `public decimal Total => _items.Sum(item => item.Subtotal);`. Una sola fuente de verdad, imposible de desincronizar. Es el mismo criterio que hace que nadie descuente de `Product.Stock` al crear un pedido: dos sitios con el mismo número acaban discrepando, y el bug aparece meses después sin que nadie sepa cuál de los dos miente.

El día que el total deje de ser "la suma de las líneas" —porque haya impuestos o envío— **deja de ser una propiedad calculada y merece columna propia**. Hoy no lo es. `2.2` tendrá que mapearlo con `Ignore()`: EF ve una propiedad `decimal` de solo lectura e intenta crearle columna.

`OrderItem.Subtotal` sigue el mismo criterio por la misma razón.

### 4. Sin `Confirm()` ni `Cancel()` todavía

**Descartado:** escribir las dos transiciones ahora, con guardas que solo las permitan desde `Pending`. Dejaría el grafo de estados legal en forma ejecutable desde 2.1 en vez de en prosa.

**Elegido:** `Status` es de solo lectura desde fuera y solo se asigna en el constructor. Es el precedente literal de 1.1, que dejó a `Product` sin `Update()` hasta que 1.3 tuvo el caso de uso — *"inventarle la firma antes de tener el caso de uso"*. En la Fase 2 el único estado alcanzable es `Pending`, y no por limitación: aceptar el pedido es lo único que Orders.API sabrá hacer. Quien mueve el estado es la saga, así que las firmas (¿`Cancel` lleva `reason`? ¿es idempotente ante un evento duplicado, que la regla 6 exige?) se escriben en **4.2/4.3**, con la máquina de estados delante y no adivinando.

Verificado por reflexión: `Order` no expone **ningún** método público hoy.

### 5. `Guid.NewGuid()` para el `Id` — generado por la entidad, no por la base

Que el `Id` sea `Guid` y no `int` no es una decisión nueva: es la 4 de [fase_0_3.md](fase_0_3.md), y es la mitad que **no** se revirtió en 1.1. Sigue viva por su argumento original — `OrderId` es la clave de correlación de toda la saga, así que Orders.API tiene que poder publicar `OrderCreated` sin haber esperado a un `INSERT`. Con `IDENTITY` habría que hacer `INSERT` → leer el id → publicar, metiendo la base de datos en el camino crítico de un flujo que existe justamente para ser asíncrono.

Lo que sí decide este punto es **quién lo genera y con qué**:

**Descartado — recibirlo como parámetro del constructor.** Permitiría a los tests fijarlo y abriría la puerta a una clave de idempotencia enviada por el cliente. Se descarta porque hoy nadie la necesita (la idempotencia de 3.6 es sobre `MessageId`, no sobre `OrderId`) y porque un parámetro obliga a todos los llamantes a inventarse un `Guid` correcto; generándolo dentro, un `Order` sin id no puede existir. Los tests leen `order.Id`.

**Descartado — `Guid.CreateVersion7()`.** Es lo que uno escribe por defecto desde .NET 9. Se descarta reutilizando la medición de 1.1: SQL Server compara `uniqueidentifier` empezando por los **últimos 6 bytes**, justo donde v7 pone la parte aleatoria, así que la ordenación de v7 **no existe dentro de la base**. Se pagaría la complejidad sin cobrar la ventaja.

Cómo se indexa esta columna —PK *clustered* o no— es la pregunta que 1.1 dejó explícitamente abierta para **4.5**, y sigue abierta.

### 6. `OrderItem` no tiene `Id` propio ni referencia de vuelta al pedido

**Descartado:** un `int Id` con `IDENTITY` por simetría con `Product`, más un `Guid OrderId`.

**Elegido:** los cinco campos del *snapshot* y nada más. Una línea de pedido **no tiene identidad fuera de su pedido**: nadie la pide por id, ningún mensaje de `Shop133.Contracts` la referencia (los contratos llevan `ProductId`, nunca un id de línea) y no existe un `GET /orders/{id}/items/{itemId}` en el roadmap.

Y hay un motivo de reparto de responsabilidades: cómo se le da clave primaria en la base —clave sombra sobre una entidad normal, o `OwnsMany` como tipo poseído— es una decisión de **persistencia**, y este punto no tiene EF Core. Se decide en **2.2**, igual que 1.1 dejó el índice único del `Sku` para 1.2.

### 7. Las longitudes se duplican; **no** se importan de `Product`

`OrderItem.ProductSkuMaxLength = 50` y `ProductNameMaxLength = 200` coinciden con las de `Product`. Copiar dos números pide a gritos reutilizar las constantes que ya existen.

**Descartado — `Product.SkuMaxLength`.** Obligaría a `Orders.Domain` a referenciar `Catalog.Infrastructure`. Eso es la **regla 1 rota en tiempo de compilación** —Orders dependiendo del modelo interno de Catalog, que es la misma enfermedad que compartir base de datos— y la **regla 5 rota de plano**, porque la capa de dominio solo puede ver `Shop133.Contracts`. `LayeringRulesTests.OrdersDomain_ProjectReferences_ContainOnlyContracts` lo tumbaría en el acto, que es exactamente para lo que existe.

Y son independientes de verdad, no por formalismo: una foto solo tiene que aguantar lo que Catalog mandó **ese día**. Si Catalog amplía su `Sku` a 80 caracteres, esta constante puede quedarse en 50 sin que ningún pedido histórico se rompa; lo que fallaría es un pedido *nuevo* de un producto con código largo — y ese fallo es correcto, porque significa que los dos servicios ya no encajan y alguien tiene que enterarse.

### 8. El SKU se recorta pero **no** se pasa a mayúsculas — al revés que `Product.Sku`

`Product.Sku` se normaliza con `ToUpperInvariant()` (decisión 9 de 1.1) para que la unicidad no dependa de la *collation*. `OrderItem.ProductSku` solo hace `Trim()`.

**Elegido:** copiar, no corregir. Normalizar el código de producto es trabajo de quien es dueño del dato, y ese es Catalog. Aquí no hay ninguna unicidad que sostener, que era el motivo entero de normalizar en 1.1. Hoy es un *no-op* porque Catalog ya lo emite en mayúsculas; el día que llegue algo en minúsculas, lo correcto es que el pedido enseñe lo que le mandaron y no una versión maquillada por Orders.

El mismo criterio aplica a `CustomerEmail`, que tampoco se pasa a minúsculas: la parte local de una dirección es sensible a mayúsculas según el RFC, así que normalizarla es corregir un dato ajeno.

### 9. `CustomerEmail` sin validación de formato

**Descartado:** una regex, o `MailAddress.TryCreate`.

**Elegido:** no vacío y `CustomerEmailMaxLength = 320` (RFC 5321: 64 de parte local + `@` + 255 de dominio). Es el criterio de la decisión 8 de 1.1 sobre `ImageUrl` — **se valida lo que se sabe, no lo que se supone** — y el de la decisión 4 sobre DataAnnotations: su sitio es el DTO de entrada de `2.3`, que llevará `[EmailAddress]` y `[MaxLength(Order.CustomerEmailMaxLength)]` leyendo la constante, nunca un literal.

Que el campo esté en la entidad y no solo en el mensaje viene de la decisión 3 de [fase_0_3.md](fase_0_3.md): `OrderConfirmed` y `OrderCancelled` lo llevan dentro porque Notifications.API **no puede leer `OrdersDb`**. Alguien tiene que guardarlo, y ese alguien es el pedido.

### 10. `DateTimeOffset.UtcNow` directo, no `TimeProvider`

**Descartado:** el `TimeProvider` de .NET 8 inyectado por constructor, que es la respuesta "correcta" para que un test pueda fijar el reloj. Añade un parámetro que **todos** los llamantes tienen que arrastrar para que un test afirme un sello de tiempo que ningún test afirma.

`DateTimeOffset` y no `DateTime`: mapea a `datetimeoffset` en 2.2 sin ambigüedad de `Kind`, que es el bug clásico de guardar un `DateTime` local y leerlo como `Unspecified`.

`CreatedAt` no lo pide el roadmap, que solo nombra el estado. Se añade porque un pedido sin fecha no es un pedido, y porque la página de estado de 6.5 lo va a necesitar.

### 11. Un pedido se construye válido: al menos una línea y sin `ProductId` repetido

**Descartado:** un `AddItem()` que vaya añadiendo líneas después de construir. Es la forma habitual y encaja bien con un carrito.

**Elegido:** las líneas entran por el constructor. Si se añadieran después, existiría una ventana en la que un `Order` **vacío** está en el `ChangeTracker` y el siguiente `SaveChanges` lo escribiría — el mismo argumento por el que `Product.Apply` valida en locales y asigna en bloque.

La invariante interesante es la segunda: **dos líneas del mismo `ProductId` se rechazan en vez de sumarse**. No es limpieza. En `3.4` estas líneas viajan dentro de `ReserveStock`, y un `Inventory.API` que reciba dos entradas del mismo producto tiene que decidir si reserva la suma o si la segunda es un duplicado — una ambigüedad que no debe salir de aquí. Agrupar es trabajo de quien construye el pedido (`2.3`); el agregado solo afirma la invariante. Dos líneas con el **mismo Sku** y distinto `ProductId` sí se aceptan: el `ProductId` es la referencia, el Sku es texto congelado.

---

## Cambios

Tres archivos de código, todos nuevos, todos en `Orders.Domain/Entities/`.

| Archivo | Rol |
|---|---|
| [src/Services/Orders/Orders.Domain/Entities/Order.cs](../src/Services/Orders/Orders.Domain/Entities/Order.cs) | El agregado: `Id` (`Guid`), `CustomerEmail`, `Status`, `CreatedAt`, `Items`, `Total` calculado, y las guardas del constructor. |
| [src/Services/Orders/Orders.Domain/Entities/OrderItem.cs](../src/Services/Orders/Orders.Domain/Entities/OrderItem.cs) | La línea congelada: los cinco campos de `OrderLine` más `Subtotal`. Sin `Id`. |
| [src/Services/Orders/Orders.Domain/Entities/OrderStatus.cs](../src/Services/Orders/Orders.Domain/Entities/OrderStatus.cs) | El `enum` con `Pending = 1`, `Confirmed = 2`, `Cancelled = 3`. |

El estilo está calcado de [Product.cs](../src/Services/Catalog/Catalog.Infrastructure/Entities/Product.cs): `sealed class` mutable con setters privados (no `record`), guardas en el constructor (no DataAnnotations), constantes de longitud públicas, constructor privado sin parámetros para EF Core y el mismo helper `Validated(value, maxLength, paramName)`.

**Ningún `.csproj` se tocó**, y aquí eso importa doblemente: `Orders.Domain` sigue con su única `ProjectReference` a `Shop133.Contracts` (regla 5) y sin un solo `PackageReference`. El suite de arquitectura **sigue en 12 tests**, porque este punto no introduce una regla nueva en CLAUDE.md — las que toca (1, 5) ya son ejecutables.

⚠️ **Este punto no cruza ninguna frontera de servicio.** No se tocó `Shop133.Contracts`: `Order` y `OrderItem` **no usan ni un solo tipo de Contracts**, y `OrderItem` duplica la forma de `OrderLine` en vez de contenerlo. Esa duplicación es la decisión de siempre — la entidad puede ganar columnas sin que sea un *breaking change* del contrato, y al revés. Quien traduce entre las dos es `2.3`.

Otros archivos: [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md) (checkbox 2.1), [docs/README.md](README.md) (fila del índice) y [CLAUDE.md](../CLAUDE.md) (tabla de estado y párrafo de situación).

---

## Detalles que cuestan tiempo

### `IReadOnlyList<T>` no protege la colección — un cast la abre entera

Este es el hallazgo del punto, y salió de la sonda, no de leer el código. La forma canónica de exponer una colección de agregado es:

```csharp
private readonly List<OrderItem> _items;
public IReadOnlyList<OrderItem> Items => _items;      // parece seguro
```

Y no lo es. `IReadOnlyList<T>` **declara lo que el llamante puede hacer, no lo que el objeto es**: el objeto sigue siendo un `List<OrderItem>`, y el cast está a una línea. Medido, con esa versión del código:

```
  FAIL  Items no es un List<OrderItem> casteable  (List`1)
  FAIL  Add por ICollection<T> rechazado  (añadió la línea)
```

Es decir, `((ICollection<OrderItem>)order.Items).Add(...)` mete una línea en el pedido **saltándose la invariante de "sin ProductId repetido"** que el constructor acababa de comprobar. Y no hace falta ni un cast explícito feo: `ICollection<T>` es una interfaz que `List<T>` implementa, así que el compilador lo acepta sin quejarse.

La corrección es una llamada:

```csharp
public IReadOnlyList<OrderItem> Items => _items.AsReadOnly();
```

`AsReadOnly()` envuelve la lista en un `ReadOnlyCollection<OrderItem>`, cuyo `Add` lanza `NotSupportedException`. Con ella, las dos comprobaciones pasan:

```
  PASS  Items no es un List<OrderItem> casteable  (ReadOnlyCollection`1)
  PASS  Add por ICollection<T> rechazado  (NotSupportedException)
```

El precio es una asignación por cada lectura de la propiedad, que a esta escala no se nota. La alternativa —guardar el `ReadOnlyCollection` en un segundo campo— ahorra esa asignación a cambio de un campo más que explicar; no compensa.

**Lo que hay que recordar:** una copia defensiva en el constructor y un tipo de retorno de solo lectura son **dos protecciones distintas**, y la primera pasando no implica que la segunda funcione. En la sonda la copia defensiva pasó desde el primer intento mientras la segunda fallaba.

### Dos de los cuatro fallos eran de la sonda, no del código

Dos de los cuatro fallos de la primera pasada **no eran del código**:

```
  FAIL  customerEmail de 321 caracteres  (no lanzó nada)
  FAIL  customerEmail de 320 caracteres aceptado
```

`new string('a', 311) + "@x.com"` da **317** caracteres, no 321: `"@x.com"` son 6, no 10. La guarda funcionaba perfectamente; lo que estaba mal era el caso de prueba. Se anota porque es el modo de fallo más caro de una sonda —**un test que afirma lo que no cree afirmar**— y porque durante un minuto pareció que faltaba la validación de longitud.

### El constructor de EF necesita la lista inicializada, no `null!`

En `Product` los campos de texto del constructor privado van a `null!` porque EF **asigna** las propiedades por reflexión justo después. Con una colección no es lo mismo: EF no reemplaza la lista, **la rellena** — hace `Add` sobre la colección que encuentra en el campo. Con `_items = null!` en el constructor privado, cargar un pedido con líneas reventaría con `NullReferenceException` dentro de la materialización de EF, lejos de aquí.

Por eso el constructor de EF hace `_items = [];` y el resto sí van a `null!`. Comprobado en la sonda: un `Order` materializado por el constructor privado tiene `Items` vacía, no nula.

### `ToList()` antes de validar, y es la copia defensiva a la vez

El constructor recibe un `IEnumerable<OrderItem>`, que puede ser una consulta perezosa que se recorre distinto cada vez. Si se validara sobre el `IEnumerable` y se guardara otro recorrido, la validación y lo guardado podrían no ser lo mismo. Un solo `ToList()` al principio resuelve las dos cosas: materializa una vez y, de paso, **es** la copia defensiva — la lista del llamante puede seguir mutando después sin que el pedido se entere. Medido: se vació la lista del llamante y `order.Items.Count` siguió en 2.

### `ArgumentException.ThrowIfNullOrWhiteSpace` sigue lanzando dos tipos distintos

Igual que en 1.1: con `null` lanza `ArgumentNullException`, con `""` o `"   "` lanza `ArgumentException`. Importa para `2.3` exactamente como importó para 1.3 — un `catch (ArgumentException)` cubre los dos porque el primero hereda del segundo, y todos salen con su `paramName` puesto, que es lo que permitirá devolver un `400` que nombre el campo. Verificado para los seis parámetros de los dos constructores.

---

## Verificación

Ejecutado el 2026-08-20. Salidas reales.

| Check | Resultado |
|---|---|
| `dotnet build src/Services/Orders/Orders.Domain/Orders.Domain.csproj` | **Build succeeded. 0 Warning(s), 0 Error(s)** |
| `dotnet build shop133.slnx` | **Build succeeded. 0 Warning(s), 0 Error(s)** |
| `dotnet msbuild ... -getItem:Compile` | Los tres `.cs` de `Entities/` listados — el glob implícito los recogió sin tocar el `.csproj` |
| Suite de arquitectura (`-trait "Category=Fast"`) | **Total: 12, Errors: 0, Failed: 0** |
| Sonda de las entidades (52 comprobaciones) | **total: 52  ok: 52  fail: 0** (primera pasada: 48/4 — ver *Detalles*) |

**Que `dotnet build` pase no demuestra nada aquí**, y es el mismo problema de 1.1 y de 0.3: ningún proyecto usa todavía `Order` ni `OrderItem`, así que compilarían igual estando mal. Se repitió la técnica — un proyecto de consola desechable **en el scratchpad, fuera del repo**, con `ProjectReference` a `Orders.Domain`.

`dotnet test` sigue roto en esta máquina desde que el SDK pasó a 10.0.400 (ver la nota de la Fase 1 en [CLAUDE.md](../CLAUDE.md)), así que la suite se corrió como ejecutable:

```
tests\Shop133.ArchitectureTests\bin\Debug\net10.0\Shop133.ArchitectureTests.exe -trait "Category=Fast"

xUnit.net v3 In-Process Runner v4.0.0+8bf043c053 (64-bit .NET 10.0.11)
=== TEST EXECUTION SUMMARY ===
   Shop133.ArchitectureTests  Total: 12, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.219s
```

Salida completa de la sonda:

```
== Order: construcción y estado inicial ==
  PASS  Status es Pending  (Pending)
  PASS  Id no es Guid.Empty  (c12d98a0-c0ce-44b9-a1ee-6101b6ced770)
  PASS  dos pedidos tienen ids distintos
  PASS  CreatedAt en UTC (offset cero)  (00:00:00)
  PASS  CreatedAt reciente  (2026-08-21T00:03:38.0974440+00:00)
  PASS  Items conserva el orden y el número de líneas  (2)

== Total: calculado, no persistido ==
  PASS  Subtotal de la línea 1  (299.00)
  PASS  Subtotal de la línea 2  (89)
  PASS  Total == suma de subtotales  (388.00)
  PASS  Total con decimales  (99.99)
  PASS  Total NO es propiedad con setter (no se persiste)

== La colección no se puede tocar desde fuera ==
  PASS  copia defensiva: mutar la lista del llamante no altera el pedido  (llamante=0, pedido=2)
  PASS  Items no es un List<OrderItem> casteable  (ReadOnlyCollection`1)
  PASS  Add por ICollection<T> rechazado  (NotSupportedException)

== Order: guardas ==
  PASS  customerEmail null  (ArgumentNullException: ... (Parameter 'customerEmail') [paramName=customerEmail])
  PASS  customerEmail vacío  (ArgumentException: The value cannot be an empty string or composed entirely of whitespace. (Parameter 'customerEmail') [paramName=customerEmail])
  PASS  customerEmail en blanco  (ArgumentException: ... [paramName=customerEmail])
  PASS  customerEmail de 321 caracteres  (ArgumentOutOfRangeException: El valor supera el máximo de 320 caracteres. (Parameter 'customerEmail') [paramName=customerEmail])
  PASS  customerEmail de 320 caracteres aceptado
  PASS  customerEmail recortado
  PASS  customerEmail NO se pasa a minúsculas
  PASS  items null  (ArgumentNullException: Value cannot be null. (Parameter 'items') [paramName=items])
  PASS  items vacío  (ArgumentException: Un pedido necesita al menos una línea. (Parameter 'items') [paramName=items])
  PASS  una línea null dentro de items  (ArgumentException: Ninguna línea del pedido puede ser null. (Parameter 'items') [paramName=items])
  PASS  ProductId duplicado  (ArgumentException: El producto 1 aparece en más de una línea; agrupa las cantidades antes de crear el pedido. (Parameter 'items') [paramName=items])
  PASS  mismo Sku con ProductId distinto sí se acepta

== Order: sin vía de mutación del estado (las transiciones son 4.2/4.3) ==
  PASS  Order no expone métodos públicos todavía  ()
  PASS  Status no tiene setter público

== OrderItem: guardas ==
  PASS  productId 0  (ArgumentOutOfRangeException: productId ('0') must be a non-negative and non-zero value. [paramName=productId])
  PASS  productId negativo  (ArgumentOutOfRangeException: ... [paramName=productId])
  PASS  quantity 0  (ArgumentOutOfRangeException: quantity ('0') must be a non-negative and non-zero value. [paramName=quantity])
  PASS  quantity negativa  (ArgumentOutOfRangeException: ... [paramName=quantity])
  PASS  unitPrice 0  (ArgumentOutOfRangeException: unitPrice ('0') must be a non-negative and non-zero value. [paramName=unitPrice])
  PASS  unitPrice negativo  (ArgumentOutOfRangeException: ... [paramName=unitPrice])
  PASS  productSku null  (ArgumentNullException: ... [paramName=productSku])
  PASS  productSku vacío  (ArgumentException: ... [paramName=productSku])
  PASS  productSku de 51 caracteres  (ArgumentOutOfRangeException: El valor supera el máximo de 50 caracteres. [paramName=productSku])
  PASS  productName null  (ArgumentNullException: ... [paramName=productName])
  PASS  productName en blanco  (ArgumentException: ... [paramName=productName])
  PASS  productName de 201 caracteres  (ArgumentOutOfRangeException: El valor supera el máximo de 200 caracteres. [paramName=productName])
  PASS  longitudes exactas 50 / 200 aceptadas

== OrderItem: la foto copia, no corrige ==
  PASS  ProductSku recortado  ('taza-001')
  PASS  ProductSku NO se pasa a mayúsculas (al revés que Product.Sku)  ('taza-001')
  PASS  ProductName recortado  ('Taza Talavera')

== Superficie de los tipos (lo que verá EF Core en 2.2) ==
  Order  sealed=True
    Id             Guid                 setter público=False
    CustomerEmail  String               setter público=False
    Status         OrderStatus          setter público=False
    CreatedAt      DateTimeOffset       setter público=False
    Items          IReadOnlyList`1      setter público=False
    Total          Decimal              setter público=False
  PASS  Order: ningún setter público
  PASS  Order: existe ctor privado sin parámetros (EF)
  PASS  Order: EF materializa sin ejecutar guardas
  OrderItem  sealed=True
    ProductId      Int32                setter público=False
    ProductSku     String               setter público=False
    ProductName    String               setter público=False
    Quantity       Int32                setter público=False
    UnitPrice      Decimal              setter público=False
    Subtotal       Decimal              setter público=False
  PASS  OrderItem: ningún setter público
  PASS  OrderItem: existe ctor privado sin parámetros (EF)
  PASS  OrderItem: EF materializa sin ejecutar guardas
  PASS  Order materializado por EF tiene Items vacía, no null
  PASS  existe el campo de respaldo _items que EF usará en 2.2  (List`1)

total: 52  ok: 52  fail: 0
```

**La primera pasada dio 48/4** y los cuatro fallos están contados en *Detalles*: dos eran un agujero real de la entidad (`IReadOnlyList` casteable, corregido con `AsReadOnly()`) y dos eran aritmética mal hecha en la propia sonda. Se dejan escritos porque un documento que solo enseña la pasada buena no enseña nada.

El proyecto de consola **no se añadió al repo**. Lo sustituyen los tests de `2.4`.

---

## Pendiente

De la Fase 2 quedan 2.2, 2.3 y 2.4. Lo que este punto les deja abierto:

- **2.2** — `OrdersDbContext` contra `OrdersDb`, conectando como `orders_user`. Cinco cosas salen de aquí: el `enum` se persiste como `int` (ojo con la decisión 2), `Total` y `Subtotal` necesitan `Ignore()` o EF les crea columna, `CreatedAt` va a `datetimeoffset`, las longitudes se leen de las constantes y **hay que decidir cómo se mapea `OrderItem` sin `Id`** — clave sombra sobre entidad normal o `OwnsMany` (decisión 6). También habrá que confirmar que EF descubre `_items` por convención pese a que la propiedad devuelve un `ReadOnlyCollection`.
- **2.3** — el DTO de entrada con sus DataAnnotations (`[EmailAddress]`, `[MaxLength(Order.CustomerEmailMaxLength)]` leyendo la constante, nunca un literal), el mapeo de `ArgumentException`/`ArgumentOutOfRangeException` a `400` con el nombre del campo, y **agrupar las líneas repetidas antes de construir el `Order`** (decisión 11). Ahí vive también la deuda deliberada: la llamada `HttpClient` a Catalog.API que rellena los tres campos congelados de cada `OrderItem`, marcada `// PHASE-2 DEBT`.
- **3.3** — cuando esa llamada desaparezca, alguien tendrá que seguir rellenando `ProductSku`, `ProductName` y `UnitPrice`. Es la pregunta abierta en la nota de revisión de la decisión 6 de [fase_0_3.md](fase_0_3.md), y este punto no la responde: solo deja escrito que el pedido **no puede** vivir sin esos tres campos.
- **4.2 / 4.3** — `Confirm()` y `Cancel()` con sus guardas de transición (decisión 4), y si el pedido necesita más estados de los tres de aquí.
- **4.5** — cómo se indexa `Order.Id`, la pregunta que 1.1 dejó abierta al medir que un `Guid` v7 no ordena dentro de SQL Server (decisión 5).
