# Fase 6.5 — Página de estado del pedido con sondeo

**Fecha:** 2026-09-23 · **Estado:** completado · **Roadmap:** [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md) → punto **6.5**

---

## Objetivo

`6.4` dejó al frontend capaz de arrancar la saga desde un formulario y **ciego a su desenlace**: la
página de confirmación no llama al Gateway ni una vez, y su último párrafo decía literalmente *"El
seguimiento en vivo de esas etapas llega en 6.5"*. Hasta hoy, el único sitio donde alguien se
enteraba de que su pedido se había cancelado era el correo de Notifications (`4.6`).

Este punto cierra eso y, al hacerlo, **recoge las dos deudas que el repositorio había dejado
apuntadas aquí por escrito**: el cupo compartido entre leer y crear pedidos, y la contradicción
sobre de dónde sale el estado que se enseña.

Lo que el título no dice y es la mitad del entregable: la página **no** pinta una barra de
progreso. Pinta **dos pistas en paralelo**, porque desde `4.9` eso es lo que la saga hace de
verdad.

**Fuera de alcance, y cada cosa tiene dueño:** Polly (`6.6`), los toasts (`6.7`) y el control de
acceso que haría que "tus pedidos" significara algo más que "los de este navegador" (`8.1`).

**Entra un paquete: ninguno.** `Shop133.Web.csproj` sigue con **cero `PackageReference` y cero
`ProjectReference`** — sigue siendo `6.6` quien rompa esa propiedad. La suite de arquitectura se
queda en **17**; el repositorio pasa de **140** a **149**.

---

## Decisiones

### 1. Sondeo desde el navegador, y no SignalR

El roadmap ofrecía las dos. Se elige la simple, y no por pereza.

SignalR obligaría a que el hub viviera en Orders.API —el navegador llegaría por el Gateway con
proxy de WebSocket— y sobre todo tropieza con algo que no se arregla eligiendo transporte: **nadie
publica eventos por etapa**. La saga solo emite `OrderConfirmed` y `OrderCancelled`, o sea el
final. Un push entregaría el desenlace, que es justo lo que el correo de `4.6` ya hace, y **no**
las etapas, que son lo que este punto existe para enseñar. Hacerlo de verdad significaría que la
máquina de estados publicara un evento por transición: un punto de la Fase 4 dentro de uno de
frontend, que es lo que `6.2` ya se negó a hacer.

*Descartado* además por el `.csproj`: el cliente de JavaScript de SignalR habría que vendorizarlo,
y aunque eso no es un `PackageReference`, sí sería la primera dependencia nueva de
`wwwroot/lib/` desde `0.1`.

### 2. El `fetch` sale del NAVEGADOR y va DIRECTO al Gateway

*Descartado* sondear contra una acción de `Shop133.Web` que reenviara al Gateway, que habría
evitado CORS y el contenido mixto de un plumazo.

El motivo es el cupo. `Shop133.Web` renderiza en **servidor**, así que para el rate limiter de
`5.2` **todos los visitantes son una sola IP** — `6.2` lo midió: el primer `429` llega en el render
30. Con el sondeo en el servidor, dos personas mirando su pedido a la vez se quitarían el cupo la
una a la otra. Con el sondeo en el navegador, cada visitante gasta el suyo.

Y es lo que `5.3` dejó escrito que CORS existía para servir. Aquel punto dice, sobre su propio
título, que quien lo necesita de verdad es *"el JavaScript de `6.5` (el polling del estado del
pedido)"*. **Hasta hoy CORS no tenía ni un consumidor**: las llamadas del catálogo y del checkout
son servidor-a-servidor y el navegador no las mira. Es el mismo movimiento con el que `6.2` le dio
su primer consumidor a `GET /categories`.

Es un `GET` sin cabeceras propias, o sea una petición **simple**: no hay preflight.

### 3. Se expone el estado de la SAGA, y eso corrige un comentario del repositorio

**El repositorio decía dos cosas incompatibles sobre este punto**, y había que elegir por escrito:

- El `///` de `OrderStateMachine.Confirmed` justifica que los estados terminales **no** sean
  `Finalize()` diciendo que la fila tiene que poder consultarse después, y nombra expresamente
  *"lo que `6.5` querrá para la página de estado del pedido"*.
- `OrderStateConfiguration` cerraba afirmando que *"la página de estado del pedido de `6.5` leerá
  `Orders`, no esto"*.

