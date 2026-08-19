# Fase 1.3 — Endpoints CRUD de `Catalog.API`

**Fecha:** 2026-08-19 · **Estado:** completado · **Roadmap:** [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md), punto 1.3

---

## Objetivo

`Product` existía desde [1.1](fase_1_1.md) y tenía tabla desde [1.2](fase_1_2.md), pero **nada la exponía**: `Catalog.API` no tenía ni un controller y `/openapi/v1.json` devolvía un documento sin operaciones. Este punto convierte esa entidad en un servicio consultable con los cinco endpoints del roadmap: `GET /products`, `GET /products/{id}`, `POST /products`, `PUT /products/{id}` y `DELETE /products/{id}`.

Va aquí, y no antes, porque los dos puntos anteriores dejaron a propósito tres deudas que solo un endpoint puede cobrar:

1. **La vía de mutación de `Product`.** 1.1 no escribió ningún `Update` para no inventarle la firma sin caso de uso. El `PUT` es ese caso de uso.
2. **El mapeo de `ArgumentException` a `400`** con el nombre del campo. 1.1 verificó que las guardas salen con el `paramName` relleno justo para esto.
3. **El `409 Conflict` por `Sku` duplicado.** 1.2 creó el índice único y dejó escrito que dejarlo salir como `500` desperdiciaría la mitad de aquel trabajo.

**Fuera de alcance deliberadamente:** el seed (`1.4`), la UI de OpenAPI con Scalar (`1.5` — el documento JSON sí se genera ya, la interfaz no), el Dockerfile (`1.6`) y los tests automatizados de estos endpoints (`1.7`). La verificación de este punto es manual, y la tabla de abajo es lo que se ejecutó. **La tabla `Products` se deja vacía**, igual que la dejó 1.2: las filas de prueba se borraron al terminar.

---

## Decisiones

### 1. El controller inyecta `CatalogDbContext` directamente, sin repositorio

*Descartado* un `IProductRepository` en `Catalog.Infrastructure` con el controller delegando en él. El argumento a favor era literal: la sección *Conventions* de [CLAUDE.md](../CLAUDE.md) dice que los controllers son delgados y que la lógica de negocio vive en `.Infrastructure`/`.Domain`.

Se descarta porque sobre un CRUD ese repositorio **no tendría lógica que llevarse**. `DbContext` ya es Unit of Work + Repository; envolverlo produce métodos que son un `return db.Products...` con otro nombre, y un archivo más que leer para entender lo mismo. La regla de controllers delgados se sostiene igual sin él, porque aquí no hay negocio que sacar del controller: las invariantes están en el constructor de la entidad (1.1) y la unicidad en el índice (1.2). Lo que queda en la acción es traducir entre HTTP y esas dos cosas, que es exactamente lo que un controller debe hacer.

Es además lo que dejó apuntado la sección *Pendiente* de [fase_1_2.md](fase_1_2.md). La decisión se revisa el día que aparezca lógica de verdad — no antes, y no para cumplir la forma de una regla en contra de su motivo.

### 2. El `PUT` puede cambiar el `Sku`

*Descartado* dejar el `Sku` inmutable tras el alta, que habría sido más simple: el `409` solo afectaría al `POST` y el `Update` de la entidad tendría un parámetro menos.

Se descarta porque contradice lo que este proyecto ya tiene escrito. La tabla de la decisión 9 de [fase_1_1.md](fase_1_1.md) distingue `Id` de `Sku` precisamente por esto:

| | `Id` | `Sku` |
|---|---|---|
| Puede cambiar | Nunca | Sí — se corrige y se renumera |

Un código de producto mal tecleado se corrige; para eso es un código de negocio y no una clave sustituta. Hacerlo inmutable habría obligado a corregir aquella tabla, y la alternativa —cambiar la documentación para que encaje con el código que salió más cómodo— es justo al revés de como debe ir.

**Consecuencia:** el `409` aplica a los dos verbos. Verificado en la comprobación 13.

### 3. Los DTO viven en `Catalog.API/Models/`, no en `Shop133.Contracts`

