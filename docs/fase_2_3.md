# Fase 2.3 — `POST /orders` con llamada síncrona a Catalog.API

**Fecha:** 2026-08-24 · **Estado:** completado · **Roadmap:** [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md)

---

## Objetivo

`2.1` escribió las entidades y `2.2` les dio tablas, pero **`Orders.API` no exponía ni un endpoint** — la carpeta `Controllers/` existía vacía, no había `Models/`, y `OrdersDb` seguía con cero filas. Este punto es el que pone datos ahí.

Y es, sobre todo, el punto donde **entra la deuda deliberada de la regla 2 de CLAUDE.md**. Una línea de pedido congela cinco campos y tres de ellos —`ProductSku`, `ProductName`, `UnitPrice`— son de Catalog, que es su único dueño. Orders no puede leer `CatalogDb` (regla 1: `orders_user` no tiene permiso, y la comprobación no es de convención sino del motor). Así que los pide por HTTP, en caliente, antes de poder aceptar el pedido.

Eso significa que **si Catalog está caído, Orders no puede crear pedidos**. Ese acoplamiento no es un defecto de la implementación: es el entregable pedagógico de la fase. El roadmap lo dice sin rodeos — *"aquí sentirás el acoplamiento… este dolor es intencional"*. `2.4` lo hace reproducible con WireMock y `3.3` lo borra publicando `OrderCreated`.

Cierra además dos deudas que `2.1` y `2.2` dejaron nombradas por escrito:

> *(2.1)* el DTO de entrada con sus DataAnnotations (`[EmailAddress]`, `[MaxLength(Order.CustomerEmailMaxLength)]` **leyendo la constante, nunca un literal**), el mapeo de `ArgumentException` a `400` con el nombre del campo, y **agrupar las líneas repetidas antes de construir el `Order`**.

> *(2.2)* el agregado solo **afirma** la invariante de "sin `ProductId` repetido", no la arregla.

**Fuera de alcance deliberadamente:** los tests de `2.4` (WireMock, camino feliz y "Catalog caído ⇒ Orders falla"), el Dockerfile y el servicio de compose para Orders (no están en el roadmap; entran cuando la Fase 3 necesite a Orders hablando con RabbitMQ), la publicación de `OrderCreated` (`3.3`), la comprobación de stock (`3.4`, contra `InventoryDb`) y `Confirm()`/`Cancel()` (`4.2`/`4.3`).

**No se añadió ningún paquete NuGet y no se tocó ningún `.csproj`.** `HttpClient` y `System.Net.Http.Json` vienen en el shared framework, y `AddHttpClient` en `Microsoft.Extensions.Http`, que arrastra el SDK Web. La suite de arquitectura sigue en **12**.

---

## Decisiones

### 1. El cliente HTTP vive en `Orders.Infrastructure`, no junto al controller

`CatalogClient`, `CatalogProduct` y `CatalogUnavailableException` están en [`Orders.Infrastructure/Catalog/`](../src/Services/Orders/Orders.Infrastructure/Catalog/), una carpeta propia.

**Descartado — ponerlo en `Orders.API`, junto al controller que lo usa.** Sería el precedente de `1.3`, donde Catalog inyecta su `DbContext` directamente en el controller sin capa intermedia. Pero ahí la excepción se justificaba porque una capa más sería un *passthrough* sobre un CRUD; aquí sí hay lógica propia que no es HTTP de entrada: traducir tres desenlaces de una respuesta ajena a tres desenlaces de dominio.

**Elegido:** `.Infrastructure`, con dos razones que se refuerzan:

1. CLAUDE.md pone ahí todo lo que no es traducir la petición entrante. Hablar con un servicio externo es acceso a datos igual que el `DbContext`: el controller no debería saber si el precio viene de una tabla, de una llamada HTTP o de un evento — que es **exactamente lo que va a cambiar en la Fase 3**.
2. La deuda queda aislada en una carpeta que `3.3` puede borrar de una pieza. Al desaparecer, el `catch (CatalogUnavailableException)` del controller se queda sin tipo y el compilador señala lo que falta por quitar. Un `catch (HttpRequestException)` repartido por la capa API no daría esa señal.

### 2. Catalog caído devuelve **502**, no 503