**Gana el primero.** El segundo se corrige en el propio archivo. El motivo no es de gusto:
`Order.Status` son **tres** valores (`2.1`), así que un pedido pasa de `Pending` a `Confirmed` de
un salto y una página que solo lo leyera no podría enseñar **ninguna** etapa intermedia — que es
literalmente la sugerencia de UX que el roadmap pone en este punto. Es la misma relectura con la
que `4.4` corrigió la nota de `4.3`.

Y trae de regalo el dato que más se echaba en falta: **el motivo de la cancelación**. Vive en la
fila de la saga, `Order` no lo guarda —`Cancel()` no lo recibe desde `4.3`— y el `///` de aquel
método dejó escrito que una columna propia entraría *"si algún día la interfaz tiene que enseñarle
al cliente por qué se canceló su pedido… entonces con su caso de uso delante"*. El caso de uso está
delante, y la respuesta es que **no hace falta la columna**: el texto ya estaba persistido a un
`JOIN` de distancia.

### 4. Un recurso nuevo, no campos nuevos en `GET /orders/{id}`

`GET /orders/{id}/status` devuelve seis campos: `id`, `status`, `stage`, `cancellationReason`,
`createdAt` e `isFinal`. Sin líneas y sin total.

*Descartado* añadirle `stage` a `OrderResponse`, que habría sido una línea. Esto se sondea cada dos
segundos: con los campos allí, cada vuelta arrastraría las líneas del pedido y el total para mirar
un `string`. Y habría cambiado el cuerpo del 201 del alta, que tiene consumidores (`PlacedOrder` en
el frontend, `Orders.Tests`) para los que ese dato no significa nada.

*Descartado* traducir los nombres de estado a etiquetas de interfaz dentro del servicio. Se
devuelve el identificador de C# tal cual; traducirlo allí metería el vocabulario de la interfaz
dentro de la máquina de estados y obligaría a desplegar un servicio para cambiar una palabra.

### 5. `isFinal` mira `status` y no `stage`, y no es lo mismo

Son **dos relojes**. La saga llega a `Confirmed` y publica `OrderConfirmed`; el pedido solo se
mueve cuando uno de los dos consumers de `4.3` procesa ese mensaje, una entrega después. Calcular
`isFinal` desde la etapa pararía el sondeo con el pedido todavía en `Pending`, y la página se
quedaría enseñando "en curso" para siempre sobre un pedido que se confirmó un instante más tarde.

Lo calcula el **servicio** y no el cliente: la regla de cuándo un pedido está resuelto pertenece a
quien es dueño de `OrderStatus`, y repartirla entre el servicio y cada cliente es cómo se acaba
teniendo dos versiones de ella.

**Medido, y salió mejor de lo esperado**: el sondeo del pedido cancelado pilló
`stage: "CompensatingStock"` con `status: "Pending"` e `isFinal: false` — exactamente la disonancia
que esta decisión evita, en vivo y en la primera vuelta (ver Verificación 5).

### 6. LEFT JOIN, y con un `JOIN` normal la página mentiría feo

`Orders` y `OrderStates` comparten el valor de la clave primaria —el `OrderId` **es** el
`CorrelationId`— pero **no tienen clave foránea ni navegación entre ellas**: son dos cosas con dos
dueños, y el repositorio de saga es de MassTransit. Así que el `JOIN` se escribe a mano, en una
sola consulta proyectada al DTO, para no hacer dos viajes por vuelta de sondeo.

Es un **LEFT** JOIN porque un pedido puede existir sin instancia de saga: desde `4.5` el
`OrderCreated` se escribe en el outbox y sale hacia RabbitMQ un instante después. Con un `JOIN`
normal, un pedido recién creado daría **404** — la respuesta más confusa posible para alguien que
acaba de verlo crearse. Roto a propósito (Verificación 8): el síntoma fue exactamente ése.

### 7. `orders-route` se parte en dos, por MÉTODO

La deuda que `6.4` midió y dejó escrita: *"o el sondeo es mucho más lento, o `orders-route` se
parte en dos rutas con dos cupos"*. Se parte.

`orders-write-route` (`Methods: ["POST"]`, cupo `orders-write`, siguen 10/60 s) y
`orders-read-route` (`Methods: ["GET"]`, cupo `orders-read`, 60/60 s). Comparten cluster, path y
transform; **solo** cambian esas dos líneas.

60 es el mismo número que `catalog-read` a propósito: una lectura es una lectura. Lo que
justificaba los 10 nunca fue "es `/orders`", era que **cada POST arranca la saga entera**; consultar
en qué va un pedido es un `SELECT` de seis columnas. Con el sondeo a 2 s son 30 por minuto, o sea
la mitad del cupo.