*Descartado* meterlos en el proyecto compartido, que es donde ya viven los tipos que cruzan servicios. La regla 4 de CLAUDE.md lo prohíbe explícitamente: `Shop133.Contracts` es solo para los mensajes que viajan por RabbitMQ, sin validation attributes. Un `CreateProductRequest` no viaja por ninguna cola — es la forma del cuerpo de una petición HTTP de un solo servicio. Ponerlo ahí haría que un cambio en el formulario de alta del catálogo fuera un breaking change para Payments.

**Tres tipos y no dos.** `CreateProductRequest` y `UpdateProductRequest` tienen hoy exactamente la misma forma, y aun así son tipos distintos. *Descartado* compartir uno: tendría que llamarse algo que mintiera sobre uno de los dos verbos, y el día que diverjan —un campo que solo se fija al alta, o al revés— habría que separarlos con los endpoints ya publicados. Duplicar seis propiedades es más barato que ese día.

**`ProductResponse` aparte de la entidad**, con un `static From(Product)`. Hoy los campos coinciden uno a uno y el tipo parece puro ceremonial. Existe porque `Product` es el modelo de **persistencia**: en cuanto la Fase 3 o un requisito interno le añadan una columna, esa columna aparecería en la respuesta HTTP sin que nadie lo hubiera decidido. El DTO es el sitio donde se elige qué sale.

### 4. Las longitudes de las DataAnnotations salen de las constantes de la entidad

`[MaxLength(Product.SkuMaxLength)]`, nunca `[MaxLength(50)]`. Era el tercer consumidor que 1.1 previó para esas constantes: la guarda del constructor, el `nvarchar(n)` de 1.2 y la validación del DTO. Con literales, subir un máximo obligaría a acordarse de tres sitios y el fallo se manifestaría como un `500` desde SQL Server en vez de como un `400`.

### 5. El `409` lo detecta el índice, no una consulta previa

*Descartado* comprobar `AnyAsync(p => p.Sku == sku)` antes del `INSERT` y devolver `409` sin excepción. Es más legible y evita el `try`/`catch`, pero es un TOCTOU: dos peticiones concurrentes pasan las dos la comprobación, y una revienta igual con la `DbUpdateException` que se pretendía evitar. Quedaría un `500` en el caso raro, que es el peor sitio donde dejarlo.

El índice único es el único árbitro fiable, así que el camino es capturar `DbUpdateException` y traducirla. La consulta previa sería una comodidad que además **mentiría sobre quién garantiza la unicidad** — que es lo que 1.2 se molestó en dejar claro.

### 6. El número de error de SQL Server no entra en el controller

La comprobación vive en `Catalog.Infrastructure/Persistence/DbUpdateExceptionExtensions.cs`, un solo método de extensión `IsUniqueConstraintViolation()`.

*Descartado* comprobarlo en el `catch` del controller. Habría necesitado `using Microsoft.Data.SqlClient` en `Catalog.API`, o sea, el driver del motor de base de datos dentro de la capa que habla HTTP. "2601 y 2627 son los códigos de violación de unicidad de SQL Server" es conocimiento de la capa de persistencia y ahí se queda. El controller sigue capturando `DbUpdateException` —inevitable si el árbitro es el índice— pero no sabe qué motor hay detrás.

Se comprueban **los dos** números: SQL Server usa 2601 para índice único y 2627 para constraint `UNIQUE`/PK. Hoy 1.2 declaró un índice, así que sale 2601; si mañana pasara a ser un constraint, el endpoint seguiría devolviendo `409` en vez de empezar a devolver `500` en silencio.

### 7. `PUT` devuelve `204`, no `200` con cuerpo

*Descartado* devolver el producto actualizado. Ahorraría un `GET` al frontend de la Fase 6, pero el servidor no tiene nada que contar que el cliente no acabe de mandar: sería devolverle su propio cuerpo. Si algún día hay un campo calculado o una versión, ese es el momento de cambiarlo, y el sitio está señalado en el comentario de la acción.

### 8. Borrado físico

*Descartado* el borrado lógico (`IsDeleted` + filtro global de consulta). Necesitaría columna, migración y un `HasQueryFilter`, y no está en el roadmap.

