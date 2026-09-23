# Fase 6.3 — Carrito de compras en sesión de servidor

**Fecha:** 2026-09-11 · **Estado:** completado · **Roadmap:** [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md) → punto **6.3**

---

## Objetivo

Dar a la tienda un carrito que viva **en la sesión del servidor** y no en una cookie, y con él
activar las dos cosas que `6.1` y `6.2` dejaron a medias a propósito: el item *Carrito* del navbar,
que estaba sin `href`, y el botón *"Añadir al carrito"* de la ficha de detalle, que estaba
`disabled` con la nota *"El carrito llega en 6.3"*.

**El título dice "en sesión de servidor, no en cookie" y ese "no" es lo que hay que entender.** El
roadmap lo dejó por escrito: desde `3.3` el cuerpo de `POST /orders` lleva el precio de cada línea,
así que **quien guarda el carrito es quien acuña la foto del pedido**. En sesión de servidor la
acuña `Shop133.Web` leyendo Catalog por el Gateway —lo que la regla 3 permite—; en cookie la
acuñaría el navegador, y el cliente volvería a dictar el importe con `4.8` como única defensa. Es la
tercera capa de la decisión 2b de [fase_3_3.md](fase_3_3.md): **`6.3` y `8.1` deciden *quién* puede
mandar la foto, `4.8` decide si la foto es cierta. Hacen falta las dos.**

**Fuera de alcance, y cada cosa tiene dueño:** el `POST /orders` y el formulario de checkout
(`6.4`), la página de estado (`6.5`), Polly (`6.6`) y los toasts (`6.7`). **No se toca ningún
servicio, ni el Gateway, ni un contrato, ni una migración** — todo el punto cabe en
`src/Frontend/Shop133.Web/`.

Tampoco se comprueba el stock al añadir, y no es un olvido: el `stock` que publica Catalog es el que
el catálogo **muestra**, el reservable vive en `InventoryDb` desde `3.4` y nadie sincroniza las dos
columnas. Bloquear aquí por ese número daría una garantía falsa, que es exactamente lo contrario de
lo que dice el párrafo que `6.2` puso en la ficha.

---

## Decisiones

### 1. Sesión de servidor con `AddDistributedMemoryCache()`, y "distributed" es un nombre engañoso

La cookie lleva **un identificador opaco y nada más**; los datos —el `UnitPrice` incluido— viven en
el `IDistributedCache` de este proceso. Eso es lo que hace del carrito el sitio donde se acuña la
foto en vez del sitio donde el navegador la guarda.

*Descartado* un `IDistributedCache` de verdad (SQL Server o Redis): exige un paquete —y
`Shop133.Web.csproj` sigue con **cero `PackageReference` y cero `ProjectReference`**, propiedad que
`6.2` dejó documentada y que solo `6.6` puede romper— y una base de datos que este proyecto no posee
(sería la sexta, y la regla 1 dice que cada base tiene exactamente un dueño).

**La limitación se dice en voz alta en lugar de esconderse: el carrito muere con el proceso y no se
comparte entre réplicas.** Está medido abajo, no afirmado.

### 2. `Add` recibe SOLO `productId` y `quantity`

Es el núcleo del punto, y se ve en el HTML servido: el `<form>` de la ficha lleva un `productId`
oculto, un `<input type="number">` de cantidad y el token antiforgery. **Ni un precio.** El sku, el
nombre y el importe los relee `CartController.Add` de Catalog por el Gateway
(`CatalogClient.FindProductOrNullAsync`, que ya existía desde `6.2`) y los congela en la sesión.

Mandar el precio en un `<input type="hidden">` habría sido más cómodo y habría ahorrado una llamada
al Gateway por cada "añadir"... y habría devuelto al navegador la autoridad sobre el importe del
pedido, que es justo lo que este punto existe para quitarle.

