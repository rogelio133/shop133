# Fase 1.4 — Seed de datos de prueba

**Fecha:** 2026-08-19 · **Estado:** completado · **Roadmap:** [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md), punto 1.4

---

## Objetivo

Llenar el catálogo. Hasta aquí `CatalogDb.dbo.Products` estaba **vacía**: los cinco endpoints de 1.3 existían pero no se podían ejercitar sin dar de alta productos a mano en cada arranque, 1.5 no tendría nada que enseñar en Swagger y la vista de catálogo de 6.2 se montaría sobre una lista vacía.

El catálogo pedido es de **souvenirs**, en cinco categorías —tazas, llaveros, playeras, pines y libretas— con **10 productos cada una**. Y la categoría no es una columna de texto ni un `enum`: es un **catálogo en base de datos**, una tabla con clave propia y una clave foránea desde `Products`.

Eso es lo que convierte este punto en algo más que un `INSERT`. Entra la **primera relación del modelo**, y con ella una segunda entidad, una migración de esquema, un cambio en los tres DTO de 1.3 y un endpoint nuevo. El seed es el entregable que da nombre al punto; la relación es el trabajo.

Este punto también salda una deuda que 1.1 dejó apuntada: la decisión 9 de [fase_1_1.md](fase_1_1.md) se negó a exigir un formato de `Sku` «antes de saber qué códigos usa el seed de 1.4». Ya se sabe — ver la decisión 6.

**Fuera de alcance deliberadamente:** paginación y filtro `GET /products?categoryId=` (entran si 6.2 los necesita), CRUD de categorías (decisión 4), imágenes reales —solo se guardan las rutas—, un `Slug` de categoría para las URLs de la Fase 6, y los tests, que son 1.7. **No se añadió ningún paquete NuGet.**

---

## Decisiones

### 1. La categoría es una tabla, no un `enum`

Lo pidió el enunciado del punto, pero conviene dejar escrito qué se compra y qué se paga, porque es una disyuntiva que reaparece en cada servicio.

*Descartado* un `enum Category { Tazas, Llaveros, … }` persistido como `int` o como `string`. Es más barato: cero tablas, cero joins, y el compilador impide una categoría inválida. Se descarta por dos motivos concretos. El primero, que añadir una categoría obliga a **recompilar y desplegar** Catalog.API — para un catálogo de tienda, que es dato de negocio y no estructura, eso es poner un despliegue por medio de una operación de administración. El segundo, que el nombre que ve el usuario queda atrapado dentro del ensamblado: `Tazas` como identificador de C# no puede llevar tildes, espacios ni cambiar de idioma, así que tarde o temprano aparece un diccionario `enum → string` en la capa de presentación, y a partir de ese momento hay dos fuentes de verdad.

**Lo que se paga:** una clave foránea, un `Include` en cada lectura y un viaje extra a la base al dar de alta un producto (decisión 5). La comprobación de que la categoría existe deja de hacerla el compilador y pasa a hacerla el motor — que es exactamente el mismo intercambio que la regla 1 de `CLAUDE.md` impone entre servicios, pero aquí dentro de una sola base.

### 2. Seed con `HasData`, dentro de una migración

*Descartado* un `CatalogSeeder` que corra al arrancar Catalog.API si la tabla está vacía. Es el patrón más común y tiene una ventaja real: los datos se corrigen sin generar una migración. Se descarta porque choca de frente con una regla ya establecida en 1.2 —**nada se ejecuta contra la base al arrancar**, ni siquiera `Database.Migrate()`— y porque un seeder condicional («si está vacía») es un estado global que no sobrevive a dos instancias del servicio arrancando a la vez.

*Descartado* también un `02-seed-catalog.sql` en `db/init/`, que [fase_0_4.md](fase_0_4.md) dejó previsto al numerar los scripts. Rompe la relación entre esquema y datos: ese script lo ejecuta `db-init` justo después de crear las bases, cuando la tabla `Products` **todavía no existe** —la crean las migraciones de EF, mucho después—, así que habría que ordenarlo a mano y mantener el `INSERT` sincronizado con un esquema que vive en otro sitio.