*Descartada* una ruta específica para `/api/orders/{id}/status` dejando el resto como estaba: habría
partido el cupo por **recurso** en vez de por **coste**, y `GET /api/orders/{id}` —igual de barato—
se habría quedado en el cupo de escritura sin motivo. Además YARP no tiene precedencia por
especificidad entre dos rutas con el mismo path, así que haría falta un `Order` explícito que nadie
vigila.

**`Global` no se toca**: sigue en 120, que es mayor que 60, así que
`GlobalQuota_StaysAboveEveryRouteQuota` sigue en verde sin tocar nada.

**La consecuencia, dicha en voz alta**: con las dos rutas restringidas por método, un `PUT` o un
`DELETE` sobre `/api/orders/*` pasa a dar **404 en el Gateway** en vez de llegar al servicio. Hoy no
existe ninguno de los dos.

### 8. Dos pistas en paralelo, no una barra de progreso

**Es la decisión que más cambia lo que se ve, y la que el título del roadmap habría hecho mal.**

*Descartada* la barra lineal de tres pasos que ese título sugiere (Reservando stock → Procesando
pago → Confirmado). Es más bonita y **es falsa desde `4.9`**: aquel punto metió la validación de
precio de Catalog **en paralelo** con la reserva de stock, porque las dos salen del mismo
`OrderCreated` por el mismo fanout. De ahí que la máquina de estados tenga nombres compuestos como
`PricingPendingStockReserved`: no son ruido, dicen **qué rama ya contestó y cuál falta**.

Con una sola barra, un pedido en `PricingPendingPaymentCompleted` —cobrado, esperando a que Catalog
diga si el precio era auténtico— se pintaría como "confirmado" un instante antes de cancelarse.

Las dos pistas se llaman **"Precio (Catalog)"** y **"Stock y cobro (Inventory → Payments)"**,
nombrando a los servicios a propósito: es un proyecto para entender microservicios, y ver que
"comprobando el precio" significa "Catalog contestó" es la mitad de lo que esta página aporta. Junto
a ellas se enseña el **nombre real del estado de la saga**, que es lo que permite cruzar la pantalla
con `OrdersDb.OrderStates`, con las colas de RabbitMQ y con los logs de los cinco servicios.

### 9. El mapa de estados está DUPLICADO, y se dice

`Models/OrderProgress.cs` (servidor, primer render) y `wwwroot/js/order-status.js` (navegador,
vueltas siguientes) tienen el mismo `switch` de nueve estados.

*Descartado* que el servidor devolviera el HTML ya pintado en cada vuelta: gastaría el mismo cupo
para mandar marcado en vez de seis campos y, sobre todo, obligaría a que el sondeo pasara por
`Shop133.Web` — que es exactamente lo que arruina la partición por IP de la decisión 2.

*Descartado* también renderizar solo en el cliente y dejar la página vacía hasta la primera vuelta:
con eso, un navegador sin JavaScript no vería nada, y con él la página parpadearía dos segundos.

**El precio: si los dos mapas divergen, el síntoma es que la página cambia de texto sola en la
primera vuelta del sondeo, y no lo vigila nada.** Queda anotado en los dos archivos.

Los nombres de estado son literales en los dos sitios y no constantes compartidas: `Shop133.Web`
tiene cero `ProjectReference` —lo vigila `Frontend_DoesNotReference_ServicesOrGateway` desde `0.6`—
así que importar `OrderStateMachine` rompería la regla 3 en tiempo de compilación. Mismo caso que
`OrderItem.ProductSkuMaxLength` duplicando las constantes de `Product`.

### 10. Pedidos recientes en la sesión, y la invariante de `6.3` se relee

El navbar necesita adónde llevar, y sin autenticación no hay "mis pedidos". `RecentOrdersStore`
apunta en la sesión los pedidos tramitados desde este navegador.

*Descartado* un formulario donde pegar el `Guid`: funciona tras reiniciar el proceso, pero pedirle a
alguien que copie un identificador de 36 caracteres es una interfaz que nadie usaría.

**Esto es el SEGUNDO archivo del proyecto que toca `ISession`, y el `///` de `CartStore` dice ser el
único.** Merece decirse por qué no es una contradicción: aquella regla defendía una propiedad
concreta —que el **precio** no salga al navegador— y lo que la sostiene es que haya **un solo dueño
por cosa guardada**, no un solo archivo en total. Aquí además no hay nada que proteger: se guardan
identificadores de pedidos que ya existen y un total que Orders ya congeló.