**Descartado — 503 Service Unavailable.** Admite `Retry-After` y es el que un cliente con reintentos trata de forma natural. Pero miente sobre quién falló: Orders está perfectamente vivo, con su base de datos disponible y sus dos endpoints respondiendo.

**Elegido:** `502 Bad Gateway`. Dice lo que pasó — un servicio del que este depende no contestó — y eso es justo la lección de la fase. Un 503 dejaría al lector pensando que Orders se cayó; el 502 le hace preguntar *¿qué hay detrás de Orders?*, que es la pregunta correcta.

El cuerpo se construye con `Problem(...)` de `ControllerBase` y **no** con `StatusCode(502, new ProblemDetails { … })` como hace Catalog con su 409: `Problem` pasa por el `ProblemDetailsFactory`, que pone el content-type `application/problem+json` y añade el `traceId` — que es lo que la Fase 7 querrá para cruzar este fallo con la traza de Jaeger. Verificado en la comprobación 6.

El mensaje de la excepción **no** se copia al `detail` de la respuesta: puede llevar la URL interna del servicio. Va al log, que es de quien opera; la respuesta es de quien consume.

### 3. Un producto que no existe en Catalog es **400**, no 404

Mismo criterio, palabra por palabra, que el `categoryId` desconocido de `POST /products` (`1.3`): lo que no existe es un valor del **cuerpo**, no el recurso al que apunta la URL. Un 404 aquí diría que no existe el pedido, que es precisamente lo que se está intentando crear.

Sale como `ValidationProblemDetails` con una clave por línea, con la misma forma que genera la validación de MVC sobre una colección — `Items[0].ProductId` —, para que el cliente no tenga que distinguir dos formatos de error de entrada. Que la forma coincide se comprobó de verdad, no se supuso: ver las comprobaciones 4 y 5.

**Los desconocidos se acumulan todos y salen en un solo 400**, en vez de cortar en el primero. Cortar ahorraría llamadas HTTP, pero obligaría al cliente a arreglar el cuerpo de uno en uno — exactamente lo que las DataAnnotations evitan al reportar todos los campos malos a la vez. El índice que se reporta es el de la **primera aparición** en el cuerpo original, porque la agrupación ya juntó las líneas repetidas.

### 4. El cuerpo lleva solo `productId` + `quantity`: Catalog es autoritativo en precios

**Descartado — que el cliente declare el `unitPrice` que vio en la ficha y devolver 409 si Catalog dice otro.** Es un escenario real (*el precio cambió mientras comprabas*) y enseña inconsistencia temporal, que está en la checklist de "señales de que estás aprendiendo de verdad". Pero mete un segundo número que puede desincronizarse y una rama de error más en el punto que ya introduce el acoplamiento. Si la Fase 6 lo quiere enseñar desde el carrito, se retoma con el caso de uso delante.

**Elegido:** el cliente dice *qué* y *cuánto*; el *cuánto cuesta* lo pone la única fuente que lo conoce. Eso es lo que significa "validar productos/precios" en el roadmap: no comparar contra un número que mandó el cliente, sino ir a buscarlo. Un cliente no puede inventarse un precio ni por error ni a propósito.

Tampoco se pide el `sku`: dos identificadores del mismo producto en el mismo cuerpo obligarían a decidir cuál gana si no coinciden.

### 5. Una petición por línea, en secuencia

**Descartado — un solo `GET /products` y filtrar en cliente.** Traería el catálogo entero (las 50 filas del seed de `1.4`) para usar dos, y escondería el coste real detrás de una sola llamada.

**Descartado — paralelizar con `Task.WhenAll`.** Iría más rápido y haría el acoplamiento **menos visible**, que es lo contrario de lo que este punto existe para enseñar.

**Elegido:** N líneas distintas ⇒ N idas y vueltas, una detrás de otra. Catalog no tiene endpoint *batch*, y eso también es parte de la lección: nadie diseñó este patrón de consumo, salió de que Orders necesita datos que no son suyos.

Es lo que justifica el `[MaxLength(50)]` de `Items`: el tamaño del cuerpo es, literalmente, el coste del acoplamiento. En la Fase 3 ese número deja de importar, porque publicar `OrderCreated` cuesta lo mismo con 1 línea que con 200.

### 6. La agrupación va **antes** de llamar a Catalog