Lo que sí abre —y merece estar escrito porque es exactamente el tipo de cosa que este proyecto existe para hacer visible— es que **a partir de la Fase 3 un `OrderLine.ProductId` puede apuntar a un producto que aquí ya no existe**. Ninguna clave foránea puede impedirlo: las bases están separadas por la regla 1 de CLAUDE.md, y `OrdersDb` no puede tener una FK contra `CatalogDb`. Es una referencia colgante entre servicios, no un bug de este endpoint, y la respuesta correcta no es una FK sino que `OrderLine` congele lo que necesita —cosa que ya hace con `UnitPrice`.

### 9. `ExecuteDeleteAsync` en vez de cargar y `Remove`

Una sola ida y vuelta, y las filas afectadas ya distinguen el `404` sin materializar la entidad. *Descartado* el `FirstOrDefaultAsync` + `Remove` + `SaveChangesAsync` clásico: son dos consultas para hacer lo mismo, y la entidad cargada se descarta sin usarla. La contrapartida —que salta el ChangeTracker y no dispararía eventos de dominio— no aplica: no hay ninguno.

### 10. `LowercaseUrls = true`

Una línea en `Program.cs`. El enrutado ya es case-insensitive, así que `/products` entraba igual sin esto. Lo que arregla es la URL **generada**: sin ello el `Location` del `201` sale como `/Products/1002` (`[Route("[controller]")]` toma el nombre de la clase) y el documento OpenAPI que consume 1.5 publica las rutas capitalizadas, contradiciendo lo que dice el roadmap.

### 11. Ningún test de arquitectura nuevo

[fase_0_6.md](fase_0_6.md) dejó anotada una regla candidata para este punto: "los controllers son delgados — si es que se puede expresar sin falsos positivos". **No se puede.** Las métricas disponibles (líneas por acción, número de sentencias) no distinguen lógica de negocio de un `switch` de mapeo o de una tanda de `[ProducesResponseType]`. Un test así se rompe con el primer endpoint legítimamente largo y acaba desactivado, que es peor que no tenerlo.

Además, 1.3 no añade ninguna regla nueva a CLAUDE.md, y el item de test de la fase es 1.7. Mismo criterio que en 1.1.

---

## Cambios

| Archivo | Rol |
|---|---|
| [Catalog.API/Controllers/ProductsController.cs](../src/Services/Catalog/Catalog.API/Controllers/ProductsController.cs) | **Nuevo.** Las cinco acciones, el mapeo de errores a 400/404/409 y los `[ProducesResponseType]` que alimentan el documento OpenAPI de 1.5. |
| [Catalog.API/Models/CreateProductRequest.cs](../src/Services/Catalog/Catalog.API/Models/CreateProductRequest.cs) | **Nuevo.** Cuerpo del `POST`, con DataAnnotations sobre las constantes de la entidad. |
| [Catalog.API/Models/UpdateProductRequest.cs](../src/Services/Catalog/Catalog.API/Models/UpdateProductRequest.cs) | **Nuevo.** Cuerpo del `PUT`. Misma forma, tipo distinto — decisión 3. |
| [Catalog.API/Models/ProductResponse.cs](../src/Services/Catalog/Catalog.API/Models/ProductResponse.cs) | **Nuevo.** Lo que se devuelve, más el único mapeo entidad → DTO del servicio. |
| [Catalog.Infrastructure/Persistence/DbUpdateExceptionExtensions.cs](../src/Services/Catalog/Catalog.Infrastructure/Persistence/DbUpdateExceptionExtensions.cs) | **Nuevo.** `IsUniqueConstraintViolation()`. Mantiene los códigos 2601/2627 dentro de la capa de persistencia — decisión 6. |
| [Catalog.Infrastructure/Entities/Product.cs](../src/Services/Catalog/Catalog.Infrastructure/Entities/Product.cs) | **Modificado.** Nuevo `Update(...)`; el cuerpo del constructor se extrae a un `private Apply(...)` que comparten los dos. Corregido el comentario sobre `ToUpperInvariant` — ver *Detalles*. |
| [Catalog.API/Program.cs](../src/Services/Catalog/Catalog.API/Program.cs) | **Modificado.** Una línea: `RouteOptions.LowercaseUrls`. |

