# Fase 1.5 — Swagger/OpenAPI habilitado

**Fecha:** 2026-08-20 · **Estado:** completado · **Roadmap:** [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md), punto 1.5

---

## Objetivo

Poner cara a `Catalog.API`. El documento OpenAPI **ya se generaba** desde 1.2 —la plantilla de .NET 10 trae `AddOpenApi()` y `MapOpenApi()`, y `GET /openapi/v1.json` respondía `200` desde entonces—, así que este punto no es «activar OpenAPI»: es cerrar las dos carencias que 1.3 y 1.4 dejaron apuntadas por escrito en su sección *Pendiente*.

La primera es que **no había interfaz**. Un JSON de 19 KB documenta el API para una máquina; para una persona, la diferencia entre eso y una página navegable es la diferencia entre poder ejercitar el catálogo y tener que escribir `curl` a mano. Ahí entra Scalar.

La segunda es que **el documento no decía nada que el código no dijera ya**. Los `[ProducesResponseType]` de 1.3 declaraban los códigos de estado, y los DTO aportaban los esquemas, pero ninguna operación llevaba una línea de prosa: nada explicaba por qué un `categoryId` inexistente sale como `400` y no como `404`, ni que el `409` del `PUT` existe porque el `Sku` es modificable, ni que el `Id` no empieza en 1. Eso es justo lo que el punto tenía que resolver — [fase_1_4.md](fase_1_4.md) lo dejó dicho: *«también debería documentar el `400` de categoría inexistente»*.

Y hacerlo aquí, y no antes, tenía una condición previa que ya se cumple: 1.4 llenó el catálogo. Una UI contra una tabla vacía enseña formularios; contra los 50 productos del seed, se ejecuta.

**Fuera de alcance deliberadamente:** los otros cuatro `.API` (decisión 5), `securitySchemes` y el candado de autenticación, que son 8.1; agregar los cinco documentos en un solo Scalar detrás del Gateway, que es la Fase 5; servir la UI desde el contenedor, que depende del Dockerfile de 1.6 —aquí solo se deja el servicio preparado para que lo haga—; y los tests, que son 1.7 y no cubren la interfaz. **Se añadió un paquete NuGet, `Scalar.AspNetCore`, autorizado antes de tocar el `.csproj`.**

---

## Decisiones

### 1. Scalar y no Swashbuckle

La elección estaba preescrita en `CLAUDE.md`, pero conviene dejar el motivo con nombre porque el instinto de cualquiera que venga de .NET 6 es escribir `AddSwaggerGen()`.

*Descartado* **Swashbuckle**. Sigue funcionando y sigue siendo lo que sale al buscar «swagger .NET». Se descarta porque **duplicaría el trabajo**: Swashbuckle no es una interfaz, es un generador de documento *con* interfaz — inspeccionaría la aplicación por su cuenta para producir su propio JSON, en paralelo al que ya produce `Microsoft.AspNetCore.OpenApi`. Habría dos documentos que pueden discrepar y dos sitios donde configurar lo mismo. Además salió de las plantillas en .NET 9, así que su integración con lo nuevo va por detrás.

*Descartado* también **quedarse solo con el JSON** y navegarlo con un editor. Es gratis y es lo que había: precisamente lo que 1.3 anotó como pendiente.

Scalar gana porque hace **una sola cosa**: leer `/openapi/v1.json` y pintarlo. No inspecciona la aplicación, no tiene opinión sobre cómo se genera el documento y se puede quitar borrando dos líneas. Es MIT y trae *target* `net10.0` propio — sin la trampa de licencia de MassTransit 9 o FluentAssertions 8, que es una comprobación que en este proyecto ya se hace por costumbre antes de añadir nada.

### 2. La prosa va en `[EndpointSummary]`, no en los comentarios XML

Esta es la decisión de fondo del punto y merece el espacio.

.NET 10 sabe leer los comentarios XML del código y volcarlos como descripción de cada endpoint: basta activar `<GenerateDocumentationFile>` en el `.csproj`. Es la opción por defecto, no cuesta escribir nada nuevo y los controllers de 1.3 y 1.4 están **densamente comentados**.