Podría ir después —el constructor de `Order` es quien la exige— pero agrupar primero convierte dos líneas del mismo producto en **una sola petición HTTP** en vez de dos. Con la agrupación al final, el cuerpo del cliente decidiría cuántas veces se llama a Catalog.

`GroupBy` de LINQ to Objects preserva el orden de aparición de los grupos, así que el pedido resultante respeta el orden en que el cliente escribió las líneas. Comprobado en la verificación 3: `[2, 5, 2]` sale como `[2, 5]`.

### 7. Se añade `GET /orders/{id:guid}`, que el roadmap situaba en `6.5`

`CreatedAtAction` necesita una acción destino para construir la cabecera `Location` del 201.

**Descartado — `Created($"/orders/{id}", …)` con la ruta escrita a mano.** Habría respetado el alcance del punto al pie de la letra, al precio de publicar un `Location` que devuelve 404 y de duplicar la ruta en una cadena que nadie revisa cuando el `[Route]` cambia.

**Elegido:** la acción mínima — 200 con el pedido, 404 si no existe. Sin listado, sin paginación y sin filtros; `6.5` la amplía con lo que necesite la página de estado. Es además lo que `2.4` va a necesitar para comprobar que el pedido quedó realmente escrito.

No lleva `Include`: las líneas son un tipo *owned* (decisión 1 de [fase_2_2.md](fase_2_2.md)), así que EF las trae siempre con el pedido. Es el modo de fallo silencioso que aquel punto se quitó de encima, cobrado aquí.

### 8. `Status` sale como texto desde el DTO, no con un converter global

`OrderResponse.Status` es un `string` que rellena `From(...)` con `order.Status.ToString()`.

**Descartado — registrar un `JsonStringEnumConverter` global en `Program.cs`.** Cambiaría la serialización de todo el servicio por un solo campo, y de forma invisible desde el tipo afectado.

**Descartado — publicar el número.** Un cliente que lea `1` se acopla al orden del enum, y ese orden es un detalle de persistencia: `2.1` fijó valores explícitos justamente para poder añadir estados al final sin renumerar filas ya escritas.

El DTO ya es el sitio donde se traduce entidad → JSON. La conversión vive donde vive el resto del mapeo.

### 9. El endpoint **no** comprueba el stock

`CatalogProduct` ni siquiera copia el campo `stock` que Catalog devuelve. El `Stock` del catálogo es el número que el catálogo **muestra**; el reservable pertenece a `InventoryDb` desde `3.4` y lo reserva la saga. Restar aquí crearía un segundo número llevando la cuenta de lo mismo — el error que `2.1` ya evitó al hacer `Total` y `Subtotal` calculados.

Consecuencia aceptada y escrita en el `[EndpointDescription]`: en la Fase 2 se puede pedir un producto agotado. El rechazo por falta de stock es `StockRejected`, y llega en la Fase 3.

### 10. `CatalogProduct` copia 4 de los 9 campos que manda Catalog

Solo `Id`, `Sku`, `Name` y `Price` — los tres que se congelan más el puntero. `System.Text.Json` ignora las propiedades sobrantes del JSON por defecto, así que recortar no cuesta nada, y copiar `description`, `stock`, `imageUrl` o `categoryName` sería declarar una dependencia sobre campos que Orders no usa: el día que Catalog renombre uno, Orders se enteraría sin motivo.

**No se importa `Catalog.API.Models.ProductResponse`**, y no por gusto: `ServiceProjects_DoNotReference_OtherServices` lo prohíbe. Un servicio que consume a otro por HTTP declara su propia vista del contrato. Que los dos tipos puedan desincronizarse *es* la información — significa que el contrato cambió, y esa es la fricción que la Fase 3 sustituye por un tipo compartido de verdad (`OrderLine`) que verifica el compilador.

Nótese la pila de copias: `Product` → `ProductResponse` → `CatalogProduct` → `OrderItem`. Cuatro representaciones del mismo dato. Ese coste es visible a propósito.

### 11. `GetAsync` + comprobar el `StatusCode`, no `GetFromJsonAsync`

`GetFromJsonAsync` lanza ante un 404, y eso perdería la distinción que sostiene todo el manejo de errores de este punto: un producto inexistente es una respuesta **válida** de Catalog (`null` → 400), mientras que un 500 o una conexión rechazada son indisponibilidad (→ 502). Confundirlos haría que un id mal escrito en el cuerpo pareciera una caída de infraestructura.