Se pierde al reiniciar el proceso, igual que el carrito. **La página lo dice**, y dice también lo que
NO se pierde: los pedidos siguen en `Orders` y su página responde si alguien conserva el número.

### 11. La cuarta copia de `Unavailable(...)` decidió, como estaba escrito

`CheckoutController` dejó la regla por escrito —*"Tercera copia… **La cuarta decide**"*— siguiendo
el precedente de `SqlServerContainerFixture`, que `2.4` dejó duplicada diciendo que *"dos copias no
son un patrón"* y `3.7` extrajo al llegar a cuatro **con un `diff` delante**. El `OrdersController`
de este punto es la cuarta.

Y el `diff` dio lo que aquel precedente pedía comprobar: **las tres copias eran idénticas salvo el
texto del log**. Nada divergió en tres puntos.

Va a `Controllers/GatewayFailureExtensions.cs` como **método de extensión**. *Descartada* una clase
base: obligaría a quitarles el `sealed` y a reescribir los constructores primarios de los cuatro
controllers para heredar seis líneas.

**Lo que NO se movió es el código de estado**: sigue poniéndose ahí dentro. La decisión 3 de `6.2`
—que la vista no se devuelva con 200— no se revierte, porque el código es lo único que hace estas
ramas comprobables desde la línea de comandos.

### 12. `Gateway:PublicBaseUrl`, opcional, para el navegador

`Gateway:BaseUrl` es `http://127.0.0.1:5104` y está elegida para llamadas servidor-a-servidor:
`2.3` midió que rechazar una conexión en `localhost` cuesta 4,13 s —resuelve a `::1` **y** a
`127.0.0.1`— frente a 2,03 s con el literal. Al navegador esa optimización no le sirve, y hay un
caso en el que la hereda **rota**: con el frontend servido por `https`, un `fetch` a una URL `http`
es **contenido mixto** y el navegador lo bloquea sin más rastro que una línea en su consola.

Es **opcional** y cae en `BaseUrl` si falta. Eso **no es el `?? ""` que `6.2` rechazó**: aquello
enmascaraba una clave ausente y dejaba todas las páginas acusando a un proceso vivo. Esto es un
valor con significado —"si no se dice otra cosa, el navegador llega igual que el servidor"— y es
cierto en el único despliegue que hoy existe. Lo que sí se valida es la forma, porque una URL
relativa produciría peticiones contra el propio frontend y un 404 que acusaría al sitio equivocado.

### 13. El sondeo se PARA, y los tres topes enseñan algo

Cada 2 s, y para cuando: `isFinal` es `true`; o fallan **dos** peticiones seguidas; o se llegó a
**60 intentos** (~2 min).

El tope de intentos no es prudencia. Es la forma honesta de enseñar que **ni `PricingPending` ni
`CompensatingStock` tienen timeout** —un hueco sin dueño desde `4.4` y `4.9`—: un pedido atascado ahí
no se resuelve nunca, y sondearlo para siempre gastaría cupo en silencio. Al llegar al tope, la
página lo dice y ofrece recargar.

Dos fallos seguidos y no uno: uno suelto puede ser un corte de un instante; dos significa que el
Gateway no está, y seguir intentándolo cada 2 s no lo va a traer de vuelta.

El motivo de cancelación se pinta con **`textContent` y nunca `innerHTML`**: ese texto lo compone
Inventory concatenando una frase por línea que falló, o Payments con el importe. Viene de otro
servicio.

### 14. `_ViewImports` por carpeta, el primero del proyecto

`Views/Orders/_ViewImports.cshtml` importa `Shop133.Web.Orders` solo donde hace falta.

Es el criterio que el `_ViewImports` raíz ya tenía escrito: al importar `Shop133.Web.Gateway` anotó
el precio —*"ese tipo es `@model`-able desde CUALQUIER vista del proyecto para siempre"*— y al no
importar `Shop133.Web.Controllers` dijo que aquélla *"sí tenía alternativa, así que se tomó"*. Ésta
también la tiene.

---

## Cambios

### Nuevos