*Descartado*, y por lo mismo que los hace valiosos: esos comentarios **no son documentación de API, son racional de diseño**. Dicen cosas como *«Descartado un CRUD completo simétrico al de ProductsController»*, *«sobre un CRUD, una capa más sería un passthrough»* o *«AsNoTracking porque nada de lo que se lee aquí se va a modificar»*. Están escritos para quien mantiene el servicio y remiten a puntos del roadmap. Publicarlos convertiría la referencia del API en un cuaderno de bitácora: el consumidor no necesita saber qué se descartó, necesita saber qué recibe y qué errores puede esperar.

Son **dos audiencias distintas**, y el error habría sido dejar que un flag del compilador las mezclara porque el texto ya estaba escrito.

`[EndpointSummary]` y `[EndpointDescription]` vienen en el framework —no hacen falta paquetes— y el generador de OpenAPI de .NET 10 los lee a través de `IEndpointSummaryMetadata` / `IEndpointDescriptionMetadata`. El resultado son dos bloques de texto por acción que conviven con los `<summary>` sin tocarlos: los primeros salen al JSON, los segundos se quedan en el código.

**Lo que se paga:** el texto público está en un atributo y el interno en un comentario, a diez centímetros el uno del otro, y nada obliga a mantenerlos coherentes. Y hay duplicación real —el `400` de categoría inexistente ahora está explicado en tres sitios: la entidad, el comentario y el atributo—.

**Lo que además se evita:** `<GenerateDocumentationFile>` habría metido un aviso **CS1591** por cada tipo público sin comentar (los cuatro DTO y sus propiedades), en un proyecto cuyo build sale hoy con `0 Warning(s)`. La salida habría sido silenciarlos con `<NoWarn>`, que es apagar un aviso para poder usar una función que no queríamos.

### 3. La UI se sirve en todos los entornos

La plantilla envuelve `MapOpenApi()` en un `if (app.Environment.IsDevelopment())`. Se quitó.

*Descartado* mantener la guarda, que es la opción prudente y la recomendación habitual. Se descarta por una consecuencia concreta y cercana: **la imagen de 1.6 arranca en `Production`**. Con la guarda, el contenedor no serviría ni el JSON ni la interfaz, y este punto solo existiría al ejecutar desde el IDE — es decir, la Fase 1 cerraría con un servicio en Docker cuya documentación desaparece justo cuando se mete en Docker.

**Lo que se paga**, y es real: la superficie completa del API queda visible para quien alcance el puerto. En un proyecto de aprendizaje que corre en `localhost` el intercambio es evidente, pero deja de serlo el día que esto salga de la máquina. **El punto donde hay que releer esta decisión es la Fase 5**, cuando el Gateway se ponga delante y decida qué expone, y la 8.1, cuando haya autenticación que poner en el documento.

Está verificado abajo arrancando el servicio con `ASPNETCORE_ENVIRONMENT=Production`: sin esa comprobación, «quité la guarda» sería una afirmación sobre el diff, no sobre el comportamiento.

### 4. Los metadatos del documento, con un transformador en línea

Sin tocar nada, el bloque `info` del documento sale como `"title": "Catalog.API | v1"` — el nombre del **ensamblado**, que es un detalle de compilación, no el nombre del API.

Se rellena con un `AddDocumentTransformer` en `Program.cs`. Sin Swashbuckle no hay otra vía para tocar `info`: es el punto de extensión que `Microsoft.AspNetCore.OpenApi` ofrece para el documento completo.

*Descartado* sacarlo a una clase `CatalogDocumentTransformer` en su propia carpeta. Son ocho líneas sin lógica y `Program.cs` es el *composition root*: un archivo más obligaría a saltar entre dos sitios para leer una constante de texto.

La descripción aprovecha para dejar dicho algo que el esquema no puede expresar y que va a importar en la Fase 3: **el `stock` que publica este API es el que muestra el catálogo, no el stock reservable**. Esa distinción vive hoy solo en `CLAUDE.md`; ponerla donde la lee quien consume el API es más barato que corregir la confusión después.

### 5. Solo `Catalog.API`; los otros cuatro no se tocan

Los cinco `.API` del proyecto llevan `Microsoft.AspNetCore.OpenApi` y la llamada a `AddOpenApi()` desde el andamiaje de 0.1.

*Descartado* añadirles Scalar de paso, que costaría una línea en cada uno y dejaría el sistema uniforme. Se descarta porque **ninguno tiene controllers todavía**: su documento sería un `paths: {}` y la UI, una página vacía. Peor aún, sería una página vacía que *parece* funcionar, y por tanto una comprobación falsa cuando la Fase 2 monte `Orders.API`.