**El coste, dicho y no escondido:** *una* llamada al Gateway por cada "añadir", contra el cupo
`catalog-read` de 60/60 s de `5.2`, y con todos los visitantes contando como una sola IP porque este
MVC renderiza en servidor. Es la misma contabilidad que `6.2` midió, con un sumando más.

### 3. El carrito es una FOTO: `/cart` no relee ningún precio

Cero llamadas al Gateway por render. *Descartado* revalidar línea a línea al pintar el carrito: sería
una petición **por línea y por render** contra ese mismo cupo, y además cambiaría el precio bajo los
pies de quien está comprando, que es lo contrario de lo que un carrito significa.

**La limitación que eso deja, y tiene dueño:** la ventana de autenticidad de `4.8` son 30 minutos
(`PricingSnapshotWindowMinutes`), así que una línea añadida hace más de media hora será rechazada por
Catalog y **el pedido se cancelará solo** en `6.4`/`6.5`, devolviendo el stock reservado. Eso no es un
defecto que tapar: es exactamente lo que esos dos puntos existen para enseñar, y por eso la página
del carrito lo dice con un párrafo propio, igual que `6.2` hizo con el stock.

### 4. `IdleTimeout` explícito a 20 minutos, y **no** acota la caducidad de la foto

Se escribe aunque coincida con el valor de fábrica, para que la relación con los 30 minutos de `4.8`
quede a la vista de quien lea el archivo. **Y para que quede a la vista también lo que NO hace: cada
petición reinicia ese contador**, así que un carrito en uso puede sostener una línea añadida hace
horas. El timeout protege la memoria del proceso, no la frescura del precio.

`Cookie.IsEssential = true` para que la cookie no desaparezca el día que alguien añada una política
de consentimiento —se vaciaría el carrito solo, sin un error en ninguna parte—, y `HttpOnly` escrito
explícitamente porque esta cookie es justo lo que el título dice que no debe llevar el carrito.

### 5. Añadir dos veces el mismo producto SUMA, y conserva el precio de la primera vez

Sumar es lo que ya hace `OrdersController` antes de construir el `Order`, y no es una comodidad: el
constructor de `Order` **prohíbe** dos líneas con el mismo `ProductId` desde `2.1`, porque esas líneas
viajan dentro de `ReserveStock` y un Inventory que recibe dos entradas del mismo producto tendría que
adivinar si reserva la suma. Un carrito que las dejara separadas produciría un `400` en `6.4`.

Lo que parece discutible y es deliberado: **al sumar se conserva el precio de la línea que ya
estaba**, no el de la foto recién leída. El precio de una línea se congela la primera vez; si Catalog
lo cambió entre el primer "añadir" y el segundo, quien decide si esa foto sigue valiendo es `4.8`, no
este método por su cuenta.

### 6. Las invariantes de la API se duplican a mano, no se importan

Máximo **50 líneas distintas** (copia de `[MaxLength(50)]` sobre `CreateOrderRequest.Items`) y
**1..10.000 unidades por línea** (copia de `[Range(1, 10_000)]` sobre
`CreateOrderItemRequest.Quantity`). Importarlas exigiría un `ProjectReference` a `Orders.API`: la
regla 3 rota de frente, con `Frontend_DoesNotReference_ServicesOrGateway` en rojo.

Es el precedente literal de `OrderItem.ProductSkuMaxLength`, que repite el número de
`Product.SkuMaxLength` por la regla hermana entre servicios — y como allí, **pueden divergir**: un
carrito solo tiene que producir un cuerpo que la API de hoy acepte.

El motivo de sostenerlas *aquí* y no dejar que la API las rechace en `6.4`: un carrito que puede
construir un cuerpo inválido se lo cuenta al usuario al tramitar el pedido, tres pantallas después de
donde se equivocó. Y superar un tope **no lanza**: devuelve un motivo legible, misma forma que
`StockItem.CanReserve` (`3.4`).

