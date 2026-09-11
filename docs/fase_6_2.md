# Fase 6.2 — Vista de catálogo: grid de productos con `card` de Bootstrap

**Fecha:** 2026-09-10 · **Estado:** completado · [Roadmap](../plan-desarrollo-shop133.md)

> **Corregido el 2026-09-11 por [`6.2.1`](fase_6_2_1.md)**, que añadió a `Catalog.API` la
> paginación, el filtro por `categoryId` y el recuento por categoría que este punto tuvo que hacer
> en memoria. Este documento describe el estado final; lo que decía antes de ese cambio está en el
> historial de git.

---

## Objetivo

Convertir `Shop133.Web` en algo que **habla con el backend**. Hasta `6.1` el frontend sabía
renderizarse y nada más: cero `PackageReference`, cero `ProjectReference`, ninguna URL en su
configuración y tres enlaces del navbar declarados `disabled` a la espera de quien los sirviera.

Este punto activa el primero de esos tres y, al hacerlo, **estrena la regla 3 en su forma
activa**. Hasta hoy la regla se cumplía *por ausencia* —el frontend no llamaba a nadie—; a
partir de aquí se cumple porque llama **solo al Gateway**: en todo el proyecto no aparece el
puerto de ningún servicio, ni 5124 (Catalog) ni 5189 (Orders), solo `Gateway:BaseUrl`.

El alcance se acordó por encima del título del roadmap, que solo pide el grid:

1. **El grid** de las 50 cards.
2. **Filtro por categoría.** No es alcance inventado: el `///` de `CategoriesController` dice
   por escrito desde `1.4` que ese endpoint existe *"para que la vista de catálogo de 6.2 pueda
   pintar el filtro por categoría"*. Sin este punto se quedaba sin ningún consumidor.
3. **Página de detalle** sobre `GET /api/catalog/products/{id}`, que le da sitio natural al
   "Añadir al carrito" de `6.3`.
4. **Las 50 imágenes**, generadas de verdad. El seed de `1.4` publica
   `ImageUrl = /img/products/<sku>.jpg` en las 50 filas y **en todo el repositorio no había ni
   un archivo de imagen**, así que sin esto las 50 cards daban 404.

**No entra ningún paquete.** `Microsoft.Extensions.Http` está en el framework compartido
—comprobado en el ref pack, no supuesto—, así que `AddHttpClient<T>` viene con el Web SDK y
**`Shop133.Web.csproj` sigue con cero `PackageReference` y cero `ProjectReference`**.

**Fuera de alcance, deliberadamente:** el carrito (`6.3`), el checkout (`6.4`), el estado del
pedido (`6.5`), Polly (`6.6`), los toasts (`6.7`), un `Dockerfile` para el frontend, y
**cualquier cambio en `Catalog.API`** — este punto lo necesitaba y decidió no tocarlo (ver la
decisión 6), y esa deuda la recogió [`6.2.1`](fase_6_2_1.md) al día siguiente. Tampoco entra ni un
test — ver la última decisión.

La suite de arquitectura se queda en **17** y el repositorio en **131**.

---

## Decisiones

### 1. Cliente tipado, en una carpeta llamada `Gateway/` y no `Services/`

`CatalogClient` se registra con `AddHttpClient<CatalogClient>`. *Descartado* un `HttpClient`
nuevo por petición (agota los sockets, que se quedan en `TIME_WAIT`) y uno `static` (no se
entera nunca de un cambio de DNS); un cliente **con nombre** devolvería la URL base al
controller, que es justo el acoplamiento que el tipado quita de en medio.

Lo que sí se decidió y no es obvio es **la carpeta**. `2.3` nombró la suya por el *destinatario*
(`Orders.Infrastructure/Catalog/`), y aquí el destinatario es el Gateway — que por la regla 3 es
el **único** que este proyecto puede tener nunca. Una carpeta `Gateway/` hace visible esa regla
en el propio árbol de archivos: una sola carpeta de salida, cuyo nombre es el único destino
permitido, y el `OrdersClient` de `6.5` aterriza dentro.

*Descartado* `Services/`, que era lo primero que salía: es literalmente la palabra que la regla 3
le prohíbe conocer al frontend, invita a que aparezca al lado un `CatalogClient` apuntando a
`:5124`, y —la señal— el test de arquitectura que vigila este proyecto se llama
`Frontend_DoesNotReference_ServicesOrGateway`, así que una carpeta `Services/` dentro del
frontend se lee como una contradicción de un vistazo.

### 2. La clave es la URL del **Gateway**, no la del catálogo, y la sección se llama `Gateway`

`Gateway:BaseUrl` en `appsettings.json`, **no** en User Secrets, porque no es un secreto
(precedente de `Services:CatalogBaseUrl` en `2.3` y `Payments:DeclineAmountAbove` en `3.5`).

*Descartado* `Catalog:BaseUrl = "http://127.0.0.1:5104/api/catalog"`: `6.5` necesita
`/api/orders/...` del **mismo** Gateway y tendría que inventar una segunda clave o recomponer el
host a mano. El prefijo público `catalog/` lo pone el cliente, y que el frontend lo conozca no
rompe la regla 3 — ese prefijo es el contrato **público** del Gateway (`5.1`), no la dirección de
un servicio.

*Descartado también* `Services:GatewayBaseUrl`, la transposición literal de la clave de `2.3`.
`Services:` es una sección **plural**, y su forma es una invitación: la siguiente clave debajo es
`Services:CatalogBaseUrl`, que es la regla 3 rota por autocompletado. Una sección llamada
`Gateway` solo puede ganar propiedades *del Gateway*. El nombre de la clave es el sitio más
barato donde codificar esa regla.

