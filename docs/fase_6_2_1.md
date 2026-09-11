# Fase 6.2.1 — Catalog.API estrena parámetros de consulta

**Fecha:** 2026-09-11 · **Estado:** completado · [Roadmap](../plan-desarrollo-shop133.md)

---

## Objetivo

Recoger una deuda que **estaba prometida por escrito en dos sitios y no tenía dueño en ninguno**.

`1.3` dejó `GET /products` sin paginar con una condición en el `///` de `ProductsController`:
*"Sin paginación: hasta que haya volumen sería complejidad sin caso. El seed de 1.4 mete decenas de
filas, no miles. **Entra si 6.2 la necesita**"*. `1.4` hizo lo mismo con el filtro: *"Filtro
`GET /products?categoryId=` — el caso de uso obvio de tener categorías, pero no hay consumidor hasta
6.2. **Entra ahí**, junto con la paginación que 1.3 también dejó aplazada"*.

Y `6.2` **la necesitó y decidió no añadirla**: *"`6.2` es un punto de frontend, y tocar un servicio
para servir a una vista es inventar alcance"*. La dejó en su *Pendiente* como *"un punto de la Fase 1
que nadie tiene asignado"*.

Las dos fases tenían razón por separado y el resultado conjunto era una deuda huérfana. Este punto es
quien la recoge, y es el criterio de `3.2` —*un contrato se revisa cuando aparece el consumidor que
lo necesita*— con el consumidor ya escrito delante y, por primera vez, con el coste medido.

**Va numerado `6.2.1` y no se renumera nada.** Los números son la clave entre commit, roadmap y
`docs/`, así que un punto intermedio se intercala; `6.3`-`6.7` no se mueven.

Tres entregables:

1. **Paginación** — `GET /products?page=1&pageSize=20`, con el cuerpo envuelto en un
   `PagedResponse<T>`.
2. **Filtro** — `?categoryId=N` sobre el **mismo** endpoint.
3. **Recuento** — `CategoryResponse` gana un `ProductCount` calculado en la consulta, para que los
   chips del frontend dejen de contarlos en memoria.

Y el consumidor: `Shop133.Web` deja de filtrar y de contar en memoria, y estrena un paginador.

**Lo que este punto NO arregla, y se dijo antes de empezarlo:** la paginación recorta el **payload**,
no el **cupo**. Siguen siendo **dos llamadas por render** contra el `catalog-read` de 60/60 s de
`5.2`, y como el MVC renderiza en servidor todos los visitantes cuentan como una sola IP, así que
**el primer `429` sigue llegando en el render #30** — esa medición de `6.2` no cambia y su documento
no se toca en esa parte. Lo que sí desaparece es traerse 50 filas para enseñar 10.

**Fuera de alcance:** búsqueda por texto, ordenación configurable, el carrito (`6.3`) y cualquier
cambio en el Gateway — su ruta es un catch-all y el query string ya viajaba.

**No entra ningún paquete, no se toca ni un `.csproj`, no hay migración y `Shop133.Contracts` no se
mira.** La suite de arquitectura se queda en **17** y el repositorio pasa de **131** a **140**.

---

## Decisiones

### 1. Un envelope en el cuerpo, no una cabecera `X-Total-Count`

`GET /products` devolvía un array pelado y ahora devuelve
`{ items, page, pageSize, totalItems, totalPages }`. **Es un cambio rompedor del cuerpo** y se dice
así en vez de disimularlo.

*Descartada* la cabecera `X-Total-Count`, que es la alternativa clásica y no habría roto nada. Dos
motivos: una cabecera **no sale en el documento OpenAPI**, así que quien genere un cliente desde él
no se entera de que existe; y se pierde en cualquier capa intermedia que no la propague
explícitamente, que es justo lo que hay delante de este servicio desde `5.1`.

**El cambio rompedor se puede pagar hoy y mañana no**, y ese es el argumento decisivo:
`GET /products` tiene exactamente **dos** consumidores —`Shop133.Web` y `Catalog.Tests`— y los dos se
tocan en este mismo punto. Es la misma lógica con la que `3.2` cambió `StockReserved` *"hoy es
gratis, medido"*.