### 7. `Subtotal` y `Total` son calculados, con `[JsonIgnore]`

Precedente `Order.Total` / `OrderItem.Subtotal` (`2.1`): un solo origen de verdad. El `[JsonIgnore]`
no es adorno — sin él, `System.Text.Json` **escribe** esas propiedades en la sesión (son getters
públicos) y no puede leerlas de vuelta, dejando en el almacén un número que ya nadie actualiza y que
puede contradecir a sus propias líneas. Un dato que solo se escribe es peor que no tenerlo.

### 8. Todo POST, todo antiforgery, todo redirect — y el atributo va en la clase

`[AutoValidateAntiforgeryToken]` sobre `CartController` y no un `[ValidateAntiForgeryToken]` por
acción: el segundo es una lista que hay que acordarse de ampliar, y una acción nueva que se olvide de
él queda desprotegida **sin un solo aviso**. El automático protege todo lo que no sea GET/HEAD/
OPTIONS/TRACE, así que la protección es lo que pasa por defecto y desactivarla es lo que exige
escribir algo. Mismo criterio con el que `5.2` añadió un `GlobalLimiter` que el título no pedía: una
red de seguridad contra un fallo **silencioso**.

POST-Redirect-GET porque un GET que muta es cacheable y precargable —un navegador que precarga
enlaces vaciaría el carrito solo— y porque sin el redirect un F5 vuelve a añadir. Los avisos cruzan el
302 en `TempData`, con las dos claves en `CartNotice` para que **6.7 solo tenga que cambiar cómo se
pintan**, no de dónde salen.

### 9. Añadir al carrito SOLO desde la ficha de detalle

La card del grid usa `stretched-link`, y el comentario que `6.2` dejó en `Index.cshtml` avisa de que
un segundo elemento interactivo dentro de la card lo rompe *y de que el síntoma es que el botón deja
de responder*. `6.2` ya había escrito que la ficha "le da sitio natural al *Añadir al carrito* de
`6.3`". **`Views/Catalog/Index.cshtml` no se toca en este punto.**

### 10. El badge del navbar es un view component

*Descartado* leer `Context.Session` desde `_Layout.cshtml`: metería deserialización de JSON dentro de
un `.cshtml` y duplicaría la clave de sesión fuera de `CartStore`, que existe justamente para ser el
único sitio que la conoce. *Descartado* un filtro o un controller base que dejara el número en
`ViewData`: el layout lo pintan **todas** las vistas, así que un controller que se olvidara dejaría el
contador a cero con tres cosas en el carrito, **sin un error en ninguna parte**. Las dos alternativas
fallan en silencio; un view component trae su dependencia por DI y revienta nombrándose si falta.

Cuenta **unidades y no líneas**: quien lleva tres tazas iguales espera ver un 3. Con el carrito vacío
no pinta nada, ni un cero.

### 11. `Unavailable.cshtml` se mueve a `Views/Shared/`

`CartController.Add` también habla con el Gateway, así que también puede quedarse sin dependencia, y
la búsqueda de vistas de Razor cae en `Views/Shared/` desde cualquier controller. **Lo que no se
mueve es el código de estado**: lo sigue poniendo cada controller antes de devolver la vista, porque
`503` y `429` son lo único que hace estas ramas comprobables desde la línea de comandos — decisión 3
de `6.2`, que no se revierte.

Solo cambia el `<h1>`, que dejó de nombrar el catálogo. El cuerpo no hizo falta tocarlo: ya hablaba
del Gateway y de la regla 3, que es verdad desde las dos páginas. Y el botón *Reintentar* sigue
siendo `asp-action="Index"` **sin** `asp-controller`, así que resuelve contra el controller actual —
y ahí aparece una propiedad que merece leerse: desde el carrito, ese reintento funciona **incluso con
el Gateway caído**, porque `/cart` no lo llama.