### 3. Hay guarda, y el motivo es que la versión "defensiva" mentiría

El repositorio tiene los dos precedentes, así que había que decidir en vez de copiar: guarda en
cada `ConnectionStrings:*` y en las tres del Gateway, **sin** guarda en
`Payments:DeclineAmountAbove` *"porque tiene un valor por defecto sensato y su ausencia no deja
el servicio a medias"*.

Aquí sí lo deja, y peor que en `2.3`: con la clave ausente, `new Uri(null!)` lanza un
`ArgumentNullException` que nombra **un parámetro del framework y no una clave de
configuración**. Y la forma tentadora —`?? ""`— sería activamente dañina: `BaseAddress` se
quedaría en `null`, cada petición se volvería relativa, `HttpClient` lanzaría
`InvalidOperationException`, el `catch` lo leería como indisponibilidad y **todas las páginas
dirían "el Gateway no responde" señalando a un proceso perfectamente vivo**. Un diagnóstico
seguro de sí mismo y equivocado es peor que ninguno.

Se valida además que sea **absoluta**, como hace `5.3` con `Cors:AllowedOrigins`. Lo que **no**
se copia de allí es el rechazo de la barra final: aquella comparación es literal contra la
cabecera `Origin`, y esto es una *base address*, que la quiere — se normaliza con `TrimEnd('/')`.

### 4. `127.0.0.1` y no `localhost`, y esta vez se nota delante de un usuario

`2.3` midió que un rechazo de conexión en `localhost` cuesta **4,13 s** —resuelve a `::1` **y** a
`127.0.0.1`, así que una sola petición loguea *dos* `SocketException`— frente a **2,03 s** con el
literal. Con el `Timeout` fijado en 5 s, `localhost` se comería el 83 % del presupuesto de fallo
**en cada render y delante de una persona esperando**.

`2.4` ya había elegido el literal por lo mismo en `CatalogStub.Url`; esta es la segunda vez y la
primera de cara al usuario. *Descartado* `localhost`, cuyo único argumento es la simetría con los
`Address` de los clusters del Gateway — pero nadie mira una pestaña del navegador mientras esos
resuelven, y copiar la *forma* de un valor sin su *motivo* es lo que la decisión 7 de `6.1` se
negó a hacer con `UseHttpsRedirection()`.

**Medido en este punto: `/catalog` con el Gateway parado devuelve `503` en 2,16 s.** La decisión
queda comprobada y no afirmada.

### 5. Dos llamadas por render, en paralelo — y `Task.WhenAll` aquí sí, donde `2.3` lo rechazó

`Index` pide productos **y** categorías con `Task.WhenAll`.

*Descartado* derivar el filtro de los propios productos con un `GroupBy` sobre `CategoryId` +
`CategoryName`, que ambos vienen en cada `ProductResponse` desde `1.4`. Ahorra una llamada
—y **el doble de cupo**, ver abajo—, pero deja `GET /categories` **sin ningún consumidor en todo
el sistema**, justo en el punto para el que su `///` dice que existe. Se prefiere que la lista de
categorías la mande quien manda sobre ellas. El recuento se calculaba aquí sobre los productos ya
traídos —lo cual solo funcionaba porque se traía el catálogo entero— y desde
[`6.2.1`](fase_6_2_1.md) lo manda ese mismo endpoint en un `productCount`, que es lo único que
sigue dando la respuesta correcta con el listado paginado.

Sobre el paralelismo: `2.3` hizo sus llamadas **secuenciales a propósito**, para que el coste de
que un servicio llame a otro se viera. Eso era acoplamiento **entre servicios**, que es el dolor
que aquella fase existía para enseñar. Esto es frontend → Gateway, que es el acoplamiento que la
regla 3 **manda** tener: no hay nada que hacer visible, y esconderlo no enseña nada.

**El coste, dicho con números porque es `5.2` medido desde el otro lado:** 2 llamadas por render
contra el cupo `catalog-read` de 60/60 s, y como este MVC **renderiza en servidor**, todos los
visitantes son **una sola IP** para el Gateway. Salen ~30 vistas de página por minuto para la
máquina entera. Medido: el primer `429` llegó en el render **#30**, exactamente.

### 6. El filtro se aplaza a la API, y este punto se queda haciéndolo en memoria a sabiendas

`GET /products` **no aceptaba ni un parámetro de consulta** cuando se escribió este punto — ni
`categoryId`, ni paginación, ni orden. El `///` de `ProductsController` incluso nombraba a `6.2`
como el punto que añadiría paginación *"si la necesita"*.

Se necesitaba, y aun así **se decidió no tocar `Catalog.API` aquí**: `6.2` es un punto de frontend,
y tocar un servicio para servir a una vista es inventar alcance. El precio, asumido por escrito en
su día: cada render se traía el catálogo **entero** y descartaba 40 de las 50 filas para enseñar una
categoría, y cada clic en un chip lo repetía. Con 50 filas semilla son ~14 kB y no se nota; con 500
deja de ser aceptable.

Esa deuda quedó en el *Pendiente* de este documento como *"un punto de la Fase 1 que nadie tiene
asignado"*, y **la recogió [`6.2.1`](fase_6_2_1.md)**: desde entonces el filtro y el recorte los hace
la API, y de este controller desaparecieron un `Where` y un `GroupBy` sobre cincuenta filas que ya no
viajan. Lo que este punto dejó decidido —y `6.2.1` no cambió— es todo lo demás: que el filtro viva en
la URL, cómo se pintan los chips y qué pasa con un `categoryId` desconocido.