| Archivo | Rol |
|---|---|
| `src/Services/Orders/Orders.API/Models/OrderStatusResponse.cs` | El DTO de seis campos (decisiones 3, 4, 5). |
| `src/Frontend/Shop133.Web/Gateway/OrderStatus.cs` | Su espejo de cable en el frontend. |
| `src/Frontend/Shop133.Web/Orders/RecentOrder.cs` | Un recibo en sesión: id, fecha y total ya formateado. |
| `src/Frontend/Shop133.Web/Orders/RecentOrdersStore.cs` | Segundo dueño de la sesión (decisión 10). |
| `src/Frontend/Shop133.Web/Models/OrderProgress.cs` | El mapa de 9 estados → 2 pistas (decisiones 8, 9). |
| `src/Frontend/Shop133.Web/Models/OrderStatusViewModel.cs` | Lo que pinta el primer render. |
| `src/Frontend/Shop133.Web/Controllers/OrdersController.cs` | `Index` (lista) y `Status` (seguimiento). |
| `src/Frontend/Shop133.Web/Controllers/GatewayFailureExtensions.cs` | La extracción de la cuarta copia (decisión 11). |
| `src/Frontend/Shop133.Web/Views/Orders/_ViewImports.cshtml` | Import por carpeta (decisión 14). |
| `src/Frontend/Shop133.Web/Views/Orders/Index.cshtml` | La lista, con su estado vacío propio. |
| `src/Frontend/Shop133.Web/Views/Orders/Status.cshtml` | La página del punto. |
| `src/Frontend/Shop133.Web/Views/Orders/NotFound.cshtml` | 404 propio, distinto de "la tienda no está". |
| `src/Frontend/Shop133.Web/wwwroot/js/order-status.js` | **El primer JavaScript propio del proyecto.** |
| `tests/Services/Orders/Orders.Tests/OrderStatusEndpointTests.cs` | 6 tests, `Docker`. |

### Modificados

| Archivo | Cambio |
|---|---|
| `src/Services/Orders/Orders.API/Controllers/OrdersController.cs` | `GetStatus` con el LEFT JOIN; **se corrige el `[EndpointDescription]` de `GetById`**, que seguía diciendo *"El estado es siempre Pending hasta la Fase 4"*. |
| `src/Gateway/Shop133.Gateway/appsettings.json` | `orders-route` → `orders-write-route` + `orders-read-route`; nueva sección `RateLimiting:OrdersRead`. |
| `src/Gateway/Shop133.Gateway/Program.cs` | La política `orders-read`; se actualiza el comentario de `WithExposedHeaders` (ver Detalles). |
| `src/Frontend/Shop133.Web/Gateway/OrdersClient.cs` | `GetStatusAsync`; `Quota` se parte en `WriteQuota` y `ReadQuota`. |
| `src/Frontend/Shop133.Web/Program.cs` | Guarda de forma de `Gateway:PublicBaseUrl`; `AddScoped<RecentOrdersStore>()`. |
| `src/Frontend/Shop133.Web/appsettings.json` | La clave nueva, documentada con el caso de contenido mixto. |
| `src/Frontend/Shop133.Web/Controllers/CheckoutController.cs` | Apunta el pedido en la sesión tras el 201; usa la extensión. |
| `src/Frontend/Shop133.Web/Controllers/{Catalog,Cart}Controller.cs` | Solo el cuerpo del helper, sustituido por la extensión. |
| `src/Frontend/Shop133.Web/Views/Shared/_Layout.cshtml` | *Estado del pedido* deja de estar `disabled`. **Ya no queda ni un `nav-link disabled`.** |
| `src/Frontend/Shop133.Web/Views/Checkout/Placed.cshtml` | Botón *Seguir el pedido*; se reescribe el párrafo que prometía `6.5`. |
| `tests/Services/Orders/Orders.Tests/Infrastructure/OrdersApiFactory.cs` | `WithDbAsync`, para sembrar la fila de saga. |
| `tests/Gateway/Shop133.Gateway.Tests/GatewayRoutingTests.cs` | +1 test. |
| `tests/Gateway/Shop133.Gateway.Tests/GatewayRateLimitingTests.cs` | +2 tests, los dos sentidos del corte. |

---

## Detalles que cuestan tiempo

- **`MSB3027` mordió DOS veces en este punto.** Un servicio vivo bloquea su propio `.dll` y el build
  falla, pero el runner corre después el binario **viejo** y sale verde (`4.9` lo documentó). La
  primera vez el mensaje llegó entre 30 avisos `MSB3026` de reintento; la segunda, al romper el
  `JOIN` a propósito, el build dio `2 Error(s)` **sin una sola línea `error CS`** — todo eran copias
  de `apphost.exe`. Parar los servicios antes de compilar, y **leer el resultado del build antes que
  el de los tests**.