**Ningún `.csproj` se tocó.** No entró ningún paquete NuGet: las DataAnnotations están en el framework y `Microsoft.Data.SqlClient` llega transitivamente a `Catalog.Infrastructure` a través del provider de EF Core que instaló 1.2. La regla de "EF Core solo en `.Infrastructure`" sigue verde (12 tests) porque mira los `PackageReference` declarados, y no se declaró ninguno nuevo.

Otros archivos: [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md) (checkbox 1.3), [docs/README.md](README.md) (fila del índice) y [CLAUDE.md](../CLAUDE.md) (párrafo de estado de la Fase 1).

---

## Detalles que cuestan tiempo

### El `ß → SS` que 1.1 dio por bueno es falso en .NET

Este es el hallazgo del punto, y salió de ir a reutilizar un argumento escrito en 1.1 en vez de copiarlo.

`Product` normaliza el `Sku` con `ToUpperInvariant()` **antes** de validar la longitud, y el comentario que justificaba ese orden decía que pasar a mayúsculas "puede alargar la cadena en Unicode (ß → SS)". El plan de este punto lo heredó, y encima lo usó como razón de ser del `catch (ArgumentException)` del controller: un `Sku` de 26 `ß` pasaría el `[MaxLength(50)]` del DTO y reventaría en la entidad al convertirse en 52 caracteres.

**No ocurre.** El `POST` con esos 26 caracteres devolvió `201`:

```
sku chars: 26, upper chars: 26
StatusCode : 201
Content    : {"id":1004,"sku":"ßßßßßßßßßßßßßßßßßßßßßßßßßß",...}
```

La razón es que `ToUpperInvariant` usa **simple case mapping**, que es 1:1 por carácter. El mapeo `ß → SS` es *full case mapping*, y .NET no lo aplica en `ToUpper`/`ToUpperInvariant`. Recorridos los 63.488 caracteres del BMP:

```
U+00DF: len 1 -> 1  (upper='ß')
U+FB00: len 1 -> 1  (upper='ﬀ')
U+0149: len 1 -> 1  (upper='ŉ')
U+01F0: len 1 -> 1  (upper='ǰ')
chars in BMP whose ToUpperInvariant changes length: 0
```

Ninguno. **El orden de las operaciones se mantiene** porque sigue siendo el correcto por otro motivo (se valida el valor que se persiste, no el que llegó), pero el comentario de la entidad se corrigió: descansaba sobre un caso que no existe. Un `curl` de 30 segundos evitó que el error se propagara a un tercer archivo.

### El hueco de validación que sí existe: `ImageUrl` en blanco

Si la razón del `catch (ArgumentException)` era falsa, la pregunta siguiente es si el `catch` sirve para algo o es código muerto. Repasando campo por campo, casi todo lo cubre el DTO: `[Required]` rechaza `null`, `""` **y** cadenas de solo espacios (`RequiredAttribute` con `AllowEmptyStrings = false` hace `IsNullOrWhiteSpace`), los rangos cubren precio y stock, y el `Trim()` de la entidad solo puede acortar.

Salvo uno. **`ImageUrl` es opcional**, así que el DTO solo le pone `[MaxLength]` — sin `[Required]`, porque un producto sin foto es válido. Pero la entidad, si el valor está *presente*, exige que no esté en blanco. Así que `"   "` pasa la validación del modelo y llega a la entidad:

```
POST {"sku":"WS-1",...,"imageUrl":"   "}
HTTP 400
{"errors":{"imageUrl":["The value cannot be an empty string or composed entirely of whitespace. (Parameter 'imageUrl')"]}}
```

El `catch` es lo único que separa eso de un `500`, y el `paramName` que 1.1 se molestó en verificar es lo que hace que el error salga con la clave `imageUrl` en vez de suelto. La justificación del comentario se reescribió con este caso, que está medido, en lugar del que no lo estaba.

### `required` y DataAnnotations no dan el mismo `400`

Los DTO usan `required` (como los mensajes de Contracts) **y** `[Required]`. Son dos mecanismos distintos y producen errores distintos, cosa que conviene saber antes de que 1.7 escriba asserts contra ellos.