Scalar entra en cada servicio en el punto en que ese servicio tenga endpoints — el mismo criterio con el que 1.4 se negó a inventar un `Slug` sin caso de uso.

### 6. Ningún test de arquitectura nuevo

`CLAUDE.md` pide plantearse, ante cada regla, si la suite de 0.6 puede hacerla ejecutable. Aquí la respuesta es que **no hay regla que hacer ejecutable**.

Las cuatro reglas de la suite protegen invariantes estructurales que se rompen **en silencio**: una referencia de proyecto en la dirección equivocada compila igual de bien. «Catalog.API sirve su documentación» no es de esa familia — si Scalar deja de estar, `/scalar` devuelve `404` la primera vez que alguien lo abre. Un test que afirmara «existe el paquete `Scalar.AspNetCore`» comprobaría el `.csproj` contra sí mismo.

Lo que sí se comprobó es que el paquete nuevo **no rompe** la única regla sobre dependencias (`EfCorePackages_LiveOnlyIn_InfrastructureProjects`, que filtra el prefijo `Microsoft.EntityFrameworkCore`). La suite sigue en **12 tests**.

### 7. `launchSettings.json` no se toca

*Descartado* poner `"launchBrowser": true` con `"launchUrl": "scalar"` para que `dotnet run` abra la interfaz. Es un clic menos, pero abriría una pestaña **cada vez** que se levanta el servicio, incluidas las veces —la mayoría— en que solo se quiere el proceso escuchando para lanzarle `curl`. La URL queda documentada aquí y en la línea de *Local UIs* de `CLAUDE.md`, junto a las de RabbitMQ y Jaeger.

---

## Cambios

| Archivo | Rol |
|---|---|
| [Catalog.API/Catalog.API.csproj](../src/Services/Catalog/Catalog.API/Catalog.API.csproj) | **Modificado.** `PackageReference` a `Scalar.AspNetCore` 2.14.14, **sin `PrivateAssets`** —al revés que `.Design`— porque es runtime y tiene que acabar en la imagen de 1.6. |
| [Catalog.API/Program.cs](../src/Services/Catalog/Catalog.API/Program.cs) | **Modificado.** `AddOpenApi(...)` con el transformador de `info`, `MapScalarApiReference()` y la retirada de la guarda `IsDevelopment()`. |
| [Catalog.API/Controllers/ProductsController.cs](../src/Services/Catalog/Catalog.API/Controllers/ProductsController.cs) | **Modificado.** `[EndpointSummary]` + `[EndpointDescription]` en las cinco acciones. Y un arreglo de paso: había un `<summary>` huérfano apilado sobre el de `FindCategoryOrNull` que pertenecía a `ToValidationProblem` — se movió a su sitio. |
| [Catalog.API/Controllers/CategoriesController.cs](../src/Services/Catalog/Catalog.API/Controllers/CategoriesController.cs) | **Modificado.** Los dos atributos en `GetAll`, explicando que es la fuente de los `categoryId` válidos. |

**Ningún archivo nuevo.** El punto entero cabe en cuatro modificaciones, y tres de ellas son texto.

**Ni las entidades, ni el `DbContext`, ni las migraciones, ni los DTO se tocaron**: los esquemas del documento salen de los DTO de 1.3 y 1.4 tal como estaban. Es la señal de que las anotaciones de aquellos puntos ya hacían su trabajo.

Otros archivos: checkbox de 1.5 en el roadmap, fila en [docs/README.md](README.md), y en [CLAUDE.md](../CLAUDE.md) el párrafo de estado de la Fase 1, la tabla de fases y la línea de *Local UIs*.

---

## Detalles que cuestan tiempo

### `OpenApiInfo` ya no vive en `Microsoft.OpenApi.Models`

`Microsoft.AspNetCore.OpenApi` 10.0.11 depende de `Microsoft.OpenApi` **2.7.5**, y la versión 2 movió los tipos: `OpenApiInfo` está en el namespace `Microsoft.OpenApi`, a secas. Todo lo que hay escrito sobre el tema —y todo lo que uno recuerda de .NET 8— dice `using Microsoft.OpenApi.Models;`, que aquí no compila.

Se puede confirmar sin buscar en internet, leyendo el XML del propio paquete en la caché de NuGet:

```
C:\Users\<usuario>\.nuget\packages\microsoft.openapi\2.7.5\lib\netstandard2.0\Microsoft.OpenApi.xml
    <member name="T:Microsoft.OpenApi.OpenApiInfo">
```