- **`Match.Methods` invertido NO rompe el enrutado, solo intercambia los cupos.** Al romperlo a
  propósito, los **nueve** tests de `GatewayRoutingTests` siguieron en verde: las dos rutas comparten
  path, cluster y transform, así que el backend recibe exactamente el mismo `path`. Lo único que
  cambia es qué cupo se gasta. Es un fallo que no se ve en ninguna respuesta correcta.
- **El destino del Gateway usa `localhost` y eso cuesta 4,13 s con el backend caído.** Con Orders.API
  parado, la página tardó **4,13 s** en rendirse — el mismo número que `2.3` midió para un rechazo en
  `localhost`, porque `ReverseProxy:Clusters:orders` apunta a `http://localhost:5189/`. Con el
  Gateway entero parado son **2,07 s**, el literal `127.0.0.1` del frontend. O sea que **el timeout
  de 5 s del frontend tiene 0,87 s de margen sobre un backend muerto detrás del Gateway**, no los
  ~3 s que sugería la medición de `6.2`.
- **`MapStaticAssets()` sirve desde un manifiesto de BUILD** (`6.2`): un `.js` nuevo sin recompilar
  da 404 con la ruta perfectamente escrita.
- **Razor codifica los acentos de una expresión `@(...)` y no los del marcado literal** (`6.2.1`).
  En la misma página conviven `l&#xED;mite` (viene del `cancellationReason`, que es una expresión) y
  `ningún pedido` (literal). Un `grep` que busque una de las dos formas falla en la otra.
- **Sin `-b`/`-c` en `curl.exe` cada petición es una sesión nueva**, así que la lista de pedidos sale
  siempre vacía y el total del pedido no aparece. Es lo que se usó para comprobar la rama del enlace
  compartido, pero es fácil confundirlo con un fallo.

---

## Verificación

Con `docker compose up -d`, los cinco servicios del IDE (`Catalog.API` en 5124, no el contenedor de
5125, que es adonde apunta el Gateway), el Gateway y `Shop133.Web` en su perfil `http`.

### 1. Compilación y suite de arquitectura

```
$ dotnet build -v:minimal
Build succeeded.
    0 Warning(s)
    0 Error(s)

$ dotnet tests\Shop133.ArchitectureTests\bin\Debug\net10.0\Shop133.ArchitectureTests.dll
   Shop133.ArchitectureTests  Total: 17, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.302s
```

No se añade regla: no se toca ningún `.csproj` ni entra ningún paquete (precedente `3.3`/`4.5`/`5.2`).

### 2. Las suites

```
   Shop133.Gateway.Tests  Total: 29, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.947s
   Orders.Tests           Total: 41, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 111.293s
   Inventory.Tests        Total: 15, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 112.707s
   Payments.Tests         Total:  9, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time:  76.538s
```

Gateway 26 → **29**, Orders 35 → **41**.

`Catalog.Tests` da **38 Total, 2 Failed**, y **no es de este punto**: ver la sección *Pendiente*.

### 3. El endpoint, directo y por el Gateway

El pedido feliz, sondeado cada 200 ms para pillar los estados intermedios:

```
{"id":"e6baee9c-...","customerEmail":"cliente@shop133.test","status":"Pending","total":498.00,...}

 1  {"id":"e6baee9c-...","status":"Pending","stage":"PricingPendingStockReserved","cancellationReason":null,"isFinal":false}
 2  {"id":"e6baee9c-...","status":"Confirmed","stage":"Confirmed","cancellationReason":null,"isFinal":true}
 3  {"id":"e6baee9c-...","status":"Confirmed","stage":"Confirmed","cancellationReason":null,"isFinal":true}
```

**`PricingPendingStockReserved` pillado en vivo** — el estado compuesto de `4.9`, que es la
justificación entera de las dos pistas. La saga se resuelve en menos de 400 ms.

### 4. El cupo, que es la deuda que `6.4` dejó aquí

```
=== 15 GET seguidos a /api/orders/{id}/status (antes de 6.5 el 11 era 429) ===
 1=200 2=200 3=200 4=200 5=200 6=200 7=200 8=200 9=200 10=200 11=200 12=200 13=200 14=200 15=200

=== y el POST sigue teniendo su cupo intacto ===
POST /api/orders -> 201
```

`6.4` midió que el `429` llegaba en la petición **11**. Ya no.

### 5. El pedido cancelado: compensación y motivo

3 unidades de `PLAY-005` a 399,00 = 1197,00, que supera el `Payments:DeclineAmountAbove` de 1000.