`HasData` gana por una razón que se cobra en 1.7: el seed viaja **dentro de la migración**, así que la fixture de Testcontainers que llame a `MigrateAsync()` obtiene los 50 productos sin escribir una línea extra, y los obtiene idénticos a los de la base de desarrollo.

**Lo que se paga**, y es un precio real: corregir un precio no es editar un archivo, es editar un archivo **y generar una migración** que lo aplique con `UpdateData`. Ver también el detalle de que `HasData` no pasa por el constructor de la entidad.

### 3. Dos migraciones: esquema y datos por separado

*Descartado* generar una sola migración con la tabla, la FK y las 55 filas. Es un comando menos y es lo que sale por defecto si se escribe todo de una vez.

Se partió en `AddProductCategories` (tabla `Categories`, columna `Products.CategoryId`, índice y FK) y `SeedSouvenirCatalog` (los `InsertData`) por dos motivos. El primero es que **se pueden revertir por separado**: `dotnet ef database update AddProductCategories` deja la base con el esquema nuevo y sin datos de prueba, que es exactamente lo que querría un entorno que no sea el de desarrollo. Está verificado abajo. El segundo es que el diff de cada una se lee por lo que es: 60 líneas de DDL en una, 305 de datos en la otra, sin mezclarse.

El coste es un paso extra en el proceso: hay que escribir las configuraciones **sin** el `HasData`, generar la primera migración, añadir el `HasData` y generar la segunda. Si se escribe todo de golpe, la primera migración se lleva los datos dentro y ya no hay forma de separarlos sin borrarla.

### 4. Solo `GET /categories`, no un CRUD

*Descartado* un `CategoriesController` con los cinco verbos, simétrico al de productos. Suena consistente y es lo que pide el instinto.

Se descarta porque las cinco filas son fijas: las pone el seed y añadir una sexta es editar `CatalogSeedData` y generar una migración —lo que además deja constancia del cambio en el historial del repositorio, que es más de lo que daría un `POST`—. A cambio, un CRUD completo obligaría a resolver dos caminos de error nuevos que hoy no tienen respuesta: qué devuelve un `POST` que choca con el índice único del nombre, y sobre todo qué hace un `DELETE` con los 10 productos que cuelgan de esa categoría. Escribir esos dos casos para una operación que nadie va a ejecutar es complejidad a cambio de nada.

*Descartado* también el otro extremo, no exponerlas en absoluto. Sin este endpoint, un cliente no tiene forma de averiguar qué `CategoryId` es válido salvo leyendo el código fuente, y el 400 de la decisión 5 sería un callejón sin salida: «no existe la categoría 999» sin decir cuáles existen.

Se ordenan **por nombre y no por id**: el orden de los ids es el de inserción del seed, que no significa nada para quien pinta un menú.

### 5. La categoría inexistente se comprueba antes, y sale como 400

*Descartado* guardar sin comprobar y traducir el error **547** de SQL Server en `DbUpdateExceptionExtensions`, que es exactamente lo que 1.3 ya hace con el 2601/2627 del índice único. Ahorraría un viaje a la base y sería consistente con lo que hay.

Se descarta porque **los dos casos no son iguales, aunque lo parezcan**. La unicidad de un `Sku` solo la puede responder el conjunto entero de filas en el instante del `INSERT`: comprobarla antes con un `SELECT` sería una condición de carrera con nombre propio, así que allí la excepción no es un atajo, es la única vía correcta. Que una categoría exista, en cambio, es una consulta corriente sobre una tabla de cinco filas que no cambia. Además el 547 no dice **qué** clave foránea falló, así que el mensaje de error saldría peor justo en el caso que más se va a ver.

**400 y no 404**: el que no existe es un campo del **cuerpo**, no el recurso al que apunta la URL. Sale como `ValidationProblemDetails` nombrando `CategoryId`, igual que los errores de DataAnnotations, para que el cliente no tenga que distinguir dos formatos de error de entrada.