### La UI de Scalar está en `/scalar`, no en `/scalar/v1`

En las versiones 1.x de `Scalar.AspNetCore` el prefijo por defecto incluía el nombre del documento. En la 2.x es `/scalar` y el nombre del documento pasa a ser opcional. Medido:

```
/scalar              302  ->  http://localhost:5124/scalar/
/scalar/             200      text/html
/scalar/v1           200      text/html
/openapi/v1.json     200      application/json (19707 bytes)
```

`/scalar` **sin** barra final redirige a `/scalar/`, así que un `curl` sin `-L` devuelve `302` y no `200`. No es un fallo: conviene saberlo antes de escribir la comprobación.

### Probar el entorno `Production` obliga a sacar el connection string de User Secrets

Es la consecuencia directa de la decisión 3, y adelanta trabajo de 1.6. Los User Secrets **solo se cargan en `Development`**; arrancar en `Production` sin más hace saltar la guarda de 1.2 («Falta la configuración `ConnectionStrings:CatalogDb`») antes de llegar a servir nada. Hay que pasarlo por entorno, con el doble guion bajo que ASP.NET traduce a los dos puntos de la clave:

```powershell
$env:ConnectionStrings__CatalogDb = "Server=localhost,1433;Database=CatalogDb;User Id=catalog_user;..."
$env:ASPNETCORE_ENVIRONMENT = "Production"
$env:ASPNETCORE_URLS = "http://localhost:5199"
dotnet run --project src/Services/Catalog/Catalog.API --no-launch-profile
```

El `--no-launch-profile` no es opcional: sin él, `dotnet run` aplica el perfil `http` de `launchSettings.json`, que **fija `ASPNETCORE_ENVIRONMENT=Development`** y pisa la variable — la prueba pasaría sin haber probado nada. Ese mismo par de variables es lo que necesitará el servicio en `docker-compose` en 1.6.

### Los números salen del esquema como `["integer","string"]`

El documento describe cada campo numérico con **dos tipos** y un patrón:

```json
"stock": { "type": ["integer","string"], "pattern": "^-?(?:0|[1-9]\\d*)$", "format": "int32" }
```

No lo causan los `[Range]` de los DTO: `ProductResponse.Id`, que no lleva ninguna anotación, sale exactamente igual. Es cómo el generador de esquemas de .NET 10 expresa en OpenAPI 3.1 que `System.Text.Json` acepta también la forma en cadena. Es correcto y Scalar lo pinta bien; solo desconcierta al leer el JSON crudo.

Lo que sí conviene tener anotado es que **`decimal` sale como `"format": "double"`**. El `Price` se guarda como `decimal(18,2)` en SQL Server (1.2) y se opera como `decimal` en C#, pero el documento anuncia un `double` — porque OpenAPI no tiene un formato estándar para decimales. Un cliente generado a partir de este documento en otro lenguaje usaría coma flotante para dinero. Hoy no hay ningún cliente generado, así que no se corrige; el sitio donde importará es la Fase 6.

Los `[Range]` sí llegan al documento, que era la duda de partida: `price` sale con `minimum: 0.01`, y `categoryId` con `minimum: 1`.

### PowerShell 5.1 no puede usar `Invoke-WebRequest` sin `-UseBasicParsing`

Detalle de entorno, pero costó una tanda de comprobaciones. En sesión no interactiva, `Invoke-WebRequest` intenta levantar el motor de Internet Explorer para analizar el HTML y muere con *«Windows PowerShell is in NonInteractive mode»*, devolviendo `$null` en vez de un error legible. Las opciones son `-UseBasicParsing`, `Invoke-RestMethod` (que no analiza HTML y sirve para el JSON) o `curl.exe`, que en Windows 11 está instalado y es el mismo `curl` de siempre.

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

Los `0 Warning(s)` son la comprobación concreta de la decisión 2: es lo que no habría salido con `<GenerateDocumentationFile>` activado.

### El documento generado

`GET http://localhost:5124/openapi/v1.json` — `openapi: 3.1.1`:

```
title:   shop133 — Catalog API
version: v1
desc:    Catálogo de productos de souvenirs. Es el servicio síncrono ...

GET    /categories        [200]             sum='Lista las categorías del catálogo'   descLen=234
GET    /products          [200]             sum='Lista todos los productos del catálogo' descLen=209
POST   /products          [201,400,409]     sum='Crea un producto'                    descLen=480
GET    /products/{id}     [200,404]         sum='Obtiene un producto por su Id'       descLen=340
PUT    /products/{id}     [204,400,404,409] sum='Reemplaza un producto completo'      descLen=503
DELETE /products/{id}     [204,404]         sum='Borra un producto'                   descLen=258
```