El `Unavailable(...)` privado del controller sí queda duplicado, y se queda así: son dos ocurrencias,
y en este proyecto *dos copias no son un patrón* (precedente de `2.4`, que esperó a tener cuatro
copias de `SqlServerContainerFixture` antes de extraerla).

### 12. `ShoppingCart` y no `Cart`, y `CartNotice` aparte

Un tipo `Cart` dentro de un namespace acabado en `.Cart` convierte cada `@model Cart` en una
ambigüedad entre el tipo y el namespace. `CartLine`, `CartStore` y `CartController` no colisionan.

Las dos claves de `TempData` viven en `Cart/CartNotice.cs` y no como constantes de `CartController`
porque `_ViewImports.cshtml` ya importa `Shop133.Web.Cart`, e importar `Shop133.Web.Controllers` solo
para leer dos cadenas dejaría **todos** los controllers disponibles como `@model` de cualquier vista
para siempre — que es exactamente la cesión que `6.2` anotó al importar `Shop133.Web.Gateway`, pero
esta sí tenía alternativa.

### 13. `CartStore` es scoped y es el único sitio que toca `ISession`

Esa exclusividad es la razón de que exista: "en sesión de servidor y no en cookie" es una propiedad
que se sostiene mientras solo haya **un archivo** capaz de romperla. Scoped y con caché durante la
petición para que el controller y el view component vean la **misma instancia** — con un transient,
el badge pintaría el carrito de antes de la mutación.

*Descartado* un método de extensión estático sobre `ISession`: no puede cachear nada y repartiría la
clave y las opciones de JSON por los archivos que lo llamen.

---

## Cambios

**No entra ningún paquete.** `Microsoft.AspNetCore.Session.dll` y
`Microsoft.Extensions.Caching.Memory.dll` están en el ref pack del framework compartido (comprobado en
`Microsoft.AspNetCore.App.Ref/10.0.12`), así que **`Shop133.Web.csproj` no se toca** y sigue con cero
`PackageReference` y cero `ProjectReference`.

### Nuevos

| Archivo | Rol |
|---|---|
| [`Cart/ShoppingCart.cs`](../src/Frontend/Shop133.Web/Cart/ShoppingCart.cs) | El carrito: líneas, `Add`/`SetQuantity`/`Remove`/`Clear`, `Total` y `UnitCount` calculados, y los dos topes copiados de la API |
| [`Cart/CartLine.cs`](../src/Frontend/Shop133.Web/Cart/CartLine.cs) | La foto congelada: los cinco campos de `OrderLine` más `ImageUrl`, el único que no viaja al pedido |
| [`Cart/CartStore.cs`](../src/Frontend/Shop133.Web/Cart/CartStore.cs) | El único sitio que toca `ISession`. `GetAsync`/`SaveAsync`, la clave y el JSON |
| [`Cart/CartNotice.cs`](../src/Frontend/Shop133.Web/Cart/CartNotice.cs) | Las dos claves de `TempData` — el contrato entre el controller y el layout, y el anclaje de `6.7` |
| [`Controllers/CartController.cs`](../src/Frontend/Shop133.Web/Controllers/CartController.cs) | `Index` (GET) y `Add`/`SetQuantity`/`Remove`/`Clear` (POST + antiforgery + redirect) |
| [`Views/Cart/Index.cshtml`](../src/Frontend/Shop133.Web/Views/Cart/Index.cshtml) | La página del carrito |
| [`ViewComponents/CartBadgeViewComponent.cs`](../src/Frontend/Shop133.Web/ViewComponents/CartBadgeViewComponent.cs) | El contador del navbar |
| `Views/Shared/Components/CartBadge/Default.cshtml` | Su vista. La ruta no es negociable: Razor la busca exactamente ahí |
| `Views/Shared/Unavailable.cshtml` | **Movida** desde `Views/Catalog/`, con el `<h1>` generalizado |

### Modificados