**Re-render en servidor con `?categoryId=N`**, no toggle en JavaScript. Funciona con JS
desactivado; el filtro vive en la URL (compartible, y el botón atrás funciona); y `site.js` sigue
vacío a propósito hasta `6.7`, así que meter aquí el primer script del proyecto gastaría aquella
decisión por adelantado. El toggle en cliente tampoco sería más barato: las 50 filas llegan
igual, y esconder 40 con una clase se lleva a JavaScript el estado vacío, el recuento por
categoría y el chip activo, que no se simplifican allí.

Un `categoryId` inexistente enseña el estado vacío y **no devuelve 404**. La regla de `2.3` —un
valor malo en el cuerpo es 400, en la URL es 404— no aplica: una cadena de consulta es un
**filtro sobre un recurso**, no la identidad del recurso.

### 7. Dos tipos y no tres: el DTO de cable y el view model de la página

- `CatalogProduct` / `CatalogCategory` (en `Gateway/`) — la forma del **JSON**.
- `CatalogIndexViewModel` (en `Models/`) — la forma de **lo que la página pinta**.

El motivo de que el view model exista **no es que los campos del DTO estén mal**: es que la
página tiene tres piezas de estado —los productos visibles, los chips y cuál está activo— y dos
de las tres no las manda la API en ningún sitio. Ese es el trabajo que hace.

*Descartado* un `ProductViewModel` que copiara los nueve campos: serían nueve asignaciones y ni
una decisión, o sea el *passthrough* que `1.3` rechazó al no meter un repositorio sobre un CRUD,
e inventar la forma antes del caso de uso, que es lo que `1.1` rechazó con `Product.Update()` y
`2.1` con `Order.Confirm()`. Se ganará su sitio el día que la card necesite algo que el cable no
trae — probablemente `6.3`, con su cantidad y su token antiforgery.

*Descartado* fundir los dos en uno: el contraargumento ya está escrito en este repositorio, en el
`///` del propio `ProductResponse` —*"hoy los campos coinciden uno a uno y el tipo parece
redundante, pero la entidad es el modelo de persistencia"*—. Aquí es el modelo de **cable**, y
`Catalog` arrastra `PreviousPrice` y `PriceChangedAt` desde `4.8` sin exponerlas: el día que las
exponga, un tipo fundido las pondría delante de un usuario sin que nadie lo decidiera.

`CatalogProduct` re-declara **los nueve** campos, al revés que el `CatalogProduct` de `2.3` que
tomó 4 de 9: aquí la card pinta siete y la ficha los pinta todos. Los `required` no son
decoración — `3.1` midió que un cuerpo al que le falta una propiedad requerida lanza
`JsonException` nombrándola, en vez de materializar un producto de 0,00.

**`Details.cshtml` toma el `CatalogProduct` directamente**, sin view model: uno de una sola
propiedad sería, otra vez, inventar la forma antes del caso de uso.

### 8. El fallo se pinta en una vista propia, con `503` — y no con el `502` de `2.3`

Gateway caído, timeout o no-2xx → **no** se deja subir la excepción. `Program.cs` registra
`UseExceptionHandler("/Home/Error")` **solo cuando no es Development**, así que en el entorno en
el que este proyecto se ejecuta de verdad una `HttpRequestException` sería la página amarilla de
excepción — y "el Gateway no está levantado" parecería un bug de `Shop133.Web`, en su página
principal.

**`503` y no `502`, y la diferencia está en la premisa, no en el gusto.** `2.3` eligió 502 con
este argumento: *"Orders está vivo, su dependencia no, y el 502 hace que el lector se pregunte
qué hay detrás de Orders"*. Allí Orders actuaba de **intermediario** de Catalog dentro de la
misma petición, que es literalmente lo que el 502 describe. `Shop133.Web` no proxea nada — es el
servidor de origen, y lo que no puede es **construir su página**, que es lo que el 503 dice.
Quedarse con la conclusión de `2.3` y tirar su premisa es exactamente lo que la decisión 7 de
`6.1` se negó a hacer.

**El código de estado importa aunque el navegador pinte el HTML igual**: es lo único que hace la
rama **comprobable desde la línea de comandos**. Un `200` con un cartel dentro no se distingue de
una página que funciona con ningún comando, y `5.4` y `6.1` verifican los dos por código de
estado.

*Descartado* renderizar `Index` con un `alert` y el grid vacío: mete dos estados mutuamente
excluyentes en una vista (chips derivados de productos que no existen) y dejaría mal el texto del
estado vacío.

### 9. El `429` tiene rama propia, y es lo primero del proyecto que enseña el rate limiting

Con 2 llamadas por render y 60/min, un paseo por la demo **agota el cupo de verdad** — medido,
render #30. Meterlo en un genérico *"el catálogo no está disponible"* tiraría el `Retry-After`
que `5.2` se molestó en calcular y `5.3` en exponer.

La rama dice qué cupo se superó, por qué todos los visitantes cuentan como una sola IP, y en
cuántos segundos reintentar. Ojo con lo que `5.2` midió: ese valor es **la ventana entera**, no
lo que queda de ella, así que siempre son 60 s y es conservador.

### 10. La card va en línea, sin partial

*Descartado* `_ProductCard.cshtml`: hoy tiene **un solo** llamante. `Details` no lo reutiliza —una
ficha es otra maquetación, no una card grande—. Precedente explícito de `2.4`, citado tres veces
en `CLAUDE.md`: *dos apariciones no son un patrón*. **Lo decide `6.3`**, que es cuando la card
gana su primer motivo de verdad para ser un componente: un formulario, una cantidad y un token.

Anotado en el marcado para que no cueste una hora: `stretched-link` hace clicable la card entera
dejando **un solo** enlace en el árbol de accesibilidad, y **un segundo elemento interactivo
dentro de la card lo rompe** — el botón de `6.3` no responderá si se añade sin quitarlo.