Las seis operaciones llevan `summary` y `description`; los códigos de estado son los cuatro caminos que 1.3 y 1.4 dejaron implementados; y las rutas siguen en minúsculas, es decir, el `LowercaseUrls` de 1.3 no ha regresado.

### Comprobaciones

| # | Comprobación | Resultado |
|---|---|---|
| 1 | `dotnet build` de `Catalog.API` | **0 warnings, 0 errores** ✓ |
| 2 | `dotnet test -- --filter-trait "Category=Fast"` | **12/12** — el paquete nuevo no altera la suite ✓ |
| 3 | `info.title` del documento | `shop133 — Catalog API`, no `Catalog.API \| v1` ✓ |
| 4 | `summary` y `description` en las 6 operaciones | Presentes las 12 cadenas ✓ |
| 5 | Rutas del documento | `/products`, `/products/{id}`, `/categories` — en minúsculas ✓ |
| 6 | Códigos declarados en `PUT` | `204`, `400`, `404`, `409` ✓ |
| 7 | `GET /scalar/` | `200 text/html`, la página de Scalar ✓ |
| 8 | `GET /scalar` (sin barra) | `302` a `/scalar/` ✓ |
| 9 | `GET /scalar/v1` | `200` — el nombre del documento sigue valiendo como ruta ✓ |
| 10 | Fuga de comentarios internos al JSON | **0 apariciones** de `Descartado`, `passthrough`, `AsNoTracking`, `ChangeTracker` ✓ |
| 11 | Arranque con `ASPNETCORE_ENVIRONMENT=Production` | `Hosting environment: Production`, y `/openapi/v1.json`, `/scalar/` y `/products` los tres a **`200`** ✓ |
| 12 | `GET /products` desde la UI | `200`, **50** productos del seed de 1.4 ✓ |
| 13 | `GET /categories` | `200`, 5 categorías ordenadas alfabéticamente ✓ |
| 14 | `POST /products` con `categoryId: 99` | `400` nombrando `CategoryId`, tal como lo describe el `[EndpointDescription]` ✓ |

La **10** es la que sostiene la decisión 2 y la **11** la que sostiene la decisión 3; sin ellas, las dos serían afirmaciones sobre el diff y no sobre el comportamiento. La **14** confirma que la prosa nueva describe lo que el servicio hace de verdad, que es el riesgo de toda documentación escrita a mano:

```json
{
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "CategoryId": ["No existe la categoría 99. Las categorías válidas están en GET /categories."]
  }
}
```

---

## Pendiente

- **1.6 (Dockerfile)** — la decisión 3 deja la UI servida también en `Production`, que es lo que la hace visible desde el contenedor; a cambio, el servicio en `docker-compose` necesitará `ConnectionStrings__CatalogDb` como variable de entorno, porque los User Secrets no existen ahí. Ver el detalle correspondiente.
- **1.7 (`Catalog.Tests`)** — no se prueba nada de este punto. Si alguna vez pareciera útil, lo único con sustancia que se puede afirmar es que `/openapi/v1.json` devuelve `200` y contiene las seis operaciones; la interfaz de Scalar no es código de este repositorio.
- **Los otros cuatro `.API`** — Scalar entra en cada uno cuando tenga endpoints: `Orders.API` en la Fase 2, `Inventory.API` y `Payments.API` en la 3.
- **Un único Scalar en el Gateway** — agregar los cinco documentos detrás de YARP es de la Fase 5, y es también donde hay que releer la decisión 3: el Gateway decide qué se expone hacia fuera.
- **`securitySchemes`** — 8.1. Cuando haya JWT, el documento tiene que declararlo para que el botón de autenticación de Scalar sirva de algo.
- **`decimal` anunciado como `double`** — sin consecuencia hasta que alguien genere un cliente a partir del documento. El sitio donde importará es la Fase 6.
- **La duplicación del texto de error** — el `400` de categoría inexistente está hoy explicado en el mensaje del `ModelState`, en el comentario XML y en el `[EndpointDescription]`. Nada mantiene los tres sincronizados; es el precio aceptado en la decisión 2.