`PagedResponse<T>` es **genérico** aunque hoy solo lo use `ProductResponse`: no hay ni una decisión
que dependa de `T`, y un `PagedProductResponse` concreto obligaría a un segundo tipo idéntico el día
que `GET /orders` se pagine en `6.5`. Vive en `Catalog.API/Models/` y **no** en `Shop133.Contracts`,
por la regla 4 — mismo argumento que el `///` de `CreateProductRequest`.

### 2. Una sola acción compone filtro y paginación, no un sub-recurso

*Descartado* `GET /categories/{id}/products`, que es más REST-ista. Duplicaría el `Skip`/`Take`, el
`Include` y la proyección en dos acciones de dos controllers distintos, y el día que hiciera falta
paginar el filtro habría que acordarse de las dos. Una sola acción compone las dos cosas sin repetir
nada — verificado de hecho con `?categoryId=5&pageSize=4&page=3`, que devuelve la última página
parcial.

### 3. Los parámetros van en un DTO, y no es estilo: sin él **no hay 400**

`GetProductsRequest` es un `record` con `[Range]`, no tres parámetros sueltos en la firma.

El motivo es concreto: enlazados uno a uno, **`[Range]` sobre un parámetro simple no produce un
400**. `[ApiController]` devuelve 400 cuando el `ModelState` es inválido, y un parámetro primitivo
fuera de rango no lo ensucia — así que un `?page=0` se colaría hasta el `Skip` con un desplazamiento
**negativo**. Dentro de un modelo, las mismas anotaciones sí se evalúan.

*Descartado* recortar los valores en silencio con un `Math.Clamp`: un `?pageSize=100000` devolvería
20 elementos sin decir por qué y el cliente creería que el catálogo tiene 20 filas. Pedir algo
imposible es un error del cliente y se le dice, que es el criterio del resto del servicio.

El tope `MaxPageSize = 100` existe porque sin él `?pageSize=1000000` es una forma perfectamente
válida de pedir el catálogo entero, y entonces la paginación **no protege de nada** — sería una
sugerencia, no un límite.

### 4. Un `categoryId` inexistente es `200` con la página vacía, no `400`

El `POST` **sí** devuelve 400 ante una categoría desconocida, y no es una incoherencia: allí ese id
**escribe** una relación que tiene que existir, mientras que aquí solo **selecciona**, y "no hay
nada" es una respuesta cierta. Es además lo que `6.2` ya había decidido y verificado desde el otro
lado (`?categoryId=99` → `200`), así que este punto hace que la API opine lo mismo que su cliente.

La consecuencia bonita, y medida: cero elementos son **cero páginas**, no una página vacía. El
paginador del frontend se queda sin nada que pintar en vez de con un "página 1 de 1" mentiroso.

### 5. El recuento sale de una subconsulta correlacionada, no de una navegación inversa

*Descartada* una `ICollection<Product> Products` en `Category` para poder hacer
`.Select(c => c.Products.Count)`. `Category` son hoy **dos propiedades** y ni una colección; añadirle
una para contar regalaría de paso una forma de cargar diez productos sin querer, y el `///` de la
entidad no dice nada que lo justifique.

**El recuento cuenta el catálogo ENTERO**, no la página ni el filtro. Eso no es un descuido: un menú
de categorías tiene que seguir diciendo lo mismo después de elegir una. Es exactamente lo que
`CatalogController` hacía en memoria con un `GroupBy` *antes* de filtrar, y el comentario de aquel
código lo decía: *"si se calculara después, al elegir una categoría las otras cuatro se quedarían a
0"*.

Consecuencia menor: **`CategoryResponse.From(Category)` desaparece**. Con el recuento dentro, el tipo
ya no se puede construir desde la entidad sola, así que el mapeo se va a la proyección de la consulta
—que es donde el recuento puede salir— y el factory se queda sin llamantes. `ProductResponse.From`
sigue existiendo, porque un producto sí se mapea entero desde su entidad.