| Archivo | Cambio |
|---|---|
| [`Program.cs`](../src/Frontend/Shop133.Web/Program.cs) | `AddDistributedMemoryCache()`, `AddSession(...)`, `AddHttpContextAccessor()`, `AddScoped<CartStore>()` y `app.UseSession()` entre `UseRouting()` y el endpoint |
| [`Views/Shared/_Layout.cshtml`](../src/Frontend/Shop133.Web/Views/Shared/_Layout.cshtml) | *Carrito* deja de estar `disabled` y estrena badge; los avisos de `TempData` se pintan en el `<main>` |
| [`Views/Catalog/Details.cshtml`](../src/Frontend/Shop133.Web/Views/Catalog/Details.cshtml) | El botón deshabilitado se convierte en el formulario de añadir |
| [`Views/_ViewImports.cshtml`](../src/Frontend/Shop133.Web/Views/_ViewImports.cshtml) | `@using Shop133.Web.Cart` |
| [`Models/Money.cs`](../src/Frontend/Shop133.Web/Models/Money.cs) | Expone `Culture`: apareció el primer número de la interfaz que no es dinero |

`Views/Catalog/Unavailable.cshtml` se **borra** (movida). `Views/Catalog/Index.cshtml` no se toca
(decisión 9).

---

## Detalles que cuestan tiempo

**Razor trata una arroba entre caracteres de palabra como la de un correo electrónico, y la
expresión sale impresa en el HTML.** El badge se escribió primero como
`Carrito@await Component.InvokeAsync("CartBadge")` dentro del `<a>` del navbar, y el HTML servido
contenía literalmente `Carrito@await Component.InvokeAsync("CartBadge")` — **compilación limpia, cero
warnings, cero errores en tiempo de ejecución**. La regla existe para poder escribir `foo@bar.com`
sin escapar nada. La solución es cerrar una etiqueta antes, de modo que la arroba vaya detrás de un
`>`: `<span>Carrito</span>@await Component.InvokeAsync(...)`.

**Un servicio levantado bloquea su propio `.exe` y la compilación falla — dos veces en este punto.**
`MSB3027 … The file is locked by: "Shop133.Web (18448)"`. Es la trampa que `4.9` documentó, y aquí
mordió justo después de arreglar lo anterior: hay que **parar el proceso, recompilar y relanzarlo**.
Lo peligroso es la variante que `4.9` midió: si además se ejecutan tests, el runner corre el binario
**viejo** y da verde con el error de compilación diez líneas más arriba.

**Reiniciar el frontend vacía todos los carritos, y eso convierte cada recompilación en una
verificación.** Es la decisión 1 funcionando, no un fallo — pero conviene saberlo antes de perder
cinco minutos buscando por qué "se ha perdido el carrito" tras un cambio de una línea.

**Los mensajes de rechazo son texto visible, no comentarios.** Los `.cs` de `Shop133.Web` llevan los
comentarios sin acentos (convención heredada de `6.2`), y al escribir `ShoppingCart` esa costumbre se
coló en las cadenas que ve el usuario: *"No se pueden pedir mas de…"*. La salida de `curl` lo destapó.
Y en el mismo sitio apareció un `:N0` con la cultura ambiente, que es exactamente la trampa que
`Money` existe para evitar desde `6.2` — el mismo mensaje diría `10,000` o `10.000` según el Windows
de quien ejecute. Por eso `Money` pasa a exponer su `Culture` en lugar de declararse una segunda en
otro archivo.

**Edge headless no comparte el frasco de cookies de `curl`, y el carrito se llena con POST.** Para
capturar el carrito lleno hay que guardar el HTML que ya devolvió el servidor y reescribir sus rutas
absolutas (`href="/` → `href="http://127.0.0.1:5025/`) antes de abrirlo con `file://`; si no, la
página se pinta sin CSS y parece rota.