En el `PUT`, el **404 va primero**: si el producto de la URL no existe, un `CategoryId` malo en el cuerpo ya da igual — el recurso que se pretendía reemplazar no está.

La comprobación devuelve **la entidad y no un `bool`**, y eso no es un detalle de estilo: al quedar rastreada por el contexto, EF rellena por *fix-up* la navegación `Product.Category` del producto que se añade justo después, y es lo que permite que el `201` incluya el nombre de la categoría sin una segunda consulta.

### 6. Formato de `Sku` fijado: `<4 letras>-<3 dígitos>`

La decisión 9 de [fase_1_1.md](fase_1_1.md) descartó exigir un regex «antes de saber qué códigos usa el seed de 1.4». Aquí ya hay 50 casos delante, y el formato que usan es `TAZA-001`, `LLAV-001`, `PLAY-001`, `PINS-001`, `LIBR-001`.

Cuatro letras y no tres, que es lo que se había esbozado al planificar: `TAZA-001` se lee como la palabra que es, y `TAZ-001` obliga a descifrar una abreviatura. `PINS` y `LLAV` sí son abreviaturas —`PIN` chocaba con la lectura de "pin" en inglés y `LLAV` no tiene una forma de cuatro letras mejor—, pero la regla se mantiene uniforme: cuatro caracteres siempre, para que todos los códigos tengan el mismo ancho.

**Y sigue sin haber regex.** Se documenta como convención y nada la impone. El motivo es el mismo que en 1.1, no inercia: 50 filas escritas por el propio proyecto no son evidencia de que un catálogo importado use el mismo formato, y un patrón en la entidad rechazaría el primer código legítimo que llegue de fuera.

**El prefijo tampoco está atado a la categoría por el esquema**: nada impide un `TAZA-011` con `CategoryId = Pines`. Es coherencia de datos que mantiene quien da de alta, no una invariante — y la decisión 7 explica por qué no se intentó convertirla en una.

### 7. `Category` no tiene columna `Code`

*Descartado* darle a `Category` un `Code` de cuatro letras (`TAZA`, `LLAV`, …) del que derivara el prefijo del `Sku`. Es atractivo: relaciona las dos cosas y da un candidato natural para el `Slug` de la Fase 6.

Se descarta porque **sugeriría una regla que el sistema no comprueba**. Teniendo la columna, cualquiera esperaría que `TAZA-004` no pueda estar en la categoría `Pines`; y para que eso fuera cierto haría falta validarlo en cada alta y en cada cambio de categoría, con la pregunta añadida de qué pasa al recolocar un producto —¿se le renumera el `Sku`, contradiciendo que el `Id` es lo único inmutable?—. Fingir una regla que no se aplica es peor que no tenerla: la primera vez que alguien confíe en ella, se rompe en silencio.

Por el mismo motivo `Category` tampoco tiene `Slug`: entra en la Fase 6 si 6.2 lo necesita para la URL, con el caso de uso delante.

### 8. `DeleteBehavior.Restrict` explícito

EF Core pone `Cascade` por defecto en una clave foránea obligatoria. Aquí eso significaría que borrar la categoría «Tazas» se lleva por delante sus 10 productos sin que nadie lo haya pedido.

Se declara `Restrict`. Hoy ni siquiera existe un endpoint para borrar una categoría, así que la restricción no protege de nada que se pueda hacer — **y esa es exactamente la idea**: la guarda tiene que estar puesta antes de que exista la operación peligrosa, no después del primer accidente. Verificado abajo con un `DELETE` directo contra la base.

### 9. Navegación unidireccional y anulable

`Product` tiene `Category`; `Category` **no** tiene una colección `Products`. *Descartado* hacerla bidireccional: nadie necesita hoy `category.Products`, y una relación con las dos puntas obliga a mantenerlas sincronizadas a mano en memoria, con el riesgo clásico de que una consulta vea una y no la otra.