```
stock ANTES (onHand/reserved): 61/0
ID=8cd976c0-...  total=1197.00

 1  {"status":"Pending","stage":"CompensatingStock","cancellationReason":"el importe 1197.00 supera el límite autorizado de 1000.00","isFinal":false}
 2  {"status":"Cancelled","stage":"Cancelled","cancellationReason":"el importe 1197.00 supera el límite autorizado de 1000.00","isFinal":true}

stock DESPUES (onHand/reserved): 61/0
```

Tres cosas de una vez: la compensación devuelve las unidades, **el motivo sale por HTTP por primera
vez en el proyecto**, y la vuelta 1 es exactamente la disonancia de la decisión 5 —
`stage: CompensatingStock` con `status: Pending` e `isFinal: false`.

### 6. Las páginas

```
codigo: 200
data-order-id="8cd976c0-..."
data-gateway="http://127.0.0.1:5104"
data-poll="false"
Pedido cancelado
alert alert-danger
data-role="stage">Cancelled</code>
    <p data-role="reason">
        el importe 1197.00 supera el l&#xED;mite autorizado de 1000.00
    </p>
text-bg-danger
order-status.js?v=9dON59dH82H7ajTGNfeevfa1HbNtxHbJVkbGZSAh7vY
```

`data-poll="false"` porque ya está resuelto: se pinta y no se sondea.

El `.js` se sirve de verdad (el manifiesto de build de `6.2`):

```
js sin huella: 200
js con huella: 200
bytes: 12141
```

Y el navbar:

```
<a class="nav-link" href="/orders">Estado del pedido</a>
0
```

Ese `0` es la cuenta de `nav-link disabled` en la página: **ya no queda ninguno**. La fila de
promesas a medias que `6.1` dejó puesta se acabó aquí.

### 7. El recorrido de navegador entero, con frasco de cookies

```
carrito: Total</th>
--- tramitar ---
redirect: http://127.0.0.1:5025/checkout/placed/e359115b-...
--- pagina de confirmacion ---
href="/orders/status/e359115b-..."
Seguir el pedido
Seguir comprando
--- lista de pedidos (/orders) ---
list-group-item
href="/orders/status/e359115b-..."
Tramitado el 23/09/2026 09:43
$498.00
```

Con sesión y sin ella:

```
--- con sesion (recuerda el total) ---
data-poll="false"
Pedido confirmado
Precio (Catalog)
Stock y cobro (Inventory &#x2192; Payments)
<dt class="col-sm-3">Tramitado
<dt class="col-sm-3">Total
fw-semibold">$498.00
<dt class="col-sm-3">Estado interno

--- SIN sesion (enlace compartido: no hay total) ---
0

--- /orders sin sesion ---
alert alert-secondary
no se ha tramitado ningún pedido.
Ir al catálogo

--- 404 de un pedido inexistente ---
codigo: 404
```

### 8. Las dos roturas a propósito

**a) `Match.Methods` invertido.** Tres tests en rojo, los dos nuevos entre ellos:

```
   Shop133.Gateway.Tests  Total: 29, Errors: 0, Failed: 3, ...
      Expected: TooManyRequests
      Actual:   OK

Shop133.Gateway.Tests.GatewayRateLimitingTests.ExhaustingTheOrdersReadQuota_DoesNotAffectTheWriteQuota
Shop133.Gateway.Tests.GatewayRateLimitingTests.ExhaustingTheOrdersWriteQuota_DoesNotBlockReadingAnOrder
Shop133.Gateway.Tests.GatewayRateLimitingTests.OrdersWrite_BeyondItsQuota_Returns429
```

Y lo que **no** se enteró: los nueve `GatewayRoutingTests`, en verde. Ver *Detalles*.

**b) `DefaultIfEmpty()` fuera, o sea un `JOIN` normal.** Un solo test, exactamente el escrito para
ello, y con el síntoma predicho:

```
   Orders.Tests  Total: 6, Errors: 0, Failed: 1, ...
      Expected: OK
      Actual:   NotFound

OrderStatusEndpointTests.GetStatus_OrderWithoutASagaRowYet_ReturnsPendingWithNoStage
```

Un pedido recién creado, contestado como inexistente.

### 9. Restauración e invariantes

```
Build succeeded.
    0 Warning(s)
    0 Error(s)

   Shop133.Gateway.Tests  Total: 29, Errors: 0, Failed: 0, ...
   Orders.Tests           Total: 41, Errors: 0, Failed: 0, ...
```