**`curl.exe` y el token antiforgery**: el token va en el HTML de cualquier página que tenga un
`<form asp-action>`, y **es reutilizable mientras dure la cookie**, así que no hace falta volver a
pedir una página por cada POST — lo que importa para no gastar cupo del rate limiter al medir.

---

## Verificación

Infraestructura: `docker compose up -d` (con `catalog-api` en el contenedor, puerto 5125), el Gateway
en 5104 apuntado a ese contenedor con
`ReverseProxy__Clusters__catalog__Destinations__primary__Address`, y `Shop133.Web` en 5025.

### Compilación y suites

```
$ dotnet build shop133.slnx
Build succeeded.
    2 Warning(s)     <- xUnit1051 preexistentes en Orders.Tests, ajenas a 6.3
    0 Error(s)

$ dotnet tests\Shop133.ArchitectureTests\bin\Debug\net10.0\Shop133.ArchitectureTests.dll
   Shop133.ArchitectureTests  Total: 17, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 1.173s

$ dotnet tests\Gateway\Shop133.Gateway.Tests\bin\Debug\net10.0\Shop133.Gateway.Tests.dll
   Shop133.Gateway.Tests  Total: 26, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 1.282s
```

**No se añade ninguna regla de arquitectura y se dice en lugar de inventar una**: este punto no
introduce ninguna forma estructural nueva —ni paquete, ni proyecto, ni `ProjectReference`— y una
regla que nunca engancha pasa en verde para siempre (precedente `3.3`/`3.5`/`4.5`/`5.2`/`6.2`). El
repositorio se queda en **140 tests**.

### 1. El formulario de añadir no lleva precio

```
$ curl.exe -s -c jar.txt -b jar.txt http://127.0.0.1:5025/catalog/details/1 -o d1.html -w "%{http_code}"
200
action="/cart/add"
name="__RequestVerificationToken"
name="productId" value="1"
-- precios dentro del <form>:
0
```

### 2. Añadir, y el ciclo POST-Redirect-GET

```
add(1,2)          -> 302  http://127.0.0.1:5025/cart
GET /cart         -> 200
  TAZA-001, cantidad 2, $249.00, subtotal y total $498.00
  badge del navbar: 2
```

### 3. Añadir otra vez el mismo producto **suma**, no duplica la línea

```
add(1,2)          -> 302
  TAZA-001 aparece 1 vez
  name="quantity" value="4"
  total: $996.00
```

### 4. Segundo producto, y `SetQuantity` a 0 quita la línea

```
add(2,1)                  -> 302   lineas: 2
setquantity(2, 0)         -> 302   lineas: 1
```

### 5. POST sin token antiforgery

```
clear sin token   -> 400
el carrito sigue con 1 linea
```

### 6. Otra sesión (otro frasco de cookies) ve un carrito vacío

```
GET /cart (other.txt) -> 200
El carrito está vacío
```

### 7. **La comprobación que es el punto: qué hay en la cookie**

```
-- veces que aparece el precio "249" en la cookie de sesion:
0
-- longitud del valor de .Shop133.Session:
190
```

Un identificador opaco de 190 caracteres. **El precio no sale del servidor.**

### 8. Producto que Catalog no conoce: aviso, **no** 503

```
add(999999,1)     -> 302  http://127.0.0.1:5025/catalog
destino           -> 200
"El producto 999999 ya no está en el catálogo."
```

### 9. Tope de cantidad

```
setquantity(1, 10001) -> 302
"No se pueden pedir más de 10,000 unidades de un producto."
cantidad tras el rechazo: value="4"      <- no se tocó nada
```

Con el mismo tope desde `add`, el redirect vuelve a la **ficha** y no al carrito, que es donde estaba
el formulario.

### 10. El carrito no sobrevive al reinicio del proceso — decisión 1, medida

```
(misma cookie, Shop133.Web reiniciado)
GET /cart -> 200
El carrito está vacío
lineas: 0
```

### 11. **Con el Gateway parado, `/cart` sigue pintándose** — decisión 3, medida