`Product.Category` es **anulable aunque `CategoryId` sea obligatoria**, y la distinción importa: `null` no significa «producto sin categoría» —eso es imposible—, significa «esta consulta no la cargó». Como cualquier `null` ahí es un error de quien escribió la consulta y no un estado válido, `ProductResponse.From` no lo deja pasar con un `!` silencioso: lanza `InvalidOperationException` con el mensaje que dice cómo arreglarlo («falta un `Include`»), en vez de dejar que salga un `NullReferenceException` a veinte frames de distancia.

### 10. `CategoryName` plano en la respuesta, no anidado

*Descartado* devolver solo el `CategoryId` y que el cliente lo cruce contra `GET /categories`. Mantiene la consulta sin join, pero convierte cada listado en dos llamadas y obliga a **cada** consumidor a escribir el mismo cruce.

*Descartado* también anidarlo (`"category": { "id": …, "name": … }`), que es más expresivo. Hoy todo el DTO es plano y un solo campo anidado no justifica un segundo tipo de respuesta ni la asimetría.

### 11. El nombre de la categoría no se normaliza a mayúsculas

`Product.Sku` se guarda con `ToUpperInvariant()` (decisión 9 de 1.1). `Category.Name` **no**, y no es un olvido: el `Sku` es un código de máquina que nadie lee en una pantalla, mientras que esto es texto de interfaz — «TAZAS» en una pestaña del catálogo sería un error de presentación introducido por la capa de datos.

La consecuencia es que la unicidad del nombre se apoya en la *collation* por defecto de SQL Server, que es *case-insensitive* — justo lo que 1.1 llamó «funcionar por accidente y no por diseño». Aquí se acepta porque el catálogo son cinco filas fijas que no se alimentan de entrada de usuario. **El día que exista un `POST /categories`, esta decisión es la que hay que releer.**

---

## Cambios

| Archivo | Rol |
|---|---|
| [Catalog.Infrastructure/Entities/Category.cs](../src/Services/Catalog/Catalog.Infrastructure/Entities/Category.cs) | **Nuevo.** La segunda entidad del proyecto. `sealed class` con setters privados y constructor que valida, calcada del estilo de `Product`. |
| [Catalog.Infrastructure/Entities/Product.cs](../src/Services/Catalog/Catalog.Infrastructure/Entities/Product.cs) | **Modificado.** Gana `CategoryId` y la navegación `Category`. El parámetro entra en el constructor y en `Update(...)` **antes** de `imageUrl`, para que el opcional siga siendo el último. |
| [Catalog.Infrastructure/Persistence/Configurations/CategoryConfiguration.cs](../src/Services/Catalog/Catalog.Infrastructure/Persistence/Configurations/CategoryConfiguration.cs) | **Nuevo.** Mapeo de `Categories`: `nvarchar(100)`, índice único sobre `Name` y el `HasData` de las 5 filas. |
| [Catalog.Infrastructure/Persistence/Configurations/ProductConfiguration.cs](../src/Services/Catalog/Catalog.Infrastructure/Persistence/Configurations/ProductConfiguration.cs) | **Modificado.** La relación `HasOne/WithMany` con `DeleteBehavior.Restrict` y el `HasData` de los 50 productos. |
| [Catalog.Infrastructure/Persistence/Seed/CatalogSeedData.cs](../src/Services/Catalog/Catalog.Infrastructure/Persistence/Seed/CatalogSeedData.cs) | **Nuevo.** Los datos: 5 categorías y 50 productos como objetos anónimos. En su propio archivo para no tapar el esquema en `Configurations/`. |
| [Catalog.Infrastructure/Persistence/CatalogDbContext.cs](../src/Services/Catalog/Catalog.Infrastructure/Persistence/CatalogDbContext.cs) | **Modificado.** `DbSet<Category> Categories` y el segundo `ApplyConfiguration`. |
| `Catalog.Infrastructure/Migrations/20260819234153_AddProductCategories.cs` | **Nuevo (generado).** Tabla `Categories`, columna `Products.CategoryId`, los dos índices y la FK. |
| `Catalog.Infrastructure/Migrations/20260819234304_SeedSouvenirCatalog.cs` | **Nuevo (generado).** 305 líneas de `InsertData`, con `DeleteData` de las 55 filas en el `Down`. |
| [Catalog.API/Models/CreateProductRequest.cs](../src/Services/Catalog/Catalog.API/Models/CreateProductRequest.cs) | **Modificado.** `CategoryId` obligatorio con `[Range(1, int.MaxValue)]`. |
| [Catalog.API/Models/UpdateProductRequest.cs](../src/Services/Catalog/Catalog.API/Models/UpdateProductRequest.cs) | **Modificado.** Igual: recolocar un producto es una operación normal de catálogo. |
| [Catalog.API/Models/ProductResponse.cs](../src/Services/Catalog/Catalog.API/Models/ProductResponse.cs) | **Modificado.** `CategoryId` + `CategoryName`, y el `From` que falla con mensaje si falta el `Include`. |
| [Catalog.API/Models/CategoryResponse.cs](../src/Services/Catalog/Catalog.API/Models/CategoryResponse.cs) | **Nuevo.** `Id` + `Name`. |
| [Catalog.API/Controllers/CategoriesController.cs](../src/Services/Catalog/Catalog.API/Controllers/CategoriesController.cs) | **Nuevo.** Un solo `GET /categories`, ordenado por nombre. |
| [Catalog.API/Controllers/ProductsController.cs](../src/Services/Catalog/Catalog.API/Controllers/ProductsController.cs) | **Modificado.** `Include` en las dos lecturas, comprobación de categoría en `POST` y `PUT`, y los helpers `FindCategoryOrNull` / `UnknownCategory`. |

