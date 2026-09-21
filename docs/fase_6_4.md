# Fase 6.4 — Formulario de checkout con validación client-side

**Fecha:** 2026-09-20 · **Estado:** completado · **Roadmap:** [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md) → punto **6.4**

---

## Objetivo

Cerrar el camino que `6.3` dejó apuntado: convertir el carrito de la sesión en un pedido de verdad.
Entra el formulario de checkout con validación en el navegador, el `POST /api/orders` por el
Gateway, el vaciado del carrito y una confirmación con el número de pedido.

**Es el primer POST que sale de este proyecto.** Hasta hoy todo lo que `Shop133.Web` hacía contra
el Gateway eran lecturas, y con este punto **la saga entera —cinco servicios, doce mensajes, cinco
bases de datos— arranca por primera vez desde un formulario de navegador** en vez de desde un
`curl`. El botón *Tramitar pedido* existía ya en `Views/Cart/Index.cshtml`, `disabled`, con la nota
*"El checkout llega en 6.4"*; este punto lo activa.

**Fuera de alcance, y cada cosa tiene dueño:** la página de estado en vivo (`6.5`), Polly (`6.6`) y
los toasts (`6.7`). **No se toca ningún servicio, ni el Gateway, ni un contrato, ni una migración**
— todo cabe en `src/Frontend/Shop133.Web/`. El item *Estado del pedido* del navbar sigue
deshabilitado: lo activa `6.5`, y adelantarlo aquí sería prometer un destino que no existe.

**No entra ningún paquete.** jquery 3.7.1, jquery-validation 1.21.0, jquery-validation-unobtrusive
4.0.0 y Bootstrap 5.3.3 están vendorizados en `wwwroot/lib/` desde `0.1`, y
`_ValidationScriptsPartial.cshtml` existía sin que ninguna vista lo usara. Así que
`Shop133.Web.csproj` sigue con **cero `PackageReference` y cero `ProjectReference`** — sigue siendo
`6.6` quien rompa eso. La suite de arquitectura se queda en **17** y el repositorio en **140**: la
Fase 6 no tiene punto de test.

---

## Decisiones

### 1. El formulario tiene UN solo campo editable, porque el contrato tiene uno

`CreateOrderRequest` son `CustomerEmail` e `Items`, y nada más. Las líneas las pone el carrito, así
que lo único que se teclea en todo el checkout es el correo.

*Descartado* pedir nombre y dirección de envío, que es lo que uno espera de un checkout: **no hay
dónde ponerlos**. O se tiran al vacío —un formulario que miente sobre lo que hace con lo que le
escriben— o hay que tocar el contrato, la entidad `Order`, su configuración de EF y una migración
de `OrdersDb`, que es un punto de roadmap entero metido dentro de uno de frontend. Es el mismo
criterio con el que `6.2` se negó a añadir paginación a `Catalog.API` para servir a una vista.

Lo que eso deja es un formulario pequeño y un resumen grande, y está bien así: el trabajo de esta
pantalla no es recoger datos, es **enseñar lo que se va a comprar y a qué precio** antes de que la
foto salga hacia Catalog.

### 2. El resumen es de SOLO LECTURA, y las cantidades se siguen cambiando en `/cart`

*Descartado* dejar editar cantidades aquí. Sería otra página de carrito con otro nombre: los
mismos cuatro formularios de `6.3`, los mismos topes, los mismos avisos y el mismo
POST-Redirect-GET, duplicados. Una decisión, un sitio.

### 3. El carrito se vacía SOLO después del 201

Si el POST falla —Gateway caído, cupo agotado, 400— el carrito tiene que seguir intacto para poder
reintentar. Vaciarlo antes sería perder la compra por un fallo de red. **Medido en las cuatro ramas
de fallo: el carrito sobrevive en todas.**