### 11. El recorte de la descripción va en `site.css`, no en un CSS aislado por vista

Se recorta por **línea renderizada** con `line-clamp`, no por número de caracteres.
*Descartado* `Description[..90]` en el controller o en un view model: corta a mitad de palabra
(y de grafema — el seed va lleno de vocales acentuadas y de eñes), es una decisión de
**presentación** tomada donde se traen los datos, no se adapta (una card a 4 columnas y otra a 1
caben cosas muy distintas del mismo texto) y **deja una mentira en el DOM**, porque el texto
recortado es lo que recibe un lector de pantalla. Bootstrap 5.3 no trae utilidad para esto.

Lo que costó decidir es **dónde**. El CSS aislado por vista (`Views/Catalog/Index.cshtml.css`)
**sí** alcanzaría a este `<p>`: lo que `6.1` midió es que un elemento renderizado por un *tag
helper* sale sin el atributo `b-<hash>`, y este `<p>` es marcado literal. Aun así va a
`site.css`, por dos razones: el modo de fallo del CSS aislado es **silencioso** —archivo servido,
CSS válido, selector que no engancha con nadie, cero avisos— y **el ámbito se mueve con el
archivo**, así que el día que `6.3` saque la card a un partial la regla dejaría de aplicarse sin
que nada lo dijera. Es la decisión 4 de `6.1` aplicada por segunda vez.

### 12. El precio se formatea con una cultura explícita, y no es cosmético

Este proceso **no configura localización en ninguna parte** —no hay `UseRequestLocalization`—,
así que `CultureInfo.CurrentCulture` es la que diga el Windows de quien ejecute. Con un
`ToString("C")` pelado, **el mismo código pinta `$249.00`, `249,00 €` o `249,00 ¤` según la
máquina**, y el marcador dejaría de poder verificarse.

Hay un segundo motivo, medido en `3.3` y `4.8`: un `decimal` **pierde los ceros finales** al
viajar por JSON (se publicó `249.00` y llegó `"249"`), así que un `@product.Price` crudo pintaría
`249`. El formato de dos decimales los devuelve.

*Descartado* el sufijo explícito (`249.00 MXN`), que quita toda ambigüedad frente al dólar pero
lee a factura y no a tienda; el seed de `1.4` son souvenirs mexicanos y `$249.00` es lo que
enseña una tienda mexicana.

### 13. El controller **no** lleva `[ApiController]`, y se dice en voz alta

La sección *Conventions* de `CLAUDE.md` dice que los controllers llevan `[ApiController]` +
`[Route("[controller]")]`. **Esa convención se escribió para los controllers de API de los cinco
servicios y no debe aplicarse aquí**: `[ApiController]` convierte un fallo de binding en un
`400 ProblemDetails` **en vez de** en un `ModelState` inválido, que es justo lo que `6.4` necesita
para sus formularios, e implica `[FromBody]`, que no significa nada en una página. `HomeController`
ya no llevaba ninguno de los dos, así que esto sigue la forma del frontend, no inventa una.

Se llama `CatalogController` y no `ProductsController`, saltándose la convención del plural: esa
vale para un **recurso**, y esto es una **página**. El nombre coincide con el título del roadmap,
con la etiqueta del navbar, con el prefijo del Gateway y con el `CartController` de `6.3`.

`LowercaseUrls` entra —una línea, la misma que Catalog (`1.5`) y Orders (`2.3`)—. El enrutado ya
era *case-insensitive*; lo que arregla es la URL **generada** por los tag helpers, que sin ella
sale `/Catalog/Details/5` mientras los otros dos proyectos web sirven en minúsculas. `6.3` y
`6.5` heredan la decisión en vez de reabrirla cada uno.

### 14. Las 50 imágenes se generan, y sale una 51

El seed publica las 50 rutas y no había ni un archivo. *Descartado* un fallback `onerror` a un
placeholder, que es más barato pero deja el catálogo enseñando 50 huecos grises; *descartado*
quitar el `<img>` y pintar un tile de CSS, que ignoraría un campo que el catálogo sí publica.

Se generan con `System.Drawing` desde Windows PowerShell 5.1: 400×300 (4:3), un color plano por
categoría y el SKU encima. **Son 51 y no 50**: `ImageUrl` es anulable en origen, así que un
producto creado por `POST /products` puede no traer imagen, y ese camino `null` lo esconde el
seed. La 51 lo cubre.

El script **no se commitea** —no hay carpeta `scripts/` ni `tools/` en el repositorio y crear una
necesita aprobación— y vive entero en la sección de Verificación de este documento, que es donde
este proyecto ya guarda sus comandos reproducibles. Peso: **323 kB los 51 archivos**.

### 15. Ni un test, y se dice por escrito

Suite de arquitectura en **17** y repositorio en **131**, sin moverse, con el precedente de
`3.3`, `3.5`, `4.5`, `5.2`, `5.3` y `6.1`: no entra paquete, ni `ProjectReference`, ni forma
estructural nueva que vigilar, y una regla que nunca engancha es lo que `3.2` rechazó.

**Sigue sin añadirse `public partial class Program { }`**, por la decisión 9 de `6.1`: la Fase 6
no tiene ningún punto de test en el roadmap, y añadir la línea declararía una superficie de test
que no existe.

*Descartada* la regla de arquitectura que apetecía —*"el frontend tiene exactamente una URL base
y es la del Gateway"*—: obligaría a `ProjectGraph` a leer `appsettings.json`, una capacidad que no
tiene y que `5.3` y `5.4` ya decidieron que vive en la suite del proyecto y no en la de
arquitectura. `Shop133.Web` no tiene suite. Ver *Pendiente*.

---

## Cambios