### 6. `OrderBy` deja de ser cosmético en cuanto hay `Skip`

La consulta ya ordenaba por `Id` desde `1.3` y la línea no cambia, pero **su motivo sí**: sin un
orden determinista, SQL Server puede devolver la misma fila en dos páginas y saltarse otra. Antes era
una cortesía para el lector; ahora es corrección, y por eso está anotado en el `///`.

### 7. El frontend recorta `page` a 1 **antes** de llamar, y sin eso mentiría

La API devuelve `400` a un `?page=0`. Si `CatalogClient` lo dejara pasar, `ReadAsync` convertiría ese
400 en `GatewayUnavailableException` y la página diría **"el Gateway no responde"** señalando a un
proceso perfectamente vivo — el mismo *diagnóstico seguro de sí mismo y equivocado* que la decisión 3
de `6.2` rechazó al negarse a poner un `?? ""` en la URL base.

Un `?page=0` escrito a mano en la barra del navegador es alcanzable, y merece la primera página.
**Medido: `/catalog?page=0` devuelve `200`.**

No es inconsistente con la decisión 3: la API le habla a un programa y le dice que pidió algo
imposible; el frontend le habla a una persona que se equivocó tecleando. Son dos clientes distintos
del mismo endpoint.

### 8. `PageSize` vive **dos veces**, en la API y en el cliente, y **no valen lo mismo**

`GetProductsRequest.DefaultPageSize = 20` es lo que la API aplica cuando el cliente no dice nada, y
es el número que pide el roadmap. `CatalogClient.PageSize = 12` es cuántas cards son **tres filas
exactas** en la rejilla de cuatro columnas.