Y al revés: **el carrito se vacía aunque el pedido acabe cancelándose** (precio caducado, sin
stock, pago rechazado). Es deliberado y no un descuido: el pedido **existe** y su desenlace es
asíncrono, así que retener el carrito "por si acaso" dejaría al comprador con dos copias de la
misma compra. Enterarse del desenlace es el correo de Notifications (`4.6`) y la página de `6.5`.

### 4. Un 400 de Orders NO se pinta como "el Gateway no responde" — entra `OrderRejectedException`

Hasta hoy `CatalogClient.ReadAsync` convertía **cualquier** no-2xx en
`GatewayUnavailableException`, lo cual era razonable mientras el único fallo posible fuera de la
dependencia. Con un POST deja de serlo: `POST /orders` tiene documentado un **400
`ValidationProblemDetails`**, y ahí el Gateway contestó, el servicio contestó, y lo que está mal es
lo que se le mandó. Enseñar la página de caída sería el diagnóstico seguro de sí mismo y
equivocado que la decisión 3 de `6.2` se negó a dar — el mismo motivo por el que
`CatalogClient.GetProductsAsync` recorta el `page` a 1 en vez de dejar que la API conteste 400.

El 400 se comprueba **antes** que `IsSuccessStatusCode`, igual que `FindProductOrNullAsync`
comprueba el 404 antes: es una respuesta válida y documentada del servicio.

**Y es una incoherencia del programa, no un error del usuario**, que es lo que decide que lance en
vez de devolver un motivo. Lo único que se teclea es el correo, y eso se valida en cliente y en
servidor antes de salir; si aun así llega un 400, el cuerpo lo construyó el carrito. Por eso se
loguea como `LogError`, mientras que un tope de carrito devuelve un texto y no revienta
(`ShoppingCart.Add`, precedente `StockItem.CanReserve`): allí se equivoca una persona, aquí nos
equivocamos nosotros.

*Descartado* devolver un resultado con dos formas desde `OrdersClient`: obligaría a todo llamante a
desempaquetar en el camino feliz, que es el único que debería leerse de corrido.

Se tiran **las claves** del `ValidationProblemDetails` y se conservan los textos: las claves
nombran campos de un DTO de Orders (`Items[0].ProductId`) que este proyecto no declara y que no le
dicen nada a quien compra. Los textos van al `asp-validation-summary="ModelOnly"` con clave vacía,
y ese emparejamiento es lo que hace **visible** la rama; con `"None"` el pedido se rechazaría en
silencio.

### 5. El id sale del CUERPO del 201, no de la cabecera `Location`

La deuda que `5.1` midió y que `5.3`, `6.1`, `6.2` y `6.3` fueron releyendo: el 201 sale con
`Location: http://localhost:5189/orders/{id}` — la dirección **real** del servicio, sin el prefijo
público del Gateway. `6.3` avisó por escrito de que en `6.4` dejaba de ser teórica.

Se vuelve a **medir** (abajo) y **no se arregla**: este punto es de frontend, y el arreglo de
verdad es un transform de respuesta en YARP, o sea código de la Fase 5 con sus propios tests en
`Shop133.Gateway.Tests`. El frontend la esquiva leyendo el `Id` del cuerpo, que necesita de todas
formas. **Sigue sin dueño**, y ahora con un consumidor real delante.

### 6. La confirmación no llama al Gateway ni una vez

`/checkout/placed/{id}` enseña número de pedido, correo y total, y nada más.

*Descartado* releer `GET /orders/{id}`: gastaría un permiso del cupo `orders-write` —**diez** por
minuto, el mismo que gasta el POST— por cada vista de una página que dos segundos después del alta
solo puede decir `Pending`. Y es literalmente el entregable de `6.5`.

El precio está dicho en voz alta en vez de disimulado: el id viene de la **ruta** y sobrevive a un
F5; el correo y el total viajan en `TempData`, que dura exactamente una petición, así que al
recargar se pierden y la página lo dice. El número de pedido es la parte que importa y es la que no
se pierde.