### Nuevos

| Archivo | Rol |
|---|---|
| `src/Frontend/Shop133.Web/Gateway/CatalogClient.cs` | El único camino de salida del proyecto. Timeout de 5 s, 404 antes que `IsSuccessStatusCode`, filtro de cancelación. |
| `src/Frontend/Shop133.Web/Gateway/CatalogProduct.cs` | Los 9 campos de `ProductResponse`, re-declarados. |
| `src/Frontend/Shop133.Web/Gateway/CatalogCategory.cs` | `Id` + `Name`. |
| `src/Frontend/Shop133.Web/Gateway/GatewayUnavailableException.cs` | Lleva dentro el código de estado y el `Retry-After`. |
| `src/Frontend/Shop133.Web/Controllers/CatalogController.cs` | `Index(int? categoryId)` y `Details(int id)`. |
| `src/Frontend/Shop133.Web/Models/CatalogIndexViewModel.cs` | Productos, chips y cuál está activo. |
| `src/Frontend/Shop133.Web/Models/Money.cs` | Formato de importe con cultura explícita. |
| `src/Frontend/Shop133.Web/Views/Catalog/Index.cshtml` | Filtro + grid + los dos estados vacíos. |
| `src/Frontend/Shop133.Web/Views/Catalog/Details.cshtml` | Ficha a dos columnas. |
| `src/Frontend/Shop133.Web/Views/Catalog/Unavailable.cshtml` | El aviso de caída y el de cupo. |
| `src/Frontend/Shop133.Web/Views/Catalog/NotFound.cshtml` | El 404 presentable de un producto. |
| `src/Frontend/Shop133.Web/wwwroot/img/products/*.jpg` | **51 archivos**, 323 kB. |

### Modificados

| Archivo | Cambio |
|---|---|
| `src/Frontend/Shop133.Web/Program.cs` | Guarda de `Gateway:BaseUrl`, `AddHttpClient<CatalogClient>`, `LowercaseUrls`. |
| `src/Frontend/Shop133.Web/appsettings.json` | Sección `Gateway`, con comentarios `//` como los del Gateway. |
| `src/Frontend/Shop133.Web/Views/_ViewImports.cshtml` | `@using Shop133.Web.Gateway`. |
| `src/Frontend/Shop133.Web/Views/Shared/_Layout.cshtml` | `Catálogo` deja de estar `disabled`. |
| `src/Frontend/Shop133.Web/Views/Home/Index.cshtml` | El CTA pasa de `<button disabled>` a `<a>`. |
| `src/Frontend/Shop133.Web/wwwroot/css/site.css` | La regla `.product-description`. |

`Shop133.Web.csproj` y `launchSettings.json` **no se tocan**. Lo segundo importa: `5025` y `7227`
son exactamente los dos orígenes de `Cors:AllowedOrigins` del Gateway (`5.3`), así que mover un
puerto rompería aquel punto y dos tests de `Shop133.Gateway.Tests`.

---

## Detalles que cuestan tiempo

- **`MapStaticAssets()` sirve desde un manifiesto de BUILD, no del sistema de archivos.** Un
  archivo dejado en `wwwroot/` *después* de compilar **no tiene endpoint y devuelve 404**, al
  revés que el viejo `UseStaticFiles()`. Generas las 51 imágenes, recargas, ves 51 huecos y te
  pones a depurar las rutas — que están bien. El arreglo es `dotnet build`. Comprobado:
  `Shop133.Web.staticwebassets.endpoints.json` acaba con **102** endpoints para 51 archivos (la
  ruta normal y la de huella digital de cada uno), que es lo que confirma que la ruta **sin**
  huella —la que publica la API, y que no pasa por ningún tag helper— se sirve igual.
- **El remedio de Smart App Control cambia el *content root*, y la guarda del Gateway saltó con
  la configuración perfecta.** Smart App Control bloqueó el apphost `Shop133.Gateway.exe`
  (`An Application Control policy has blocked this file`), así que se aplicó el remedio de `3.7`:
  lanzar el `.dll` con `dotnet`, que está firmado por Microsoft. Pero `WebApplication.CreateBuilder`
  toma el content root del **directorio actual**, así que lanzarlo desde la raíz del repositorio
  hace que **`appsettings.json` no se encuentre** y el Gateway muere con
  *"Falta la configuración 'ReverseProxy:Routes'"*. La guarda estaba haciendo su trabajo; el
  diagnóstico era el equivocado. Hay que lanzarlo **desde la carpeta del proyecto**. Es la misma
  familia que *"User Secrets solo cargan en Development"* de `3.4`.
- **Un servicio con la salida redirigida a un archivo se ATASCA, y el síntoma es un
  `Empty reply from server` sin un solo error.** `Catalog.API` arrancado con `dotnet run` y la
  salida redirigida aceptaba la conexión, leía la petición y la cerraba sin contestar; `curl`
  devolvía `000` con código de salida 52 y el proceso decía estar vivo y `Responding`. La
  prueba: **el archivo de log dejó de crecer** (clavado en 4652 bytes) con el proceso al 0 % de
  CPU — bloqueado escribiendo en una tubería que nadie vacía. El telemetry de MassTransit vuelca
  trazas de pila enormes y la llena. No es un fallo del código: el mismo `Catalog.API` en
  contenedor contestaba `200`. Se verificó contra el contenedor, apuntando el Gateway con
  `ReverseProxy__Clusters__catalog__Destinations__primary__Address`, que es el patrón de `1.6`
  que `5.1` dejó preparado justo para esto.