```
GET /cart     -> 200  (tiempo 0.004561 s)   1 linea, total $807.00
GET /catalog  -> 503  (tiempo 2.056744 s)
```

Los dos números juntos son la decisión 3 entera: el catálogo tarda **2,06 s** en rendirse (el rechazo
de conexión sobre el literal `127.0.0.1` que `2.3` midió en 2,03 s) y el carrito contesta en **4,5
ms** porque no llama a nadie. Si `/cart` releyera precios, esa línea sería un 503.

### 12. `POST /cart/add` con el Gateway parado → 503 con la vista compartida

```
-> 503
<title>La tienda no está disponible - Shop133</title>
"La tienda no está disponible ahora mismo"
Reintentar apunta a: /cart
```

El *Reintentar* resuelve contra el controller actual, así que desde el carrito lleva a una página que
**funciona incluso con el Gateway caído**.

### 13. Carrito de cinco líneas

```
add(1,2) add(11,2) add(21,2) add(31,2) add(41,2)   -> 302 x5
GET /cart -> 200
  lineas: 5
  skus:   TAZA-001 LLAV-001 PLAY-001 PINS-001 LIBR-001
  badge:  10
  total:  $1,810.00    (498+178+658+118+358)
```

### 14. Capturas

`/cart` lleno y `/catalog/details/1`, con Edge headless a 1200 px de ancho (por encima del clamp de
~500 px que `6.1` midió) y por debajo de los ~1400 px de alto a partir de los cuales `6.2.1` midió que
el archivo no llega a escribirse.

---

## Pendiente

- **El tope de 50 líneas distintas no se pudo ejercitar.** El seed de `1.4` tiene exactamente 50
  productos, así que con el carrito lleno no queda ningún producto **nuevo** que añadir: volver a
  añadir cualquiera de ellos entra por la rama de "sumar cantidad". La rama está escrita y revisada,
  pero **no medida** — y este proyecto documenta lo que se ejecutó. Se cerraría creando un producto de
  prueba por `POST /products`, que ensuciaría el catálogo de desarrollo.
- **Un carrito activo puede sostener una foto más vieja que la ventana de 30 min de `4.8`**, porque
  cada petición reinicia el `IdleTimeout`. No es un defecto de este punto: lo enseña `6.5` cuando el
  pedido se cancela solo. Lo que **no** existe es una forma de avisarlo *antes* de tramitar; si
  alguna vez se quiere, el sitio es `6.4`.
- **El carrito muere con el proceso y no se comparte entre réplicas** (decisión 1, medida arriba). El
  día que `Shop133.Web` tenga contenedor o más de una instancia, hace falta un `IDistributedCache` de
  verdad y con él el primer paquete de este proyecto — que muy probablemente llegue antes por `6.6`.
- **La Fase 6 sigue sin ningún punto de test en el roadmap**, así que nada de `6.1`, `6.2` ni `6.3`
  está cubierto; todo lo de arriba es verificación a mano. Es el mismo hueco sin dueño que dejó `4.6`.
  El día que se recoja, `ShoppingCart` es lo más fácil y valioso de cubrir —es lógica pura, sin
  Docker— y entonces `Shop133.Web` necesitaría el `public partial class Program { }` que `6.1`
  deliberadamente no añadió.
- **Nada vigila que el carrito siga sin salir del servidor.** Añadir mañana un `<input type="hidden"
  name="unitPrice">` al formulario de la ficha rompería el punto entero y **ninguna prueba se
  enteraría** — misma forma que la regla 2, que tampoco es ejecutable. **Sin dueño.**
- **La cabecera `Location` del `201` de `POST /api/orders` sigue apuntando al backend sin prefijo**,
  deuda medida en `5.1` y releída en `5.3`, `6.1` y `6.2`. Sigue sin dueño, y a partir de `6.4` deja
  de ser teórica.