Sobre lo que viaja en esa cookie de `TempData`: un `Guid`, el correo que el usuario acaba de teclear
y un total **ya formateado**. Ninguno es una autoridad que nadie pueda reenviar —el pedido ya existe
y su importe ya lo congeló Orders—, y la foto de precios que `6.3` protegió salió de la sesión al
vaciarse el carrito, un paso antes. La propiedad de `6.3` se comprueba abajo en vez de darse por
supuesta.

### 7. `[BindNever]` en el resumen, y hay que repoblarlo a mano

`Lines`, `Total` y `UnitCount` llevan `[BindNever]`. Sin eso, un POST fabricado a mano puede mandar
sus propias líneas y su propio total: nada de eso llegaría al cuerpo del pedido —el controller lo
construye desde la sesión—, pero **el resumen que se le re-pinta al usuario le estaría enseñando
cifras que escribió el atacante**. Es la propiedad que `6.3` defendió en el carrito, un piso más
arriba.

El precio es la trampa clásica del patrón: en el camino de error hay que **repoblar** las tres
desde el carrito antes de devolver la vista, o el resumen sale en blanco y parece que el carrito se
vació solo. Está en un helper (`WithCartSummary`) que llaman los tres caminos que devuelven la
vista, y se comprueba abajo.

### 8. La acción se llama `Index`, y no es indiferente

`Views/Shared/Unavailable.cshtml` remata con `<a asp-action="Index">Reintentar</a>` **sin**
`asp-controller`, así que resuelve contra el controller actual. Con cualquier otro nombre de
acción, caerse el Gateway durante el checkout dejaría al usuario con un botón que da 404. Medido:
el botón apunta a `/checkout`.

### 9. Un `OrdersClient` aparte, no un método más en `CatalogClient`

*Descartado* meter el POST en el cliente existente: se llama `CatalogClient` y hablaría con Orders.
El argumento que decide no es el nombre, sino que **son dos cupos distintos del rate limiter de
`5.2`** (60/min las lecturas del catálogo, 10/min los pedidos) y, en `6.6`, dos políticas de
reintento incompatibles: reintentar automáticamente una **lectura** es gratis, y reintentar una
**escritura** crea pedidos duplicados.

Lo que sí se comparte es la **configuración** del `HttpClient`, extraída a una función local en
`Program.cs`: la base y el timeout tienen que ser los mismos, y dos lambdas calcadas son dos sitios
donde el `/api/` puede divergir sin que nada avise.

### 10. `NewOrder`/`NewOrderLine`/`PlacedOrder` se re-declaran, y con otro nombre

Importar `CreateOrderRequest` exigiría un `ProjectReference` a `Orders.API`: la regla 3 rota de
frente, con `Frontend_DoesNotReference_ServicesOrGateway` en rojo. Precedente literal:
`CatalogProduct` re-declara 4 de los 9 campos de `ProductResponse`.

Los nombres son **distintos a los del servidor** a propósito, siguiendo el criterio que `CatalogPage`
dejó escrito frente a `PagedResponse`: un tipo del frontend con el nombre del DTO del servicio
invita a "unificarlos", que es exactamente lo que la regla 3 prohíbe. `PlacedOrder` declara además
**solo lo que se usa** (cuatro campos de los seis que trae `OrderResponse`); `System.Text.Json`
ignora el resto.

Y `NewOrderLine` son, campo a campo, las **cinco primeras propiedades de `CartLine`** — la promesa
que el `///` de aquel tipo lleva escrita desde `6.3`, cumplida en una expresión. `ImageUrl` se queda
fuera: un pedido no guarda la miniatura del catálogo.

### 11. CORRECCIÓN — el `Timeout` de 5 s que `6.2` documentó nunca se escribió

Al añadir el segundo cliente tipado apareció esto en `Program.cs`:

```csharp
builder.Services.AddHttpClient<CatalogClient>(client =>
{
    client.BaseAddress = new Uri(gatewayBaseUrl.TrimEnd('/') + "/api/");

    // 5 s en vez de los 100 de fabrica (precedente de 2.3). ...
    // 6.6 TIENE QUE RELEER ESTA LINEA. ...
});
```