- **`curl.exe` en PowerShell devuelve un ARRAY de líneas, no una cadena.** Así que
  `$html.Contains("card-img-top")` hace comparación **por elemento** y devuelve `False` con el
  marcador perfectamente presente — una tanda entera de comprobaciones salió "ausente" por esto,
  mientras `[regex]::Matches` sobre la misma variable encontraba 50 coincidencias (porque esa sí
  coacciona el array a cadena). Hay que unir con `-join "`n"` antes de comparar. Quinta variante
  de la familia PowerShell/`curl.exe` que este repositorio lleva anotando desde `3.4`.
- **`$home` es una variable de SOLO LECTURA en PowerShell.** Asignarle el HTML de la portada
  falla con `Cannot overwrite variable HOME`, la asignación no ocurre, y la comprobación que
  venía detrás da un falso "AUSENTE" sobre una variable vacía. El error sale **antes** que el
  resto de la salida, así que es fácil leerlo como ruido.
- **Todos los `Dispose()` del script de imágenes son estructurales.** `Bitmap.Save` retiene el
  manejador del archivo hasta que se libera el `Bitmap`; sin eso, la segunda pasada muere con
  *"A generic error occurred in GDI+"* — que es además el mensaje de GDI+ para "el directorio no
  existe", "la ruta es relativa a otro sitio" y media docena de causas más. No nombra nunca la
  real.
- **El `ToLowerInvariant()` de los nombres de archivo no es cosmético.** Windows no distingue
  mayúsculas, así que `TAZA-001.jpg` funcionaría en local y daría 404 el día que el frontend
  tenga contenedor sobre Linux. El `ImageUrl` del seed está en minúsculas y los archivos tienen
  que estarlo también. Se comprueba con un `Compare-Object` contra el propio `CatalogSeedData.cs`,
  no contra la suposición del bucle.
- **El `429` llegó en el render #30 y la aritmética cuadra exactamente**: 2 llamadas por render
  × 30 = 60 = el cupo, habiéndose gastado las dos primeras en la petición de calentamiento. No es
  una estimación.

---

## Verificación

### 1. Build limpio

```
> dotnet build src\Frontend\Shop133.Web\Shop133.Web.csproj
  Shop133.Web -> C:\personalprojects\shop133\src\Frontend\Shop133.Web\bin\Debug\net10.0\Shop133.Web.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### 2. Sigue sin un solo paquete ni referencia — la afirmación central del punto

```
> dotnet msbuild src\Frontend\Shop133.Web\Shop133.Web.csproj -getItem:PackageReference -getItem:ProjectReference
{
  "Items": {
    "PackageReference": [],
    "ProjectReference": []
  }
}
```

### 3. Generación de las 51 imágenes

El script completo (Windows PowerShell 5.1, **no** `pwsh`):

```powershell
Add-Type -AssemblyName System.Drawing

$root = "src\Frontend\Shop133.Web\wwwroot\img\products"
New-Item -ItemType Directory -Force -Path $root | Out-Null

$palette = [ordered]@{
    'TAZA' = '#4A6FA5'; 'LLAV' = '#8A6552'; 'PLAY' = '#4F7942'
    'PINS' = '#9B5D73'; 'LIBR' = '#5F5B8B'
}

$codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() |
         Where-Object { $_.MimeType -eq 'image/jpeg' }
$encoderParams = New-Object System.Drawing.Imaging.EncoderParameters 1
$encoderParams.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter(
    [System.Drawing.Imaging.Encoder]::Quality, [long]82)

function New-Placeholder {
    param([string]$Text, [string]$Hex, [string]$Path)

    $bitmap   = New-Object System.Drawing.Bitmap 400, 300
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
    $graphics.Clear([System.Drawing.ColorTranslator]::FromHtml($Hex))

    $band = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(46, 0, 0, 0))
    $graphics.FillRectangle($band, 0, 232, 400, 68)

    $format = New-Object System.Drawing.StringFormat
    $format.Alignment     = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center

    $bigFont   = New-Object System.Drawing.Font -ArgumentList 'Segoe UI', 34, ([System.Drawing.FontStyle]::Bold)
    $smallFont = New-Object System.Drawing.Font -ArgumentList 'Segoe UI', 12

    $graphics.DrawString($Text, $bigFont, [System.Drawing.Brushes]::White,
        (New-Object System.Drawing.RectangleF 0, 0, 400, 232), $format)
    $graphics.DrawString('shop133', $smallFont, [System.Drawing.Brushes]::White,
        (New-Object System.Drawing.RectangleF 0, 232, 400, 68), $format)

    $bitmap.Save($Path, $codec, $encoderParams)

    # Sin estos Dispose el archivo queda bloqueado y la segunda pasada revienta.
    $bigFont.Dispose(); $smallFont.Dispose(); $format.Dispose(); $band.Dispose()
    $graphics.Dispose(); $bitmap.Dispose()
}