Una propiedad **presente pero vacía** la caza la DataAnnotation, y el error va con la clave del campo:

```json
{"errors":{"Sku":["The Sku field is required."],
           "Price":["The field Price must be between 0.01 and 9999999999999999.99."],
           "Stock":["The field Stock must be between 0 and 2147483647."]}}
```

Una propiedad **ausente** la caza `System.Text.Json` antes de que exista el modelo, así que la clave es `$` y el mensaje es de deserialización:

```json
{"errors":{"$":["JSON deserialization for type 'Catalog.API.Models.CreateProductRequest' was missing required properties including: 'name'."],
           "request":["The request field is required."]}}
```

Los dos son `400` con forma `ValidationProblemDetails`, así que un cliente no se rompe. Pero **la clave no es el nombre del campo en el segundo caso**, y aparece un `"request"` de propina que no se corresponde con nada que el cliente haya mandado. Un test de 1.7 que afirme `errors["name"]` para un cuerpo incompleto fallaría.

### El `IDENTITY` no empieza en 1, y una inserción fallida quema un número

El primer producto insertado en una tabla recién creada y vacía salió con **`Id = 1002`**:

```
Location: http://localhost:5124/products/1002
```

Es el caché de identidad de SQL Server: tras un reinicio del servicio —y el contenedor se había reiniciado— el siguiente valor arranca en un bloque nuevo de 1000, no donde se quedó. No es un fallo y no hay que "arreglarlo" con `DBCC CHECKIDENT`.

Lo que importa es la consecuencia: **nada puede asumir que el primer producto tiene `Id = 1`**. El seed de 1.4 y los tests de 1.7 tienen que leer el id que devuelve el `201`, no darlo por supuesto. En la tanda de pruebas los ids salieron `1002`, luego `1004`, luego `1005` — el `1003` se lo llevó el `POST` que acabó en `409`: **`IDENTITY` no es transaccional**, y un `INSERT` que se echa atrás ya ha consumido su número.

### `curl` en PowerShell 5.1 no es curl

`curl` es un alias de `Invoke-WebRequest`, que no entiende `-X`, `-d` ni `-s`. Hay que escribir **`curl.exe`**. Y para cuerpos con caracteres no ASCII ni siquiera basta: pasarlos por bash los manda en la codificación equivocada y ASP.NET responde `The JSON value could not be converted to System.String`, que parece un error de la API y es del terminal. La prueba de los 26 `ß` hubo que rehacerla con `Invoke-WebRequest` y `[System.Text.Encoding]::UTF8.GetBytes($body)` explícito.

### `Apply` valida en locales y asigna en bloque

El constructor de 1.1 asignaba `Sku`, `Name` y `Description` y *después* validaba `price` y `stock`. En un constructor da igual: si algo lanza, el objeto se descarta.

En `Update` **no da igual**. La entidad ya está rastreada por el ChangeTracker, así que un precio inválido la dejaría con el `Sku` y el `Name` nuevos y el precio viejo, y esa entidad medio mutada seguiría ahí. Por eso `Apply` valida todo en variables locales y asigna las seis propiedades al final: el `Update` es atómico o no ocurre.

El precio de compartir `Apply` entre el constructor y `Update` es un `[MemberNotNull(nameof(Sku), nameof(Name), nameof(Description))]`. Sin él el compilador no ve que el constructor público inicializa las tres propiedades no anulables y avisa con `CS8618`.

### El `PUT` es un reemplazo, y se nota

`PUT /products/1002` sin `imageUrl` en el cuerpo **borra la imagen que había**:

```
antes:  "imageUrl":"/img/lap-14.png"
PUT sin imageUrl -> 204
después: "imageUrl":null
```

Es lo correcto para el verbo —`PUT` manda el recurso entero— pero es una trampa clásica para el frontend de la Fase 6, que tendrá que mandar el producto completo y no solo los campos del formulario que tenga a mano. Si eso resulta incómodo, la respuesta es un `PATCH`, no relajar el `PUT`.

---

## Verificación

Build y suite de arquitectura:

```
Build succeeded.
    0 Warning(s)
    0 Error(s)

Test run summary: Passed!
  total: 12
  failed: 0
  succeeded: 12
  skipped: 0
  duration: 2s 552ms
```

Con `docker compose up -d` y `dotnet run --project src/Services/Catalog/Catalog.API` (perfil `http`, puerto 5124):

| # | Comprobación | Resultado |
|---|---|---|
| 1 | `POST /products` válido, `sku: "lap-14"` | `201`, `Location: http://localhost:5124/products/1002`, cuerpo con `"sku":"LAP-14"` — **normalizado y ruta en minúsculas** |
| 2 | `GET /products` | `200`, lista con el producto |
| 3 | `GET /products/1002` | `200` |
| 4 | `GET /products/999` | `404` con `ProblemDetails` |
| 5 | `POST` repitiendo el sku como `"LaP-14"` | `409` — `{"title":"Sku duplicado","detail":"Ya existe un producto con el Sku 'LAP-14'."}` |
| 6 | `POST` con `sku:""`, `price:-1`, `stock:-5` | `400` nombrando **los tres** campos |
| 7 | `POST` sin la propiedad `name` | `400`, pero con clave `$` — ver *Detalles* |
| 8 | `POST` con `sku` de 26 `ß` | `201` — la hipótesis del `ß → SS` no se cumple, ver *Detalles* |
| 9 | `POST` con `imageUrl: "   "` | `400` con clave `imageUrl` — el hueco real que cubre el `catch` |
| 10 | `PUT /products/1002` cambiando sku, nombre, precio y stock | `204`; el `GET` posterior devuelve `LAP-14-V2`, `999.00`, `7` y **`imageUrl: null`** |
| 11 | `PUT /products/999` | `404` |
| 12 | `PUT /products/1002` con el sku de otro producto (`"mou-1"`) | `409` — el conflicto también aplica al `PUT` |
| 13 | `DELETE /products/1004` | `204` |
| 14 | `DELETE /products/1004` otra vez | `404` |
| 15 | `GET /openapi/v1.json` | Las cinco operaciones, con las rutas en minúsculas |

Operaciones publicadas en el documento OpenAPI:

```
/products       ->  GET, POST
/products/{id}  ->  GET, PUT, DELETE
```

Estado en la base, consultado como `catalog_user` (no como `sa` — regla 1):

```
Id          Sku          Name           Price     Stock   ImageUrl
----------- ------------ -------------- --------- ------- --------
       1002 LAP-14-V2    Laptop 14 Pro    999.00        7 (null)
       1005 MOU-1        Raton             19.90       50 (null)
```

Los `Sku` en mayúsculas, el precio con la escala de `decimal(18,2)` y la fila borrada ausente. **Las dos filas se eliminaron después** con el propio `DELETE`, así que la tabla queda vacía para el seed de 1.4:

```
GET /products -> 200 []
```

---

## Pendiente

- **1.4** — el seed. Debe leer los ids que asigne `IDENTITY`, no suponer que empiezan en 1 (ver *Detalles*), y es quien fijará por fin qué formato de `Sku` usa el catálogo — la pregunta que la decisión 9 de 1.1 dejó abierta al no exigir regex.
- **1.5** — la UI de OpenAPI con Scalar. El documento JSON ya se genera y los `[ProducesResponseType]` ya describen los cuatro códigos de cada acción; falta solo la interfaz.
- **1.7** — los tests de estos endpoints con `WebApplicationFactory` + Testcontainers. Los quince casos de la tabla de arriba son su especificación; los tres que conviene no perder son el `409` en `POST` **y** en `PUT`, el `400` de `imageUrl` en blanco y la diferencia de forma entre el `400` de DataAnnotation y el de `required`.
- **Paginación de `GET /products`** — sin ella mientras el catálogo quepa en una pantalla. Entra si 6.2 la necesita.
- **Referencia colgante `OrderLine.ProductId` → producto borrado** — Fase 3, cuando exista un segundo servicio que guarde ids de producto. Ver decisión 8.
- **`PATCH`** — si el frontend de 6.2 encuentra incómodo mandar el producto entero. No se relaja el `PUT` para evitarlo.