**El comentario describe una línea que no existe.** No hay ninguna asignación a `client.Timeout`,
así que el valor real era el de fábrica: **100 segundos**. Un comentario que describe una línea
ausente es peor que no tenerlo — sostiene que una decisión está tomada, y encima le pasa el aviso
al punto siguiente (`6.6` habría releído una línea inexistente).

Se corrige aquí, no en un punto aparte, por dos motivos: `6.4` añade el segundo consumidor de esa
misma configuración, y la espera de minuto y medio pasa ahora **delante de alguien que está
tramitando un pedido**. Precedente de escribir una corrección como corrección en vez de
disimularla: la 2b de [fase_3_3.md](fase_3_3.md).

Matiz honesto sobre lo que esto cambia y lo que no: el 503 medido abajo tarda **2,08 s** porque es
un **rechazo de conexión** en `127.0.0.1` (los 2,03 s que midió `2.3`), y eso no dependía del
timeout. Lo que el timeout gobierna es el caso del Gateway **colgado** — que acepta la conexión y
no contesta —, y ese no se ha provocado. O sea: la línea estaba mal y ahora está bien, pero **la
medición de abajo no es la prueba de ello**, y decirlo es más útil que atribuirle un mérito que no
tiene.

### 12. CORRECCIÓN — la página de 429 mentía en cuanto hubo un segundo llamante

`Views/Shared/Unavailable.cshtml` llevaba escrito desde `6.2`:

> El Gateway limita las lecturas del catálogo a **60 por minuto y por IP**

Eso es el cupo `catalog-read`. El checkout va por `orders-route`, cuyo cupo `orders-write` son
**diez** por minuto —mucho más estrecho a propósito, porque cada POST arranca la saga entera—, así
que desde el momento en que existe un segundo llamante esa página le da al usuario **una cifra
falsa y un consejo equivocado**.

`GatewayUnavailableException` gana un `Quota`: una frase corta que pone **el cliente que se quedó
sin cupo**, que es el único que sabe contra qué ruta iba. *Descartado* que la vista decidiera por
el controller actual (una cadena de `if` que hay que ampliar con cada ruta nueva, exactamente la
forma de fallo que `CartController` evita poniendo el `[AutoValidateAntiforgeryToken]` en la clase).

Limitación dicha en voz alta: es el valor **configurado por defecto**, no el vivo. El Gateway no le
cuenta su cupo a nadie, solo devuelve 429, y que el frontend leyera la configuración del Gateway
sería justo el acoplamiento del que la regla 3 libra. Se ve en la verificación de abajo: con el
cupo bajado a 2 por variable de entorno, la página sigue diciendo 10.

### 13. Tercera copia de `Unavailable(...)`, y se queda copiada

`CatalogController`, `CartController` y ahora `CheckoutController` tienen el mismo helper privado,
con el log distinto. El `///` de `CartController` fijó el criterio en **cuatro** ocurrencias
(precedente de `SqlServerContainerFixture`, que esperó a tener cuatro copias en `3.7` antes de
extraerse). La cuarta decide; anotarlo es la mitad del trato.

---

## Cambios

### Nuevos — `src/Frontend/Shop133.Web/`

| Archivo | Rol |
|---|---|
| `Gateway/NewOrder.cs` | Cuerpo del POST: correo y líneas. Re-declarado (decisión 10). |
| `Gateway/NewOrderLine.cs` | Una línea: las cinco propiedades de `CartLine` que son la foto. |
| `Gateway/PlacedOrder.cs` | Lo que se lee del 201: `Id`, `CustomerEmail`, `Status`, `Total`. |
| `Gateway/OrderRejectedException.cs` | El 400, separado de la indisponibilidad (decisión 4). |
| `Gateway/OrdersClient.cs` | Segundo cliente tipado y primer POST del proyecto. |
| `Models/CheckoutViewModel.cs` | El formulario: un campo con DataAnnotations + el resumen con `[BindNever]`. |
| `Models/PlacedOrderViewModel.cs` | La confirmación, con el total ya formateado. |
| `Controllers/CheckoutController.cs` | `GET`/`POST /checkout` y `GET /checkout/placed/{id}`. |
| `Views/Checkout/Index.cshtml` | Primera vista del proyecto con `asp-for`/`asp-validation-for`/`asp-validation-summary`. |
| `Views/Checkout/Placed.cshtml` | La confirmación. |