foreach ($prefix in $palette.Keys) {
    foreach ($n in 1..10) {
        $sku = '{0}-{1:D3}' -f $prefix, $n
        New-Placeholder -Text $sku -Hex $palette[$prefix] `
                        -Path (Join-Path $root ("{0}.jpg" -f $sku.ToLowerInvariant()))
    }
}

New-Placeholder -Text 'Sin imagen' -Hex '#6C757D' -Path (Join-Path $root 'placeholder.jpg')
```

```
Generados: 51 archivos, 323 kB en total
```

Y la comprobación que vale, derivada del **seed** y no de la suposición del bucle:

```
> Compare-Object $wanted $have
seed pide: 50   en disco: 51

InputObject     SideIndicator
-----------     -------------
placeholder.jpg =>
```

Las 50 rutas del seed resuelven a un archivo real; la única diferencia es la 51 deliberada.

### 4. Códigos de estado

Con `catalog-api` (contenedor), el Gateway y `Shop133.Web` levantados:

```
/                                200
/catalog                         200
/catalog?categoryId=3            200
/catalog?categoryId=99           200
/catalog/details/1               200
/catalog/details/99999           404
/img/products/taza-001.jpg       200
/img/products/placeholder.jpg    200
/img/products/libr-010.jpg       200
```

El `404` de `/catalog/details/99999` es la rama de producto inexistente, y el `200` de
`?categoryId=99` es la decisión de que un filtro desconocido **no** es un 404.

> **Vuelto a ejecutar el 2026-09-11 tras [`6.2.1`](fase_6_2_1.md): salida idéntica, los nueve.** Que
> el `200` de `?categoryId=99` siga siendo un 200 tiene ahora más contenido que entonces — desde
> `6.2.1` quien lo decide es `Catalog.API`, que devuelve una página vacía en vez de un 400, y no el
> frontend filtrando en memoria una lista que sí traía.

### 5. Contenido renderizado

```
cards en /catalog            : 12
cards en ?categoryId=3       : 10
cards en ?categoryId=99      : 0
chips de categoria           : 5

--- recuento por categoria (chips) ---
  id=5 Libretas   count=10
  id=2 Llaveros   count=10
  id=4 Pines      count=10
  id=3 Playeras   count=10
  id=1 Tazas      count=10
```

Los chips salen **ordenados por nombre y no por id**, que es como los devuelve `GET /categories`
desde `1.4`, así que la vista no tiene que volver a ordenarlos.

> **Vuelto a ejecutar el 2026-09-11 tras [`6.2.1`](fase_6_2_1.md)**, que es por lo que
> `cards en /catalog` dice **12** y no las 50 que midió este punto: la vista pasó a pedir páginas de
> 12 (tres filas exactas en la rejilla de cuatro columnas). Los recuentos siguen diciendo 10 porque
> ahora los manda la API sobre el catálogo entero, y no un `GroupBy` sobre lo que cupiera en la
> página. Todo lo demás de esta sección salió idéntico. Los números son salidas medidas y **no se
> editaron a mano**; se volvió a lanzar la comprobación.

```
row-cols-1 row-cols-sm-2 row-cols-lg-3 row-cols-xl-4 PRESENTE
card h-100                                           PRESENTE
card-img-top                                         PRESENTE
product-description                                  PRESENTE
card-footer bg-transparent                           PRESENTE
stretched-link                                       PRESENTE
loading="lazy"                                       PRESENTE
width="400" height="300"                             PRESENTE
mensaje 'No hay productos en esta categor':          PRESENTE
detalle: breadcrumb                                  PRESENTE
detalle: aviso de stock (Inventory)                  PRESENTE
detalle: boton carrito deshabilitado                 PRESENTE
```

Precios y URLs generadas:

```
  $249.00
  $229.00
  $269.00
  /catalog
  /catalog?categoryId=5
```

Dos decimales (los ceros que el `decimal` pierde en tránsito) y URLs en minúsculas.

### 6. El navbar, que es donde se vuelve a ver la medición de `6.1`

```html
<a class="nav-link active" aria-current="page" href="/">Inicio</a>
<a class="nav-link" href="/catalog">Catálogo</a>
<a b-aivb9iabbi class="nav-link disabled" aria-disabled="true">Carrito</a>
<a b-aivb9iabbi class="nav-link disabled" aria-disabled="true">Estado del pedido</a>
```

`Catálogo` ya no está deshabilitado y, al pasar por el `AnchorTagHelper`, **ha perdido el
`b-aivb9iabbi`**: los `<a>` con hash bajan de 3 a 2, que es la medición de `6.1` confirmada desde
el otro lado. `Carrito` y `Estado del pedido` siguen esperando a `6.3` y `6.5`.

Y el CTA de la portada:

```html
<a class="btn btn-primary btn-lg" href="/catalog">Ver el catálogo</a>
```

### 7. El Gateway parado — la regla 3 medida

```
/catalog con el Gateway parado: 503  en 2,16 s
aviso 'no esta disponible': PRESENTE
explica la regla 3:         PRESENTE
pagina de Error de la app:  no (correcto)
```

Los **2,16 s** son la decisión 4 comprobada: `2.3` midió 4,13 s en `localhost` y 2,03 s en el
literal. Y que la página **no** se vaya a `/Home/Error` es la decisión 8.

Esto es además la regla 3 en forma de medición: si `/catalog` siguiera enseñando el catálogo con
el Gateway parado, sería porque alguien está hablando con `Catalog.API` a sus espaldas.

### 8. El cupo del Gateway

```
--- 35 renders seguidos (cada uno = 2 llamadas contra un cupo de 60/min) ---
  render #30 -> 429  <== primer 429
aviso de cupo:        PRESENTE
dice 60 por minuto:   PRESENTE
segundos de espera:   60
distinto del aviso de caida: si (correcto)
```

El #30 es la aritmética de la decisión 5 confirmada al número. Los 60 s son la ventana entera,
como midió `5.2`.

### 9. La suite de arquitectura no se mueve

```
> dotnet tests\Shop133.ArchitectureTests\bin\Debug\net10.0\Shop133.ArchitectureTests.dll
=== TEST EXECUTION SUMMARY ===
   Shop133.ArchitectureTests  Total: 17, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.363s
```

`Frontend_DoesNotReference_ServicesOrGateway` sigue en verde — y lo hace sin poder ver nada de lo
que este punto añade, porque el cliente habla **HTTP**, que no deja rastro en ningún `.csproj`.
Es la misma razón por la que la regla 2 tampoco es ejecutable. Ver *Pendiente*.

### 10. Comprobación visual

Capturas con Edge headless a 1280, 700 y 500 px. **Nunca a 420**: `6.1` midió que el headless
clampa por debajo de ~500 px y **recorta la captura**, con un resultado idéntico a un
desbordamiento horizontal que no existe.

| Ancho | Resultado |
|---|---|
| 1280 | Cuatro columnas, cards a la misma altura con los precios alineados (el `h-100`), descripciones recortadas a tres líneas con puntos suspensivos, chips en una fila. |
| 700 | Dos columnas, toggler visible, sin desbordamiento. |
| 500 | Una columna, los chips envuelven a dos filas, navbar colapsado. Sin recortes. |

La ficha de detalle a 1280 sale con el breadcrumb, la imagen a la izquierda, el aviso de que el
stock lo decide Inventory y el botón de carrito deshabilitado — **con el footer pegado abajo en
una página que no llena la pantalla**, que es el mecanismo de flexbox de `6.1` funcionando.

### 11. Los archivos tocados son los previstos

```
> git status --short
 M src/Frontend/Shop133.Web/Program.cs
 M src/Frontend/Shop133.Web/Views/Home/Index.cshtml
 M src/Frontend/Shop133.Web/Views/Shared/_Layout.cshtml
 M src/Frontend/Shop133.Web/Views/_ViewImports.cshtml
 M src/Frontend/Shop133.Web/appsettings.json
 M src/Frontend/Shop133.Web/wwwroot/css/site.css
?? src/Frontend/Shop133.Web/Controllers/CatalogController.cs
?? src/Frontend/Shop133.Web/Gateway/
?? src/Frontend/Shop133.Web/Models/CatalogIndexViewModel.cs
?? src/Frontend/Shop133.Web/Models/Money.cs
?? src/Frontend/Shop133.Web/Views/Catalog/
?? src/Frontend/Shop133.Web/wwwroot/img/
```

Ningún `.csproj`, ningún `launchSettings.json`, ningún archivo de un servicio ni del Gateway.

### Lo que NO se verificó, dicho en voz alta

- **La guarda de `Gateway:BaseUrl` no se ejerció con la clave ausente.** Se razona en la decisión
  3 y el código está escrito, pero no se llegó a arrancar el frontend sin la clave para ver el
  mensaje. Es una comprobación de un minuto que se quedó fuera.
- **`Catalog.API` se verificó en su contenedor (5125) y no arrancado desde el IDE (5124)**, por el
  atasco de la tubería de logs descrito arriba. Es el mismo código y la misma base de datos, con
  el Gateway apuntado por variable de entorno; pero la combinación exacta que documenta la
  sección *Commands* de `CLAUDE.md` no es la que se ejecutó.
- **Que el filtro y el detalle funcionen con JavaScript desactivado** se razona (son enlaces, no
  scripts) pero no se probó con el JS apagado en el navegador.

---

## Pendiente

- **`6.6` acaba con los "cero `PackageReference`" de `Shop133.Web`.** Comprobado: en
  `Microsoft.AspNetCore.App/10.0.11` **no** hay Polly ni `Microsoft.Extensions.Http.Resilience`,
  así que la resiliencia sí necesita paquete y será el **primero** de este proyecto — el último de
  `src/` que no tenía ninguno. Necesita aprobación.
- **`6.6` tiene que releer `client.Timeout = 5 s`.** `Timeout` es el plazo **exterior** de todo el
  pipeline de resiliencia: con 5 s ahí, el *total-request-timeout* de 30 s del handler estándar no
  cabe y **los reintentos no llegan a ejecutarse nunca**, sin error y sin aviso. Queda anotado en
  el propio `Program.cs`.
- **Nada vigila que el frontend siga teniendo una sola URL base.** Antes de hoy no tenía ninguna;
  desde hoy tiene una. Añadir mañana `"Services": { "CatalogBaseUrl": "http://localhost:5124" }`
  a su `appsettings.json` rompería la regla 3 de frente y **ningún test se enteraría** — la misma
  forma que la regla 2. El nombre de la sección (decisión 2) empuja en contra, pero desanimando
  la forma, no rompiendo una build. **Sin dueño.**
- **`GET /api/catalog/categories` tiene ahora exactamente un consumidor** (este punto). Si alguna
  vez se decide derivar los chips de los productos, se queda sin ninguno.
- ~~**`Catalog.API` sigue sin aceptar parámetros de consulta.**~~ **Recogido por
  [`6.2.1`](fase_6_2_1.md)** el 2026-09-11: `GET /products` acepta `page`, `pageSize` y `categoryId`,
  y `GET /categories` devuelve el recuento. Sigue sin búsqueda por texto ni ordenación
  configurable, y eso no tiene dueño.
- **Un producto creado por `POST /products` con una `ImageUrl` inventada da un `<img>` roto.** El
  `?? placeholder.jpg` solo cubre el `null`, no una ruta que no existe. Se descartó el fallback
  `onerror` al elegir generar los archivos.
- **La Fase 6 sigue sin ningún punto de test en el roadmap**, así que nada de `6.1` ni de `6.2`
  está cubierto. Es el mismo hueco sin dueño que dejó `4.6`. El día que se recoja, la forma sería
  `Shop133.Gateway.Tests` —`WebApplicationFactory`, sin Docker, `Category=Fast`— y entonces sí
  haría falta el `public partial class Program { }`.
- **La cabecera `Location` del `201` de `POST /api/orders` sigue apuntando al backend sin
  prefijo**, deuda medida en `5.1` y releída en `5.3` y `6.1`. Todavía sin dueño; duele en `6.5`.
- **Sin `Dockerfile` ni servicio en compose** para el frontend. El día que lo tenga,
  `Gateway:BaseUrl` pasa a ser el nombre del servicio de compose vía `Gateway__BaseUrl`.