**Ningún `.csproj` se tocó y no se añadió ningún paquete NuGet**, así que la regla `EfCorePackages_LiveOnlyIn_InfrastructureProjects` de 1.2 no tenía nada que decir y la suite de arquitectura sigue en **12 tests**.

**`DbUpdateExceptionExtensions.cs` tampoco se tocó**: sigue traduciendo solo el 2601/2627 del índice único, no el 547 de la clave foránea — ver la decisión 5.

Otros archivos: checkbox de 1.4 en el roadmap, fila en [docs/README.md](README.md) y el párrafo de estado de la Fase 1 en [CLAUDE.md](../CLAUDE.md).

---

## Detalles que cuestan tiempo

### `HasData` no pasa por el constructor de la entidad

Es la contrapartida de la decisión 2 y no es evidente. `Product` valida en `Apply(...)` —normaliza el `Sku` con `Trim()` y `ToUpperInvariant()`, exige precio positivo, comprueba longitudes— pero EF materializa las filas de `HasData` **por reflexión**, sin llamar al constructor público. Ninguna de esas guardas se ejecuta sobre las 50 filas del seed.

Consecuencia práctica: los `Sku` de `CatalogSeedData` tienen que estar ya en mayúsculas y sin espacios sobrantes, porque **nada los va a arreglar**. Un `"taza-001"` en el seed acabaría en la base tal cual, y convivirían `taza-001` y `TAZA-001` como dos productos distintos — precisamente lo que la normalización de 1.1 existe para impedir.

### El `IDENTITY` no se reinicia, y esa es la buena noticia

`HasData` inserta los ids 1–50 rodeados de `SET IDENTITY_INSERT [Products] ON/OFF`. Eso **no mueve el contador de identidad**, que tras los reinicios de servicio de 1.3 andaba por 1000 y pico. El primer `POST /products` después del seed devolvió:

```
STATUS 201 | Location: http://localhost:5124/products/1007
```