### Modificados

| Archivo | Cambio |
|---|---|
| `Program.cs` | `AddHttpClient<OrdersClient>`; configuración de los dos clientes extraída a una función local; **se escribe el `client.Timeout` ausente** (decisión 11). |
| `Gateway/GatewayUnavailableException.cs` | Nueva propiedad `Quota` (decisión 12). |
| `Gateway/CatalogClient.cs` | Declara su cupo y lo pasa al lanzar. |
| `Views/Shared/Unavailable.cshtml` | El 429 imprime `Model.Quota` en vez del "60 por minuto" fijo. |
| `Views/Cart/Index.cshtml` | El botón `disabled` pasa a `<a asp-controller="Checkout">`; fuera la nota *"El checkout llega en 6.4."* Un `<a>` y no un `<form>`: `GET /checkout` no muta nada. |

---

## Detalles que cuestan tiempo

- **`TempData` no sabe serializar un `decimal`.** El proveedor por cookie admite `string`, `int`,
  `bool`, `DateTime`, `Guid` y listas de esos; un `decimal` revienta en tiempo de **ejecución**, no
  de compilación. Por eso el total se formatea con `Money.Format` **antes** de guardarlo, y
  `PlacedOrderViewModel.FormattedTotal` es un `string`.
- **`TempData` dura exactamente una petición.** Un F5 sobre la confirmación pierde correo y total.
  La vista tiene que seguir siendo correcta sin ellos (`HasDetails`), o quedan dos huecos en blanco
  sin explicación.
- **El log del pedido tiene que ir ANTES del `cart.Clear()`.** Escrito después, `cart.Lines.Count`
  es siempre cero y el log dice que se tramitó un pedido de cero líneas. Se detectó releyendo, no
  ejecutando.