Los miembros de `CatalogProduct` son `required`, así que un JSON al que le falte un campo lanza `JsonException` en vez de dejar pasar un precio en cero. Esa excepción también se traduce a `CatalogUnavailableException`: quien incumplió el contrato fue el otro servicio, no Orders.

### 12. Timeout de 5 segundos, y ni un reintento

`HttpClient` trae **100 segundos** por defecto. Con ese valor, "Catalog caído" tardaría minuto y medio *por línea* en devolver el 502: el fallo parecería un cuelgue y el test de `2.4` sería inviable.

**Sin Polly, sin reintentos y sin circuit breaker**, y es deliberado: aquí amortiguarían justo el dolor que el punto quiere enseñar. Polly entra en `6.6`, del lado del Frontend, donde el problema que resuelve sí es el suyo.

---

## Cambios

### Nuevos

| Archivo | Rol |
|---|---|
| [`Orders.Infrastructure/Catalog/CatalogClient.cs`](../src/Services/Orders/Orders.Infrastructure/Catalog/CatalogClient.cs) | El *typed client*. Un método, tres desenlaces: producto, `null` (404) o `CatalogUnavailableException`. **`// PHASE-2 DEBT`**. |
| [`Orders.Infrastructure/Catalog/CatalogProduct.cs`](../src/Services/Orders/Orders.Infrastructure/Catalog/CatalogProduct.cs) | La vista de Orders sobre el JSON de Catalog: 4 campos. **`// PHASE-2 DEBT`**. |
| [`Orders.Infrastructure/Catalog/CatalogUnavailableException.cs`](../src/Services/Orders/Orders.Infrastructure/Catalog/CatalogUnavailableException.cs) | Evita que `HttpRequestException`/`TaskCanceledException`/`JsonException` se filtren a la capa API. **`// PHASE-2 DEBT`**. |
| [`Orders.API/Controllers/OrdersController.cs`](../src/Services/Orders/Orders.API/Controllers/OrdersController.cs) | `POST /orders` (201/400/502) y `GET /orders/{id:guid}` (200/404). |
| [`Orders.API/Models/CreateOrderRequest.cs`](../src/Services/Orders/Orders.API/Models/CreateOrderRequest.cs) | Cuerpo del alta. Aquí vive el `[EmailAddress]` que `2.1` dejó fuera de la entidad. |
| [`Orders.API/Models/CreateOrderItemRequest.cs`](../src/Services/Orders/Orders.API/Models/CreateOrderItemRequest.cs) | `productId` + `quantity`, y nada más. |
| [`Orders.API/Models/OrderResponse.cs`](../src/Services/Orders/Orders.API/Models/OrderResponse.cs) | Salida del 201 y del `GET`, con `From(Order)`. |
| [`Orders.API/Models/OrderItemResponse.cs`](../src/Services/Orders/Orders.API/Models/OrderItemResponse.cs) | Una línea, con el `Subtotal` calculado ya resuelto. |

### Modificados

| Archivo | Cambio |
|---|---|
| [`Orders.API/Program.cs`](../src/Services/Orders/Orders.API/Program.cs) | `LowercaseUrls`; `AddDocumentTransformer` con el `OpenApiInfo`; guarda de `Services:CatalogBaseUrl` + `AddHttpClient<CatalogClient>` con `BaseAddress` y `Timeout`; `public partial class Program { }` al pie. |
| [`Orders.API/appsettings.json`](../src/Services/Orders/Orders.API/appsettings.json) | `Services:CatalogBaseUrl` = `http://localhost:5124`. |
| [`Orders.API/Orders.API.http`](../src/Services/Orders/Orders.API/Orders.API.http) | Peticiones de ejemplo para los cinco escenarios. |

**Ningún `.csproj` tocado. Ningún paquete NuGet añadido.**

---

## Detalles que cuestan tiempo

### La dirección de Catalog va en `appsettings.json`, no en User Secrets

El connection string vive en User Secrets porque lleva una contraseña. Una URL no es un secreto: meterla ahí la escondería de quien lee el repositorio y obligaría a configurarla a mano en cada máquina. Se sobreescribe con `Services__CatalogBaseUrl` cuando Orders tenga contenedor (allí sería `http://catalog-api:8080`).