Son **dos decisiones de dos dueños distintos** —una de contrato y otra de maquetación— y que
divergan no es un descuido: es la prueba de que el acoplamiento no existe. El frontend puede pasar a
16 el día que cambie los breakpoints y la API no se entera; la API puede subir su defecto y el
frontend tampoco, porque manda el suyo explícito en cada llamada. Es el mismo razonamiento con el
que `Orders` duplica las constantes de longitud de `Product` (*"son genuinamente libres de
divergir"*), aquí entre dos proyectos que ni siquiera se referencian.

**El "20 por página" del título del roadmap es el de la API y se cumple**: `GET /products` sin
parámetros devuelve 20. Lo que el frontend pide es asunto suyo.

### 9. Los extremos agotados del paginador son `<span>` sin `href`, no `<a class="disabled">`

La primera versión emitía `<a class="page-link" href="/catalog?page=0">&laquo;</a>` dentro de un
`<li class="page-item disabled">`, que es lo que enseña el ejemplo de Bootstrap. **Medido en el HTML
servido y corregido:** Bootstrap 5 le pone `pointer-events: none` al `.page-link` deshabilitado, así
que el ratón no lo alcanza — **pero el teclado sí**, y un tabulador + Enter navegaría a `?page=0`.

Es la misma forma que `6.1` eligió para los enlaces pendientes del navbar: sin destino, sin `href`.

### 10. La primera página va **sin** `?page=1`

`/catalog` y `/catalog?page=1` pintarían lo mismo, y dos direcciones para una página es lo que `6.2`
ya evitó al no meter el filtro en la URL cuando no hay filtro. El `<a>` del "1" y el de "Anterior"
desde la página 2 apuntan los dos a `/catalog` pelado — **verificado en el HTML**.

Y **cada enlace del paginador arrastra el `categoryId`**. Perderlo es el fallo clásico de un
paginador: la página 2 te devolvería al catálogo completo sin decir que ha quitado el filtro. Razor
**omite un atributo cuyo valor es `null`**, así que con `SelectedCategoryId` a `null` el
`asp-route-categoryId` no llega a salir en la URL — el mismo mecanismo que `6.1` usó para el
`aria-current`.

Los chips, en cambio, **no** conservan la página: elegir una categoría vuelve a la primera. Conservar
la página 3 al pasar a una categoría que tiene una sola sería aterrizar en un estado vacío.

### 11. Un tercer estado vacío, porque el segundo pasaría a mentir

La vista tenía dos: *"no hay productos en esta categoría"* y *"el catálogo está vacío"*. Con
paginación aparece un tercero —`?page=99`— en el que **sí hay productos**, solo que no en esa página,
y el primer mensaje sería simplemente falso.

`CatalogIndexViewModel.IsPastLastPage` lo distingue (`Products.Count == 0 && TotalItems > 0`) y el
aviso dice cuántos hay y en cuántas páginas, con un enlace a la primera **que conserva el filtro**.

### 12. `CategoryFilterViewModel` se elimina, y es la decisión 7 de `6.2` envejeciendo bien

Aquel tipo existía para cargar un recuento que el controller calculaba en memoria. Ahora ese número
lo manda `GET /categories`, así que `CatalogCategory` —id, nombre y recuento— **es** el chip, y un
tipo que copiara esos tres campos sería el *passthrough* que `6.2` rechazó al no crear un
`ProductViewModel`.

`CatalogIndexViewModel` **se queda**, y su razón de ser no cambia: la página tiene estado que la API
no manda (cuál es el filtro activo) y estado que hay que juntar de **dos** respuestas distintas.

**Y el `GroupBy` no se podía quedar aunque se quisiera:** contaba sobre los productos traídos, lo
cual solo funcionaba porque se traía el catálogo entero. Sobre una página de veinte contaría veinte.
Que el recuento llegue de la API no es una optimización — es lo único que sigue dando la respuesta
correcta.

---

## Cambios

### Nuevos

| Archivo | Rol |
|---|---|
| `src/Services/Catalog/Catalog.API/Models/PagedResponse.cs` | El sobre. Su `From(...)` es el único sitio con la aritmética del paginado. |
| `src/Services/Catalog/Catalog.API/Models/GetProductsRequest.cs` | `page`, `pageSize`, `categoryId` con `[Range]`. |
| `src/Frontend/Shop133.Web/Gateway/CatalogPage.cs` | El envelope re-declarado en el lado del cliente (regla 3). |
| `docs/fase_6_2_1.md` | Este documento. |

### Modificados

| Archivo | Cambio |
|---|---|
| `src/Services/Catalog/Catalog.API/Controllers/ProductsController.cs` | `GetAll` pagina y filtra; `///`, `[EndpointDescription]` y `[ProducesResponseType]` reescritos. |
| `src/Services/Catalog/Catalog.API/Controllers/CategoriesController.cs` | Proyección con subconsulta correlacionada para el recuento. |
| `src/Services/Catalog/Catalog.API/Models/CategoryResponse.cs` | `+ ProductCount`; `From(Category)` eliminado. |
| `src/Frontend/Shop133.Web/Gateway/CatalogClient.cs` | `GetProductsAsync(categoryId, page, ct)` + `PageSize`; recorte de `page`. |
| `src/Frontend/Shop133.Web/Gateway/CatalogCategory.cs` | `+ ProductCount`. |
| `src/Frontend/Shop133.Web/Controllers/CatalogController.cs` | Fuera el `GroupBy` y el `Where` en memoria; `Index(categoryId, page)`. |
| `src/Frontend/Shop133.Web/Models/CatalogIndexViewModel.cs` | `+ Page`, `TotalPages`, `TotalItems`, `IsPastLastPage`; `CategoryFilterViewModel` fuera. |
| `src/Frontend/Shop133.Web/Views/Catalog/Index.cshtml` | Cabecera por `TotalItems`, chips por `ProductCount`, paginador, tercer estado vacío. |
| `tests/Services/Catalog/Catalog.Tests/ProductsEndpointsTests.cs` | 8 tests nuevos; el de siempre lee el envelope. |
| `tests/Services/Catalog/Catalog.Tests/CategoriesEndpointsTests.cs` | 1 test nuevo, el del recuento. |
| `docs/fase_6_2.md` | Corregido — ver abajo. |

Ningún `.csproj`, ningún `launchSettings.json`, ninguna migración, nada del Gateway ni de
`Shop133.Contracts`.

---

## Detalles que cuestan tiempo

- **`[FromQuery]` es obligatorio sobre el DTO y su ausencia no da ningún error.** Con
  `[ApiController]`, un parámetro de **tipo complejo** se infiere como `[FromBody]`. Sin el atributo,
  un `GET` con cadena de consulta llega con los valores por defecto: 20 elementos de la página 1,
  siempre, **con la petición perfectamente escrita**. No hay excepción, ni aviso, ni log.
- **`[Range]` sobre un parámetro simple no produce un 400.** Es lo que obligó a meter los tres
  parámetros en un `record` (decisión 3). Enlazados sueltos, las anotaciones no se evalúan y el
  `ModelState` queda limpio, así que `?page=0` habría llegado al `Skip` con un desplazamiento
  negativo.
- **La división entera en `TotalPages` hace desaparecer categorías enteras, no "una página".** Se
  rompió a propósito (`totalItems / pageSize`) y el fallo más revelador no fue el de la última página
  parcial sino `GetAll_FilteredByCategory`: **`Expected: 1, Actual: 0`** — 10 productos con
  `pageSize` 20 dan **cero** páginas, así que el paginador del frontend no pintaría nada para una
  categoría que tiene diez productos.
- **Edge headless clampa también la ALTURA, y ahí no recorta: no escribe el archivo.** `6.1` midió
  que por debajo de ~500 px de ancho clampa y **recorta** la captura; por encima de ~1400 px de alto
  el `--screenshot` simplemente **falla en silencio** —sin error, sin PNG— y lo único que se ve es un
  `Get-Item: Cannot find path` de PowerShell diez líneas después. Es lo que impidió capturar el
  paginador de la página 3, que cae hacia los 1750 px.
- **Razor codifica los acentos de una expresión `@(...)` a entidades HTML y los del markup literal
  no.** En la misma frase del aviso salen `página 99` (literal) y `una sola p&#xE1;gina`
  (interpolada). El navegador pinta lo mismo, pero **una comprobación con `.Contains("página")` pasa
  en un caso y falla en el otro**, y el HTML servido parece corrupto a medias. Sexta variante de la
  familia de trampas de verificación que este repositorio lleva anotando desde `3.4`.
- **Los tests de filtro usan Libretas (5) y nunca Tazas (1) ni Llaveros (2).** `NewProduct` crea en
  Tazas y `Update_ExistingProduct…` mueve un producto a Llaveros, así que filtrar por 1 o 2 daría un
  `totalItems` que depende del orden de ejecución. Sobre una categoría que nadie escribe se puede
  afirmar **10 exacto**, sin romper la regla de la clase de no contar el catálogo entero.
- **El primer `GetAll` tuvo que pasar a `?pageSize=50`.** `GetAll_AfterMigrations_ReturnsSeededCatalog`
  afirma que están las 50 filas del seed, y con la página por defecto solo vería 20. Funciona porque
  el seed ocupa los ids 1..50 con `IDENTITY_INSERT` y los productos que crean los demás tests salen
  con ids de cuatro cifras, así que ordenando por `Id` la primera página de 50 **es** el seed.
- **`Stop-Process` sobre los servicios que había levantados falló con `Access is denied`** y el build
  funcionó igual: eran procesos que ya no escuchaban en ningún puerto. La comprobación que vale no es
  `Get-Process` sino un `curl` contra el puerto — `5124`, `5104` y `5025` devolvían `000` con los tres
  procesos listados como vivos.

---

## Verificación

### 1. Build limpio de los dos proyectos tocados

```
> dotnet build src\Services\Catalog\Catalog.API\Catalog.API.csproj
  Catalog.API -> C:\personalprojects\shop133\src\Services\Catalog\Catalog.API\bin\Debug\net10.0\Catalog.API.dll
Build succeeded.
    0 Warning(s)
    0 Error(s)

> dotnet build src\Frontend\Shop133.Web\Shop133.Web.csproj
  Shop133.Web -> C:\personalprojects\shop133\src\Frontend\Shop133.Web\bin\Debug\net10.0\Shop133.Web.dll
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### 2. Las tres suites

```
> dotnet tests\Services\Catalog\Catalog.Tests\bin\Debug\net10.0\Catalog.Tests.dll
   Catalog.Tests  Total: 38, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 180.735s

> dotnet tests\Shop133.ArchitectureTests\bin\Debug\net10.0\Shop133.ArchitectureTests.dll
   Shop133.ArchitectureTests  Total: 17, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.859s

> dotnet tests\Gateway\Shop133.Gateway.Tests\bin\Debug\net10.0\Shop133.Gateway.Tests.dll
   Shop133.Gateway.Tests  Total: 26, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 3.171s
```

Catalog pasa de 29 a **38**; las otras dos no se mueven. La del Gateway es la que confirma que este
punto no le afecta: su stub no deserializa cuerpos y su ruta es un catch-all.

### 3. La API, contra el contenedor (5125)

```
/products                                      200
/products?page=3                               200
/products?page=99                              200
/products?categoryId=5                         200
/products?categoryId=99                        200
/products?page=0                               400
/products?pageSize=101                         400
/products?pageSize=100                         200
/categories                                    200
```

```
/products                                    items=20  page=1    size=20   total=50  pages=3
/products?page=3                             items=10  page=3    size=20   total=50  pages=3
/products?page=99                            items=0   page=99   size=20   total=50  pages=3
/products?categoryId=5                       items=10  page=1    size=20   total=10  pages=1
/products?categoryId=99                      items=0   page=1    size=20   total=0   pages=0
/products?categoryId=5&pageSize=4&page=3     items=2   page=3    size=4    total=10  pages=3
```

La última línea es la decisión 2: filtro y paginación **compuestos en una acción**, devolviendo la
última página parcial. Y `?categoryId=99` es la decisión 4: `200`, `totalItems` 0 y **`totalPages`
0**, no una página vacía.

El `400` nombra el campo:

```json
{"title":"One or more validation errors occurred.","status":400,
 "errors":{"Page":["The field Page must be between 1 and 2147483647."]},"traceId":"00-9f47…"}

{"title":"One or more validation errors occurred.","status":400,
 "errors":{"PageSize":["The field PageSize must be between 1 and 100."]},"traceId":"00-6b3e…"}
```

### 4. El recuento, y el SQL que lo produce

```
> curl.exe -s http://127.0.0.1:5125/categories
[{"id":5,"name":"Libretas","productCount":10},{"id":2,"name":"Llaveros","productCount":10},
 {"id":4,"name":"Pines","productCount":10},{"id":3,"name":"Playeras","productCount":10},
 {"id":1,"name":"Tazas","productCount":10}]
```

El log de EF de la suite enseña la subconsulta correlacionada de la decisión 5 traducida tal cual:

```sql
SELECT [c].[Id], [c].[Name], (
    SELECT COUNT(*)
    FROM [Products] AS [p]
    WHERE [p].[CategoryId] = [c].[Id]) AS [ProductCount]
FROM [Categories] AS [c]
ORDER BY [c].[Name]
```

Un `COUNT` por fila sobre cinco categorías fijas, sin traer un solo producto — que es exactamente lo
que se descartó conseguir con una navegación inversa.

### 5. A través del Gateway (5104)

```
/api/catalog/products                            200
/api/catalog/products?page=2                     200
/api/catalog/products?categoryId=5               200
/api/catalog/categories                          200
  page 2 -> items=20 total=50 pages=3 primerId=21
  400 conserva su forma: 400
```

`primerId=21` es lo que prueba que **el query string sobrevive al `PathRemovePrefix`** de `5.1`. El
Gateway no necesitó ni una línea de cambio.

### 6. El frontend (5025)

Con `CatalogClient.PageSize = 12` (decisión 8), o sea **5 páginas de las que la última tiene 2**:

```
/catalog                         200   cards=12
/catalog?page=2                  200   cards=12
/catalog?page=5                  200   cards=2
/catalog?page=99                 200   cards=0
/catalog?categoryId=3            200   cards=10
/catalog?categoryId=99           200   cards=0
/catalog?page=0                  200
/catalog/details/1               200
```

**`/catalog?page=0` es `200` y no un aviso de caída**: es la decisión 7 medida. Y la página 5 con 2
cards es la última página parcial vista **desde la interfaz**, no solo desde un test — 50 = 4×12 + 2.

Cabecera y chips:

```
50 productos · página 1 de 5

  id=5 Libretas   count=10
  id=2 Llaveros   count=10
  id=4 Pines      count=10
  id=3 Playeras   count=10
  id=1 Tazas      count=10
```

Los recuentos siguen diciendo 10 con el grid pintando 12 de una página — el recuento cuenta el
catálogo entero (decisión 5). Que ninguno de los dos números sea 12 es lo que hace la comprobación
útil: si el recuento se calculara sobre lo traído, los cinco chips sumarían 12.

El paginador, en la primera, una intermedia y la última:

```
--- /catalog ---        --- /catalog?page=3 ---   --- /catalog?page=5 ---
  span   «                a      «                  a      «
  a      1                a      1                  a      1
  a      2                a      2                  a      2
  a      3                a      3                  a      3
  a      4                a      4                  a      4
  a      5                a      5                  a      5
  a      »                a      »                  span   »
--- /catalog?categoryId=5 ---
  (sin paginador: 10 productos, una sola página)
```

`span` en los extremos agotados —y solo ahí— es la decisión 9. Y los `href` desde la página 3:

```
  «        -> /catalog?page=2
  1        -> /catalog
  2        -> /catalog?page=2
  3        -> /catalog?page=3
  4        -> /catalog?page=4
  5        -> /catalog?page=5
  »        -> /catalog?page=4
```

La página 1 sin `?page=1` —tanto en el «1» como en el «Anterior» desde la página 2— es la decisión
10; y ninguno lleva `categoryId` porque no hay filtro, que es Razor omitiendo el atributo `null`.

El tercer estado vacío, con filtro:

```
> /catalog?page=99&categoryId=5
  "No hay nada en la página 99. Hay 10 productos en una sola página. [Ir a la primera página]"
  enlace -> /catalog?categoryId=5
```

El enlace **arrastra el `categoryId`**.

### 7. Dos roturas deliberadas

**a) División entera en `TotalPages`** (`totalItems / pageSize` en vez del `Math.Ceiling`):

```
Catalog.Tests.ProductsEndpointsTests.GetAll_FilterAndPagingCombined_ReturnsTheLastPartialPage [FAIL]
Catalog.Tests.ProductsEndpointsTests.GetAll_WithoutQueryParameters_ReturnsFirstPageOfTwenty   [FAIL]
Catalog.Tests.ProductsEndpointsTests.GetAll_FilteredByCategory_ReturnsOnlyThatCategory        [FAIL]
   Catalog.Tests  Total: 26, Errors: 0, Failed: 3
```

Tres tests la atrapan, y el revelador es el tercero: `Expected: 1, Actual: 0`. Diez productos con
`pageSize` 20 dan **cero** páginas, así que una categoría entera desaparecería del paginador.

**b) El recuento calculado sobre una página** (`.Take(4).Count()`):

```
Catalog.Tests.CategoriesEndpointsTests.GetAll_AfterMigrations_ReturnsTenProductsPerCategory [FAIL]
Expected: 10   Actual: 4   (×5)
```

Atrapada por el único test previsto para ello. Las dos roturas se revirtieron y la suite volvió a
**38/38**.

### 8. Comprobación visual

Capturas con Edge headless a 1280, 700 y 500 px. **Nunca a 420** (`6.1`: clampa y recorta) **ni por
encima de ~1400 px de alto** (medido aquí: no escribe el archivo).

| Ancho | Resultado |
|---|---|
| 1280 | Cuatro columnas, 12 cards en **tres filas exactas**, `50 productos · página 1 de 5`, chips con su badge. La página 1 mezcla Tazas y Llaveros, que es la paginación cruzando categorías. |
| 700 | Dos columnas, toggler visible, sin desbordamiento. |
| 500 | Una columna, chips a dos filas, navbar colapsado. |

`?page=5` a 1280 es la captura que más dice: las **dos** últimas libretas, `página 5 de 5`, y el
paginador entero visible —`« 1 2 3 4 5 »` con el 5 en azul y el `»` en gris—, que es la decisión 9
vista con los ojos y no solo en el HTML. Bajar a 12 por página es lo que lo hizo capturable: con 20
el paginador caía hacia los 1750 px y el headless no llegaba.

`?page=99` a 1280: el aviso del tercer estado vacío con su botón, **y el footer pegado abajo en una
página que no llena la pantalla** — el mecanismo de flexbox de `6.1` funcionando.

### Lo que NO se verificó, dicho en voz alta

- **El paginador con un filtro activo no es alcanzable hoy.** Cada categoría del seed tiene 10
  productos y el frontend pide de 12 en 12, así que **ninguna categoría llega a dos páginas**. Que
  los enlaces del paginador arrastren el `categoryId` está escrito y razonado, pero solo se ha
  medido en el enlace del tercer estado vacío, que usa el mismo `asp-route-categoryId`. Se
  comprobará solo cuando una categoría pase de 20 productos.
- **El paginador se capturó solo en la última página.** En la primera cae fuera del recorte del
  headless (~1400 px de alto) y en la quinta cabe porque solo hay dos cards. El resto se verificó
  sobre el HTML servido, que además es más estricto: distingue `<a>` de `<span>` y enseña cada
  `href`.
- **El `429` y el Gateway caído no se volvieron a medir**, a propósito: `6.2` los midió y este punto
  no los cambia — la paginación recorta el payload, no el cupo.
- **`Catalog.API` se verificó en su contenedor (5125) y no arrancado desde el IDE (5124)**, igual que
  en `6.2` y por el mismo atasco de la tubería de logs.

---

## Pendiente

- **`GET /products` sigue sin búsqueda por texto ni ordenación configurable.** El orden es siempre
  por `Id` y no hay `?q=`. No hay ningún punto del roadmap que los pida; entrarían si `6.3` o una
  vista de administración los necesitan, con el mismo criterio con el que entró esto.
- **Nadie vigila que `Shop133.Web` siga teniendo una sola URL base** (deuda de `6.2`, sin dueño). El
  `CatalogPage<T>` nuevo no la mueve ni en un sentido ni en otro.
- **La cabecera `Location` del `201` de `POST /api/orders` sigue apuntando al backend sin prefijo**,
  deuda medida en `5.1` y releída ya cuatro veces. Duele en `6.5`.
- **La Fase 6 sigue sin ningún punto de test en el roadmap**, así que el paginador, el tercer estado
  vacío y el recorte de `page` del cliente **no tienen ni un test** — solo la verificación a mano de
  arriba. Es el mismo hueco sin dueño que dejó `4.6`. La API sí queda cubierta, porque
  `Catalog.Tests` existe.
- **Nada comprueba que el frontend y la API no divergan en el envelope.** `CatalogPage<T>` re-declara
  `PagedResponse<T>` por la regla 3, así que renombrar un campo en la API deja el frontend
  compilando y devolviendo un `JsonException` en tiempo de ejecución — que además **no** lo captura
  `CatalogClient` (solo atrapa `HttpRequestException` y `TaskCanceledException`), así que saldría
  como un 500 y no como la vista de indisponibilidad. Es la misma forma que la deuda de la regla 2:
  sin dueño, y no hay test de arquitectura que pueda verla.
- **`PagedResponse<T>` tiene un solo usuario.** Si `6.5` pagina `GET /orders`, tendrá que
  re-declararlo en `Orders.API` —la regla 1 y la 4 impiden compartirlo— y ese será el momento de
  releer si el genérico se ganó el sitio.