- **Los mensajes de DataAnnotations salen en inglés y nombrando la propiedad** ("The CustomerEmail
  field is required"), y aquí eso es doblemente malo porque son **los que
  jquery-validation-unobtrusive copia a `data-val-*`** y enseña en el navegador sin pasar por el
  servidor. Hay que escribir los tres `ErrorMessage` a mano. Se ve en la verificación: los mensajes
  del 400 de Orders, que sí llegan sin tocar, **vienen en inglés**.
- **El `novalidate` del `<form>` no es un descuido**: apaga la validación **nativa** del navegador
  para que la que se vea sea la de jquery-validation. Sin él salen las dos, con dos estilos y en
  dos idiomas según el navegador.
- **`asp-validation-summary="ModelOnly"` y no `"All"`**: los errores de campo ya salen junto a su
  campo. El resumen queda reservado para los que no son de ningún campo, que hoy son exactamente
  los del 400.
- **Un `@` entre caracteres de palabra es un correo para Razor.** El `placeholder="tu@correo.es"`
  compila y se sirve literal por la misma heurística que `_Layout.cshtml` documentó al revés en
  `6.3`.
- **El truco de `dotnet <dll>` cambia el content root**, así que los servicios se lanzan con
  `dotnet run --project ... --launch-profile http`, que además es lo que carga los User Secrets.
- Para medir esto con `curl.exe` hace falta **tarro de cookies** (`-c jar -b jar`) o cada petición
  es una sesión nueva y el carrito sale siempre vacío. El token antiforgery se saca del HTML de
  cualquier página con `<form asp-action>` y **se reutiliza** mientras dure la cookie.
- **Comprobar un marcador con acento en el HTML servido falla si se escribe el acento.** Razor
  codifica los acentos de una expresión `@(...)` como entidades (`vac&#xED;o`) y los del marcado
  literal no — la trampa que `6.2.1` midió. Un `.Contains("vacío")` dio `False` con el texto
  perfectamente presente.

---

## Verificación

Con `docker compose up -d`, los cuatro servicios de host, el Gateway (con su destino de Catalog
apuntado al contenedor, `ReverseProxy__Clusters__catalog__Destinations__primary__Address=http://localhost:5125/`)
y `Shop133.Web`.

### 1. Compilación

```
$ dotnet build -v:minimal
Build succeeded.
    0 Warning(s)
    0 Error(s)

$ dotnet tests\Shop133.ArchitectureTests\bin\Debug\net10.0\Shop133.ArchitectureTests.dll
   Shop133.ArchitectureTests  Total: 17, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.700s
```

### 2. La validación client-side, en el HTML servido

```
token obtenido: 155 caracteres
POST /cart/add        -> 302 -> http://127.0.0.1:5025/cart
GET /checkout         -> 6550 bytes

--- marcado de validacion client-side en el HTML servido ---
data-val="true"                        PRESENTE
data-val-required                      PRESENTE
data-val-email                         PRESENTE
data-val-maxlength                     PRESENTE
jquery.validate.min.js                 PRESENTE
jquery.validate.unobtrusive.min.js     PRESENTE

--- los mensajes que vera el navegador, sin pasar por el servidor ---
  data-val-email="Ese correo no tiene una forma v&#xE1;lida."
  data-val-maxlength="El correo no puede pasar de 320 caracteres."
  data-val-required="Hace falta un correo para avisarte del pedido."
```

El `{1}` del `[MaxLength]` resuelve al 320 de la constante, no a un literal.

**Lo que esto NO demuestra, dicho claramente:** `curl` no ejecuta JavaScript. Lo comprobado es que
los atributos y los dos scripts llegan al navegador; que jquery-validation los lea y frene el envío
es inferencia, no medición. La rotura 1 de abajo es lo que le da valor a esta comprobación.

### 3. Camino feliz: la saga entera desde un formulario

```
--- POST /checkout ---
302 http://127.0.0.1:5025/checkout/placed/67cced8c-661a-4222-9849-e35ece6b94b2

--- GET la confirmacion ---
  heading: Pedido recibido
  Número de pedido
  67cced8c-661a-4222-9849-e35ece6b94b2
  Aviso a
  ana@correo.es
  Total
  $498.00
```

Y el pedido, visto por el Gateway unos segundos después:

```
  id       : 67cced8c-661a-4222-9849-e35ece6b94b2
  estado   : Confirmed
  correo   : ana@correo.es
  total    : 498.00
  lineas   : 1
    TAZA-001 x2 @ 249.00 = 498.00
```

El carrito quedó vacío (`contiene '<table'`: `False`, `contiene 'Ver el catálogo'`: `True`).

### 4. Compensación, también desde el formulario

Tres unidades del producto más caro, `PLAY-005` a `399.00` = `1197.00`, por encima del
`Payments:DeclineAmountAbove` de `1000.00`.

```
stock del producto 25 (OnHand/Reserved) ANTES:
61/0

POST /cart/add 3x PLAY-005 -> 302
POST /checkout             -> 302 http://127.0.0.1:5025/checkout/placed/ce78e7d1-335c-41ab-966f-2ee7598ab13a

pedido ce78e7d1-335c-41ab-966f-2ee7598ab13a -> estado Cancelled, total 1197.00

stock del producto 25 (OnHand/Reserved) DESPUES:
61/0
```

Y el correo que generó Notifications:

```
2|Tu pedido ce78e7d1-... no se ha podido completar|Hola,

Lo sentimos: tu pedido ce78e7d1-... se ha cancelado y no se te ha cobrado nada.

Motivo: el importe 1197.00 supera el límite autorizado de 1000.00

Si crees que ha sido un error, vuelve a intentarlo desde la tienda.
```

Reservó 3 y devolvió 3 sin que nadie intervenga: la regla 7, disparada desde un `<form>`.

### 5. Validación de servidor y antiforgery

```
--- 5. correo invalido saltandose el navegador ---
  codigo: 200
  error junto al campo: Ese correo no tiene una forma v&#xE1;lida.
  el resumen sigue pintado: True
  el carrito sigue: SI, intacto

--- 9. POST sin token antiforgery ---
  codigo: 400
  el carrito sigue: SI, intacto
```

`el resumen sigue pintado: True` es la comprobación de la decisión 7: el `[BindNever]` no dejó el
resumen en blanco porque `WithCartSummary` lo repobló.

### 6. Carrito vacío

```
--- GET /checkout con el carrito vacio ---
  302 -> http://127.0.0.1:5025/cart
  aviso: El carrito est&#xE1; vac&#xED;o, as&#xED; que no hay nada que tramitar.
```

### 7. La cabecera `Location`, medida otra vez

```
HTTP/1.1 201 Created
Location: http://localhost:5189/orders/c3cc1119-9828-4eb6-8597-eeeb56caf118
```

Apunta al backend y sin el prefijo `/api`. El frontend no la mira.

### 8. El Gateway parado

```
--- 7. tramitar con el Gateway PARADO ---
  [503] [2.075974s]
  titulo: La tienda no está disponible ahora mismo
  rama de cupo (429)?: False
  boton Reintentar apunta a: /checkout

  el carrito SOBREVIVE: SI
```

2,08 s, que son los 2,03 s del rechazo en `127.0.0.1` que midió `2.3`. Y el botón *Reintentar*
resuelve a `/checkout`: la decisión 8 comprobada.

### 9. El cupo agotado

Con `RateLimiting__OrdersWrite__PermitLimit=2` por variable de entorno:

```
  intento 1 -> 302
  intento 2 -> 302
  intento 3 -> 429
    cuota que anuncia la pagina: la creaci&#xF3;n de pedidos a 10 por minuto y por IP
    espera:                      Vuelve a intentarlo en 60 segundos.
    el carrito SOBREVIVE:        SI
```

**Antes de este punto esa línea habría dicho "las lecturas del catálogo a 60 por minuto"**. Y se ve
la limitación de la decisión 12: dice 10 mientras el cupo vivo era 2.

### 10. `GET /orders/{id}` comparte el cupo del POST — el aviso para `6.5`

```
  1=200  2=200  3=200  4=200  5=200  6=200  7=200  8=200  9=200  10=200  11=429  12=429  13=429  14=429
```

Diez lecturas y el cupo de **escritura** está agotado. Un sondeo cada 2-3 s, que es lo que plantea
`6.5`, son 20-30 peticiones por minuto: el 429 llegaría a los ~25 segundos de abrir la página.

### 11. Las dos roturas a propósito

**Rotura 1 — quitar el `@section Scripts`:**

```
  data-val="true"                        PRESENTE
  data-val-email                         PRESENTE
  jquery.validate.min.js                 AUSENTE
  jquery.validate.unobtrusive.min.js     AUSENTE
```

Esto es lo que hace útil la comprobación 2: **los `data-val-*` siguen ahí**. El HTML parece
validado y no lo lee nadie; no hay error, ni aviso, ni diferencia visible en pantalla. Es la forma
exacta del fallo silencioso.

**Rotura 2 — divergir la constante duplicada (400 en el frontend, 320 en Orders)**, y mandar un
correo de 350 caracteres:

```
  codigo HTTP del frontend: 200
  es la pagina de caida?:   False
  resumen de validacion:    The field CustomerEmail must be a string or array type with a maximum length of '320'.
  el carrito SOBREVIVE:     SI
```

La rama de la decisión 4, ejercitada: Orders contestó 400, el frontend lo pintó en el resumen,
**no** salió la página de "el Gateway no responde" y el carrito sobrevivió. Es además la medida de
qué cuesta que las constantes duplicadas diverjan — y se ve que el mensaje **llega en inglés**,
porque es de Orders y allí nadie lo tradujo.

### 12. Restauración e invariantes

```
--- restauracion confirmada ---
  jquery.validate.min.js                 PRESENTE
  jquery.validate.unobtrusive.min.js     PRESENTE
  data-val-maxlength dice:  El correo no puede pasar de 320 caracteres.

--- la cookie de sesion sigue sin llevar el precio (propiedad de 6.3) ---
  .Shop133.Session: 186 caracteres opacos
  contiene '249':   False
  contiene 'TAZA':  False

--- el .csproj sigue sin paquetes ni referencias ---
  PackageReference: 0   ProjectReference: 0
```

Y un último pedido con el código ya restaurado:

```
POST /checkout tras restaurar -> 302 http://127.0.0.1:5025/checkout/placed/a1853d9e-24b8-47dc-b80c-d86cfafd7c24
pedido a1853d9e-24b8-47dc-b80c-d86cfafd7c24 -> Confirmed, total 249.00
```

---

## Pendiente

- **La cabecera `Location` del 201 sigue rota** para cualquier cliente que no sea este frontend
  (decisión 5). El arreglo es un transform de respuesta en YARP, con sus tests en
  `Shop133.Gateway.Tests`. **Sigue sin dueño**, y desde hoy con un consumidor real delante.
- **`GET /orders/{id}` comparte el cupo `orders-write` de 10/60 s con el POST**, medido arriba.
  Condiciona directamente a `6.5`: o el sondeo es mucho más lento que los 2-3 s que plantea el
  roadmap, o `orders-route` tiene que partirse en dos rutas con dos cupos (lectura y escritura), lo
  cual es un cambio en el Gateway. **Es de `6.5` y hay que decidirlo antes de escribir el polling.**
- **Nada vigila que el checkout no empiece a mandar precios desde el navegador.** Añadir mañana un
  `<input type="hidden" name="unitPrice">` rompería `6.3` y `6.4` a la vez y **ninguna prueba se
  enteraría** — misma forma que la regla 2, que tampoco es ejecutable. **Sin dueño**, igual que lo
  dejó `6.3`.
- **El timeout de 5 s corregido no se ha ejercitado.** Lo que se midió es un rechazo de conexión,
  que no pasa por él. Provocarlo exige un Gateway que acepte y no conteste; el sitio natural es
  `6.6`, que tiene que releer esa línea de todas formas.
- **La Fase 6 sigue sin ningún punto de test en el roadmap**, así que nada de `6.1`–`6.4` está
  cubierto: todo lo de arriba es verificación a mano. Es el mismo hueco sin dueño que dejó `4.6`.
  De este punto, lo más fácil y valioso de cubrir sería `OrdersClient` (las tres ramas de la
  decisión 4 con un stub HTTP) y el `CheckoutController` con `WebApplicationFactory` — y entonces
  `Shop133.Web` necesitaría el `public partial class Program { }` que `6.1` deliberadamente no
  añadió.
- **Tercera copia de `Unavailable(...)`** (decisión 13). La cuarta decide la extracción.
- **El resumen del checkout no avisa de una foto caducada antes de tramitar**, que es lo que `6.3`
  apuntó como posible trabajo de este punto. No se hace porque **el frontend no puede saberlo**: la
  ventana de `4.8` no mide la edad de la línea sino el tiempo desde que el precio **cambió**, y
  `PreviousPrice`/`PriceChangedAt` no se publican en `ProductResponse` justamente para que nadie
  pueda forjar una foto auténtica. Avisar por edad sería dar por caducadas fotos perfectamente
  válidas. Lo que sí hace la página es decir en un párrafo que el pedido puede cancelarse solo, que
  es la verdad y es lo que `6.5` enseña.