Es decir: el seed ocupa del 1 al 50 y las altas nuevas siguen en el 1007. No hay colisión y no hay nada que arreglar — la regla de `CLAUDE.md` de que **nada puede suponer que `Product.Id` empieza en 1** sigue intacta, ahora conviviendo con un seed que sí usa ids bajos porque `HasData` no tiene otra opción.

### `required` convierte un campo que falta en error de deserialización, no de validación

`CreateProductRequest.CategoryId` lleva `[Range(1, int.MaxValue)]`, pero un cuerpo que **omite** el campo no produce el error de rango que uno esperaría. Lo intercepta antes el modificador `required` del record, en la deserialización:

```json
{
  "errors": {
    "$": ["JSON deserialization for type 'Catalog.API.Models.CreateProductRequest' was missing required properties including: 'categoryId'."],
    "request": ["The request field is required."]
  }
}
```

Sigue siendo un 400 con `ValidationProblemDetails`, así que el contrato de error no cambia — pero la clave del error es `$` y no `CategoryId`, y el mensaje menciona el nombre del tipo de C#. Conviene saberlo antes de escribir el test de 1.7 que afirme «falta la categoría ⇒ 400 nombrando el campo»: eso solo es cierto cuando el campo **viene con un valor inválido**, no cuando falta.

### Partir el seed en dos migraciones hay que planificarlo antes de escribirlo

Para que `AddProductCategories` no se llevara los datos dentro, las dos configuraciones tuvieron que escribirse **primero sin el `HasData`**, generar esa migración, y solo entonces añadir las dos llamadas y generar `SeedSouvenirCatalog`. En el código quedó constancia del paso intermedio con un comentario `// SEED-PENDIENTE` que se sustituyó en la segunda pasada.

Si se escribe todo de una vez, la primera migración incluye los 55 `InsertData` y ya no hay forma de separarlos sin borrarla y volver a empezar — que con la migración aún sin aplicar es barato, pero deja de serlo en cuanto alguien la ejecuta.

### La navegación se rellena sola en el `POST`, pero solo porque la categoría está rastreada

El `201` devuelve `"categoryName": "Tazas"` sin una segunda consulta. Funciona por el *fix-up* de EF Core: `FindCategoryOrNull` devuelve la entidad **rastreada** por el contexto, y al añadir el `Product` con ese `CategoryId`, EF conecta las dos y rellena `product.Category`.

Es la razón por la que ese helper devuelve `Category?` y no `bool`. Con un `AnyAsync` la comprobación sería igual de válida pero la navegación quedaría a `null`, y `ProductResponse.From` lanzaría su `InvalidOperationException` en el camino feliz del alta.

---

## Verificación

### Build y tests

```
Catalog.Infrastructure -> ...\Catalog.Infrastructure.dll
Shop133.Contracts -> ...\Shop133.Contracts.dll
Catalog.API -> ...\Catalog.API.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Test run summary: Passed!
  total: 12
  failed: 0
  succeeded: 12
```

### Migraciones aplicadas

```
20260819001038_InitialCreate
20260819234153_AddProductCategories
20260819234304_SeedSouvenirCatalog
```

### Estado de la base

```
Id  Categoria   Productos  PrecioMin  PrecioMax
--- ----------- ---------- ---------- ----------
  1 Tazas               10     149.00     289.00
  2 Llaveros            10      45.00      95.00
  3 Playeras            10     249.00     399.00
  4 Pines               10      35.00      79.00
  5 Libretas            10      89.00     179.00

TotalProductos   50
StocksDistintos  50
```

Los 50 `Stock` son distintos entre sí a propósito: si un copia-pega hubiera duplicado una fila, se vería aquí sin tener que revisar las 50.

### Comprobaciones