```
PackageReference: 0  ProjectReference: 0
```

### 10. Las ramas de fallo

Con **Orders.API parado** (el Gateway sigue vivo y contesta 502):

```
  /api/orders/{id}/status -> 502
  pagina /orders/status/{id} -> 503 en 4.130791s

=== y el Index NO llama al Gateway, asi que sigue vivo ===
  /orders -> 200 en 0.010705s
```

Con el **Gateway entero parado**:

```
  pagina /orders/status/{id} -> 503 en 2.067427s
La tienda no está disponible ahora mismo
único Gateway
  /orders (sin Gateway) -> 200 en 0.003499s
```

Los dos números están en *Detalles*: 4,13 s es `localhost` dentro del Gateway, 2,07 s es el literal
del frontend. Y `/orders` contesta en milisegundos en los dos casos, porque no llama a nadie — la
misma propiedad que `6.3` anotó para `/cart`, y lo que hace que el botón *Reintentar* funcione.

---

## Pendiente

- **`Catalog.Tests` está en rojo (2 de 38) y NO es de este punto — es anterior y estaba tapado.**
  `GetProductsRequest.DefaultPageSize` vale **12** en `HEAD`, y `ProductsEndpointsTests` afirma
  **20**, que es el número que `6.2.1` documentó como decisión de la API (frente al 12 del frontend,
  *"dos decisiones de dos dueños"*). El test hardcodea el 20 a propósito — su comentario dice *"un
  test que importara la constante pasaría igual si alguien la cambiara a 5"*—, o sea que **la guarda
  funcionó y el rojo llevaba ahí desde antes**. Lo que lo escondía es el `bin/` rancio de
  `Catalog.Tests` (la trampa de `4.8`), y una compilación completa de la solución lo destapó.
  **Hay que decidir cuál de los dos números es el bueno**: si es 20, se revierte la constante; si es
  12, se actualizan los dos tests y el documento de `6.2.1`. **Sin dueño.**
- **Nada verifica que los dos mapas de estados coincidan** (decisión 9). Un estado nuevo en la saga,
  o un texto cambiado en uno solo de los dos archivos, se ve como que la página cambia de palabras
  sola en la primera vuelta del sondeo. El sitio natural sería una suite de `Shop133.Web`, que no
  existe.
- **La Fase 6 sigue sin punto de test en el roadmap**, así que nada de `6.1`–`6.5` está cubierto por
  el lado del frontend: `OrderProgress`, `RecentOrdersStore`, `OrdersClient.GetStatusAsync` y el
  `.js` entero son verificación a mano. Es el mismo hueco sin dueño que dejó `4.6`. Si se recoge, la
  forma es `Shop133.Gateway.Tests` (`WebApplicationFactory`, sin Docker, `Category=Fast`) y entonces
  `Shop133.Web` necesitaría el `public partial class Program { }` que `6.1` deliberadamente no
  añadió.
- **La partición por IP del rate limiter sigue sin ejercitarse**, ahora con una razón más para
  quererlo: la decisión 2 se justifica precisamente en que cada visitante sea su propia partición, y
  eso no lo comprueba nada — bajo `TestServer` no hay `RemoteIpAddress`. Lo heredó `5.4` y sigue sin
  dueño.
- **`CreateOrderTests.cs` tiene 2 avisos `xUnit1051` preexistentes** (líneas 248 y 282), sobre
  `Harness.Published` y `Harness.InactivityTask`. No son de este punto y no se tocan, pero una
  compilación limpia de `Orders.Tests` los enseña y conviene saber que ya estaban.
- **Ni `PricingPending` ni `CompensatingStock` ni `CancellingStockPending` tienen timeout.** El tope
  de 60 intentos del sondeo (decisión 13) lo **enseña**, no lo arregla: un pedido atascado ahí sigue
  atascado, solo que ahora se ve. **Sigue sin dueño** desde `4.4` y `4.9`.
- **Si Catalog rechaza el precio después de que Payments haya cobrado, el cargo no se devuelve.**
  La página lo pinta como cancelado sin más, porque es lo que el sistema sabe. Hueco de `4.9`, sin
  dueño.
- **La cabecera `Location` del 201 sigue rota** y este punto vuelve a esquivarla leyendo el id del
  cuerpo. **Sigue sin dueño** desde `5.1`.
- **`Gateway:PublicBaseUrl` no se ha ejercitado con `https`.** La verificación entera va por el
  perfil `http`. El caso de contenido mixto está razonado y documentado, no medido.