La guarda existe por el mismo motivo que la del connection string: sin ella, `new Uri(null)` revienta con un mensaje que no dice qué falta.

### `BaseAddress` **tiene** que acabar en `/`, y la ruta relativa **no** puede empezar por `/`

`Uri` combina base + relativa descartando el último segmento de la base si no acaba en barra, y trata una relativa que empiece por `/` como absoluta desde la raíz del host. Con la base en la raíz —como hoy— ninguno de los dos errores se nota. El día que Catalog viva detrás del Gateway en `/api/catalog/` (Fase 5), los dos harían desaparecer el prefijo sin ningún mensaje de error. Por eso el registro normaliza con `TrimEnd('/') + '/'` y la petición pide `products/{id}` sin barra inicial.

### Un timeout y una cancelación del cliente llegan como la misma excepción

`HttpClient` señala el agotamiento de su `Timeout` con `TaskCanceledException`, que es exactamente lo que lanza también un `CancellationToken` cancelado porque el cliente cerró la pestaña. Lo que los separa es el filtro:

```csharp
catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
```

Si el token del *request* no está cancelado, nadie pidió parar: fue el timeout. Sin ese filtro, cerrar el navegador a mitad de un `POST` se registraría como "Catalog no disponible", que es mentira, y ensuciaría los logs con caídas que no existieron.

### Una conexión rechazada en `localhost` **no** es instantánea: tardó 4,13 s

Medido en la comprobación 6. Con Catalog parado y el timeout puesto en 5 s, el 502 llegó en 4,13 s — no en milisegundos, como sugiere la idea de que un puerto cerrado responde `ECONNREFUSED` al momento. En el log aparecen **dos** `SocketException (10061)` para una sola petición: `localhost` resuelve a `::1` y a `127.0.0.1`, y `HttpClient` prueba las dos.

Importa para `2.4`: un test que afirme "Catalog caído ⇒ falla rápido" no puede poner el umbral en milisegundos. Y con el timeout de fábrica esa espera se multiplicaría por línea del pedido.

### Las claves de `ValidationProblemDetails` salen en PascalCase, no camelCase

El resto del JSON del servicio sale camelCase (`"customerEmail"`, `"productSku"`), pero las claves del diccionario `errors` conservan el nombre del modelo: `Items[0].ProductId`, `CustomerEmail`. No es una inconsistencia que haya que arreglar — es lo que emite la validación de MVC, y el objetivo era precisamente que el error añadido a mano y el de las DataAnnotations tuvieran la misma forma. Comprobado poniendo los dos a fallar en el mismo cuerpo (comprobación 5).

### El `IDENTITY` invisible de `OrderItems` tampoco empieza en 1

Las tres primeras líneas escritas en la tabla se llevaron los ids **5, 6 y 7** (comprobación 8), con la tabla vacía antes. Es la misma cesión de bloques de `IDENTITY` que CLAUDE.md ya documenta para `Product.Id`, aplicada a la columna sombra que EF inventa para la clave compuesta del tipo *owned* (`2.2`, decisión 2). No importa —esa columna no existe en C# y nadie la referencia— pero conviene no sorprenderse al mirar la tabla.

### El `.http` de la plantilla usa puntos en los nombres de variable y el linter se queja

`@Orders.API_HostAddress` es lo que genera Visual Studio, y el analizador de `.http` intenta resolver `Orders` como un objeto de entorno con un miembro `.API_HostAddress`. Renombradas a `OrdersApi_HostAddress` / `CatalogApi_HostAddress`. Cosmético; no afecta a nada compilado.

---

## Verificación

### 1. Build limpio

```powershell
dotnet build
```
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### 2. Camino feliz

Con Catalog en `5124` y Orders en `5189`, ambos con `dotnet run --launch-profile http`:

```powershell
$body = '{"customerEmail":"cliente@example.com","items":[{"productId":1,"quantity":2}]}'
Invoke-WebRequest -Uri "http://localhost:5189/orders" -Method Post -Body $body -ContentType "application/json" -UseBasicParsing
```
```
STATUS: 201
LOCATION: http://localhost:5189/orders/99d013a2-3768-432e-bf6c-b31f58243d23
{"id":"99d013a2-3768-432e-bf6c-b31f58243d23","customerEmail":"cliente@example.com",
 "status":"Pending","createdAt":"2026-08-25T00:02:36.4861602+00:00","total":498.00,
 "items":[{"productId":1,"productSku":"TAZA-001","productName":"Taza Talavera Puebla",
           "quantity":2,"unitPrice":249.00,"subtotal":498.00}]}
```

El `Location` sale en minúsculas (`/orders/…`), el `status` como texto, y el `total` es 249,00 × 2 — el precio lo puso Catalog, no el cuerpo.

### 3. Líneas repetidas: se agrupan sumando

Cuerpo con `[productId 2 ×2, productId 5 ×1, productId 2 ×3]`:

```
STATUS: 201
{"id":"3aa217ab-…","total":1434.00,"items":[
  {"productId":2,"productSku":"TAZA-002","productName":"Taza Calavera Catrina","quantity":5,"unitPrice":229.00,"subtotal":1145.00},
  {"productId":5,"productSku":"TAZA-005","productName":"Taza Barro Negro","quantity":1,"unitPrice":289.00,"subtotal":289.00}]}
```

Tres líneas de entrada, **dos** de salida, cantidad 2+3 = 5, y el orden de aparición respetado. El constructor de `Order` no llegó a ver el duplicado.

### 4. Productos inexistentes: un solo 400 con todos

Cuerpo con `[999999, 3, 888888]`:

```
STATUS: 400
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1",
 "title":"One or more validation errors occurred.","status":400,
 "errors":{"Items[0].ProductId":["No existe el producto 999999 en el catálogo."],
           "Items[2].ProductId":["No existe el producto 888888 en el catálogo."]},
 "traceId":"00-caff58764b4abf570d1bf63ce20d372b-2ed9a799b50e2408-00"}
```

Los dos desconocidos en una sola respuesta, con los índices del cuerpo **original** (0 y 2), no los de la lista agrupada.

### 5. Validación del cuerpo, y la forma de las claves

```
{"errors":{"CustomerEmail":["The CustomerEmail field is not a valid e-mail address."],
           "Items[0].Quantity":["The field Quantity must be between 1 and 10000."]}}

{"errors":{"Items":["The field Items must be a string or array type with a minimum length of '1'."]}}
```

Aquí está la comprobación que importa: MVC genera `Items[0].Quantity` **por su cuenta**, que es exactamente la forma que usa el error añadido a mano en la comprobación 4. La suposición estaba verificada, no asumida.

El `[MinLength(1)]` devuelve el 400 antes de que el constructor de `Order` lance su *"un pedido necesita al menos una línea"*: la misma invariante, comprobada dos veces a propósito.

### 6. Catalog caído ⇒ 502, y el pedido no se crea

```powershell
Stop-Process -Id (Get-NetTCPConnection -LocalPort 5124 -State Listen).OwningProcess -Force
```
```
STATUS: 502
TIEMPO: 4.13 s
CONTENT-TYPE: application/problem+json; charset=utf-8
{"type":"https://tools.ietf.org/html/rfc9110#section-15.6.3",
 "title":"Catalog no disponible","status":502,
 "detail":"No se pudieron consultar los productos en Catalog.API, así que el pedido no se ha creado…",
 "traceId":"00-9ea73c1ff5a493468d035311fb6d81e1-23fd1a1a1b1853b8-00"}
```

Y en el log de Orders, la cadena completa:

```
fail: Orders.API.Controllers.OrdersController[0]
      No se pudo validar el pedido contra Catalog.API.
      Orders.Infrastructure.Catalog.CatalogUnavailableException: No se pudo contactar con
        Catalog.API para consultar el producto 1.
       ---> System.Net.Http.HttpRequestException: No connection could be made because the
            target machine actively refused it. (localhost:5124)
       ---> System.Net.Sockets.SocketException (10061)
```

Se recorrió la rama de `HttpRequestException`, no la del timeout. **La rama del timeout no se llegó a medir**: forzarla necesita un Catalog que acepte la conexión y no conteste, que es justo lo que WireMock hará en `2.4`.

### 7. Recuperación