| # | Comprobación | Resultado |
|---|---|---|
| 1 | `SELECT COUNT(*) FROM Categories` | **5** ✓ |
| 2 | `SELECT COUNT(*) FROM Products` | **50** ✓ |
| 3 | Productos por categoría | **10** en las cinco ✓ |
| 4 | `GET /categories` | `200`, 5 elementos, ordenados alfabéticamente (Libretas, Llaveros, Pines, Playeras, Tazas) ✓ |
| 5 | `GET /products` | `200`, 50 elementos, todos con `categoryId` y `categoryName` ✓ |
| 6 | `GET /products/25` | `200` — `PLAY-005`, `"categoryName": "Playeras"` ✓ |
| 7 | `POST` con `categoryId: 999` | `400` nombrando `CategoryId` ✓ |
| 8 | `POST` con `sku: "TAZA-001"` | `409` «Ya existe un producto con el Sku 'TAZA-001'» ✓ |
| 9 | `POST` omitiendo `categoryId` | `400`, pero por deserialización — ver el detalle de `required` ✓ |
| 10 | `POST` válido con `sku: "taza-011"` | `201`, `Location` = `/products/1007`, `Sku` normalizado a `TAZA-011` ✓ |
| 11 | `PUT` moviendo ese producto de Tazas a Pines | `204`, y la relectura devuelve `"categoryName": "Pines"` ✓ |
| 12 | `DELETE` de ese producto | `204`, el catálogo vuelve a 50 ✓ |
| 13 | `DELETE FROM Categories WHERE Id = 1` directo en SQL | **`Msg 547`** — la FK `Restrict` lo bloquea ✓ |
| 14 | Revertir solo el seed (`database update AddProductCategories`) | `Productos = 0`, `Categorias = 0`, tabla `Categories` **intacta** ✓ |
| 15 | Volver a aplicar | `Productos = 50`, `Categorias = 5` ✓ |

La 13 y la 14 son las que justifican las decisiones 8 y 3: sin ellas, «puse `Restrict`» y «lo partí en dos migraciones» serían afirmaciones sin comprobar.

### El 409 y la normalización, en la misma prueba

El alta de la comprobación 10 se mandó con `"sku": "taza-011"` en minúsculas y volvió como `"sku": "TAZA-011"`. Es la guarda de `Apply(...)` de 1.1 funcionando sobre una entrada que sí pasa por el constructor — a diferencia de las 50 filas del seed, que no lo hacen.

---

## Pendiente

- **1.5 (Swagger/OpenAPI)** — el catálogo ya tiene 50 productos y 5 categorías que enseñar, que era la condición para que ese punto sirviera de algo. También debería documentar el `400` de categoría inexistente.
- **1.7 (`Catalog.Tests`)** — la fixture de Testcontainers obtiene el seed gratis al llamar a `MigrateAsync()`, que es lo que compró la decisión 2. Ojo con dos cosas: los tests arrancan con **50 filas**, no con una base vacía, y el aserto de «falta la categoría ⇒ 400» tiene que distinguir el campo ausente del campo inválido (ver el detalle de `required`).
- **Filtro `GET /products?categoryId=`** — el caso de uso obvio de tener categorías, pero no hay consumidor hasta 6.2. Entra ahí, junto con la paginación que 1.3 también dejó aplazada.
- **`Slug` en `Category`** — Fase 6, si 6.2 necesita URLs del tipo `/catalogo/tazas`. Deliberadamente no se adelantó (decisión 7).
- **CRUD de categorías** — sin caso de uso. Si alguna vez entra, la decisión 11 (el nombre sin normalizar, apoyado en la *collation*) es la primera que hay que releer.
- **Las imágenes** — solo se guardan las rutas (`/img/products/taza-001.jpg`). Los archivos los sirve `Shop133.Web` en la Fase 6; hasta entonces son enlaces rotos, que es exactamente lo que la decisión 8 de 1.1 previó al no exigir URI absoluta.
- **`OrderLine` y las categorías** — el snapshot de `OrderLine` congela `ProductSku`, `ProductName` y `UnitPrice`, y **no** lleva categoría. No es un olvido de este punto: si la Fase 2 o 3 decide que un pedido necesita saber la categoría de lo que se compró, es una sexta propiedad del snapshot y un cambio de contrato, no un `join` — `orders_user` no puede ver `CatalogDb`.