Con Catalog levantado de nuevo, el mismo `POST` devuelve `201` sin reiniciar Orders — el pool de conexiones no se queda envenenado.

### 8. Las filas reales, consultadas con `orders_user`

```powershell
docker exec shop133-sqlserver /opt/mssql-tools18/bin/sqlcmd `
  -S localhost -U orders_user -P "<ORDERS_DB_PASSWORD>" -C -d OrdersDb -W -s "|" -Q "..."
```
```
Id                                   |CustomerEmail      |Status|CreatedAt
3AA217AB-3F46-433D-9773-56670CA64037 |cliente@example.com|1     |2026-08-25 00:02:47.4614028 +00:00
99D013A2-3768-432E-BF6C-B31F58243D23 |cliente@example.com|1     |2026-08-25 00:02:36.4861602 +00:00

OrderId                              |Id|ProductId|ProductSku|ProductName            |Quantity|UnitPrice
3AA217AB-3F46-433D-9773-56670CA64037 | 6|        2|TAZA-002  |Taza Calavera Catrina  |       5|229.00
3AA217AB-3F46-433D-9773-56670CA64037 | 7|        5|TAZA-005  |Taza Barro Negro       |       1|289.00
99D013A2-3768-432E-BF6C-B31F58243D23 | 5|        1|TAZA-001  |Taza Talavera Puebla   |       2|249.00
```

**2 pedidos y 3 líneas**, exactamente los dos `201` de las comprobaciones 2 y 3. Los dos `400` y el `502` no escribieron nada. `Status = 1` es el `Pending` explícito de `2.1`, y los tres campos congelados están en la tabla: el pedido sabe qué se compró aunque Catalog borre el producto mañana.

### 9. Lectura y 404

```
GET /orders/3aa217ab-…                              → 200, con sus dos líneas (sin Include)
GET /orders/00000000-0000-0000-0000-000000000000    → 404
GET /orders/no-es-un-guid                           → 404  (lo rechaza la restricción :guid de la ruta)
```

### 10. El documento OpenAPI

```
TITLE:   shop133 — Orders API
VERSION: v1
PATHS:   /orders, /orders/{id}
```

Ya no dice `Orders.API | v1`, y las rutas salen en minúsculas.

### 11. La suite de arquitectura sigue en 12

```
Shop133.ArchitectureTests  Total: 12, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 1.323s
```

Nada que añadir a `.csproj` significa nada que romper. La regla que **sí** estaba en juego —`ServiceProjects_DoNotReference_OtherServices`— es la que obligó a `CatalogProduct` a existir en vez de importar `ProductResponse`.

---

## Pendiente

- **`2.4`** — `Orders.Tests` con WireMock.Net: camino feliz y "Catalog caído ⇒ Orders falla", los dos marcados `// PHASE-2 DEBT` y borrados en `3.7`. Ya tiene puesto lo que necesita: el `public partial class Program { }` del pie de `Program.cs` y el `GET /orders/{id}` con el que comprobar que el pedido quedó escrito. Es también donde se medirá la rama del **timeout**, que esta verificación no pudo forzar.
- **`3.3`** — borra `Orders.Infrastructure/Catalog/` entera, su registro en `Program.cs` y la clave `Services:CatalogBaseUrl`. El `catch (CatalogUnavailableException)` del controller se quedará sin tipo, que es la señal de por dónde seguir. Queda abierta la pregunta que arrastra la nota de revisión de la decisión 6 de [fase_0_3.md](fase_0_3.md): **si la llamada síncrona desaparece, quién rellena los tres campos congelados de cada línea**.
- **`3.4`** — el stock. Hoy se puede pedir un producto agotado; el rechazo llega como `StockRejected`.
- **`6.5`** — amplía `GET /orders/{id}` con lo que pida la página de estado del pedido. Si necesita listar u ordenar por fecha, es el momento de releer la decisión 5 de [fase_2_2.md](fase_2_2.md): `OrdersDb` no tiene ningún índice más allá de las dos claves primarias.
- **Contenedor de Orders** — sin numerar en el roadmap; entra cuando la Fase 3 necesite a Orders hablando con RabbitMQ. Traerá consigo la guarda de `UseHttpsRedirection()` con `IsDevelopment()` y probablemente desproteger `MapOpenApi()`, por los mismos motivos que `1.6` en Catalog.
