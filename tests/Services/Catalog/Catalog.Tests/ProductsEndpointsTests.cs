using System.Net;
using System.Net.Http.Json;
using System.Text;

using Catalog.API.Models;
using Catalog.Tests.Infrastructure;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

using Shop133.TestUtilities;

using Xunit;

namespace Catalog.Tests;

/// <summary>
/// Los cinco endpoints de 1.3 contra un SQL Server real, con sus tres caminos
/// de error. El que justifica todo el montaje es
/// <see cref="Create_DuplicateSku_Returns409"/>: ese 409 nace de un
/// <c>SqlException</c> 2601/2627 que solo existe si hay un índice único de
/// verdad, así que es exactamente el bug que el provider InMemory dejaría pasar.
///
/// **Todos los tests de esta clase comparten la misma base de datos** (una por
/// clase, ver <see cref="CatalogApiFactory"/>), y eso impone dos reglas:
///   1. Ningún test modifica ni borra una fila del seed. El que necesita
///      escribir crea su propio producto con un Sku propio (TEST-0xx).
///   2. Las lecturas del catálogo completo afirman que *contienen* lo que
///      esperan, nunca que hay exactamente N filas.
/// xUnit no paraleliza dentro de una clase, así que no hay escrituras
/// concurrentes; el riesgo es el orden, y esas dos reglas lo neutralizan.
/// </summary>
[Collection(CatalogApiCollection.Name)]
[Trait("Category", "Docker")]
public sealed class ProductsEndpointsTests(SqlServerContainerFixture container) : IAsyncLifetime
{
    private const int TazasCategoryId = 1;
    private const int LlaverosCategoryId = 2;

    /// <summary>
    /// La categoría que usan los tests de filtro de 6.2.1, y **la elección no es
    /// arbitraria**: ningún test de esta clase escribe en Libretas. <c>NewProduct</c>
    /// crea en Tazas y <c>Update_ExistingProduct…</c> mueve un producto a Llaveros,
    /// así que filtrar por 1 o por 2 daría un <c>totalItems</c> que depende del orden
    /// de ejecución. Sobre Libretas se puede afirmar **10 exacto**, que es lo que hace
    /// verificable el recuento — sin romper la regla 2 de la clase, porque aquí no se
    /// cuenta el catálogo entero sino un subconjunto que nadie toca.
    ///
    /// Sus 10 productos son los ids 41..50 del seed (LIBR-001 .. LIBR-010).
    /// </summary>
    private const int LibretasCategoryId = 5;

    private const int LibretasProductCount = 10;

    /// <summary>El <c>GetProductsRequest.DefaultPageSize</c>, repetido aquí a propósito: un test que importara la constante pasaría igual si alguien la cambiara a 5.</summary>
    private const int DefaultPageSize = 20;

    /// <summary>Id del seed que no existe en ninguna de las 50 filas sembradas.</summary>
    private const int UnknownProductId = 999_999;

    private const int UnknownCategoryId = 999;

    private readonly CatalogApiFactory factory = new(container);
    private HttpClient client = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await factory.InitializeAsync();

        client = factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        client?.Dispose();

        await factory.DisposeAsync();
    }

    // ── GET /products ────────────────────────────────────────────────────────

    /// <summary>
    /// La fixture no siembra nada: las 50 filas las pone la migración
    /// SeedSouvenirCatalog de 1.4, así que este test comprueba de paso que
    /// MigrateAsync deja la base en el estado que el resto de la clase supone.
    ///
    /// Pide <c>pageSize=50</c> desde 6.2.1, que es cuando el endpoint se paginó:
    /// las 50 filas del seed ocupan los ids 1..50 y los productos que crean los
    /// demás tests salen del IDENTITY con ids de cuatro cifras, así que
    /// ordenando por Id la primera página de 50 es exactamente el seed.
    /// </summary>
    [Fact]
    public async Task GetAll_AfterMigrations_ReturnsSeededCatalog()
    {
        var response = await client.GetAsync("/products?pageSize=50", CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await response.Content.ReadFromJsonAsync<PagedResponse<ProductResponse>>(CancellationToken);

        Assert.NotNull(page);

        // "Contiene", no "son exactamente 50": otros tests de la clase pueden
        // haber creado productos antes que este.
        var seededIds = page.Items.Select(product => product.Id).ToHashSet();
        Assert.All(Enumerable.Range(1, 50), id => Assert.Contains(id, seededIds));

        // El nombre de la categoría viaja resuelto (1.4), no solo su id: es lo
        // que evita que cada consumidor tenga que cruzar contra GET /categories.
        var firstMug = Assert.Single(page.Items, product => product.Sku == "TAZA-001");
        Assert.Equal("Tazas", firstMug.CategoryName);
        Assert.Equal(TazasCategoryId, firstMug.CategoryId);
    }

    // ── GET /products — paginación y filtro (6.2.1) ──────────────────────────

    /// <summary>
    /// Sin un solo parámetro, el endpoint pagina igual: 20 elementos, que es el
    /// <c>DefaultPageSize</c>. Antes de 6.2.1 esta misma petición devolvía el
    /// catálogo entero en un array pelado.
    /// </summary>
    [Fact]
    public async Task GetAll_WithoutQueryParameters_ReturnsFirstPageOfTwenty()
    {
        var page = await GetPageAsync("/products");

        Assert.Equal(DefaultPageSize, page.Items.Count);
        Assert.Equal(1, page.Page);
        Assert.Equal(DefaultPageSize, page.PageSize);

        // El total cuenta el catálogo entero, no la página — que es la mitad de
        // lo que el sobre existe para poder decir. Es ">= 50" y no "== 50" por la
        // regla 2 de la clase: otros tests crean productos.
        Assert.True(page.TotalItems >= 50, $"totalItems fue {page.TotalItems}");
        Assert.True(page.TotalPages >= 3, $"totalPages fue {page.TotalPages}");

        // Ordenado por Id, así que la primera página es el principio del seed.
        Assert.Equal(Enumerable.Range(1, DefaultPageSize), page.Items.Select(product => product.Id));
    }

    /// <summary>
    /// La segunda página trae productos **distintos**. Es lo que de verdad falla
    /// si alguien quita el <c>OrderBy</c> o se equivoca en la aritmética del
    /// <c>Skip</c>: con <c>(page - 1) * pageSize</c> mal escrito, la página 2
    /// repite la 1 y un test que solo contara elementos pasaría.
    /// </summary>
    [Fact]
    public async Task GetAll_SecondPage_ReturnsDifferentProducts()
    {
        var first = await GetPageAsync("/products");
        var second = await GetPageAsync("/products?page=2");

        Assert.Equal(2, second.Page);
        Assert.Equal(DefaultPageSize, second.Items.Count);
        Assert.Equal(Enumerable.Range(21, DefaultPageSize), second.Items.Select(product => product.Id));

        var firstIds = first.Items.Select(product => product.Id).ToHashSet();
        Assert.DoesNotContain(second.Items, product => firstIds.Contains(product.Id));

        // El total no cambia entre páginas: cuenta el conjunto, no lo devuelto.
        Assert.Equal(first.TotalItems, second.TotalItems);
    }

    /// <summary>
    /// Una página más allá del final es un <c>200</c> con <c>items</c> vacío, no
    /// un <c>404</c>: la página existe como consulta y su resultado está vacío,
    /// igual que una lista vacía nunca fue un 404 en este servicio.
    /// </summary>
    [Fact]
    public async Task GetAll_PageBeyondTheLast_Returns200WithEmptyItems()
    {
        var page = await GetPageAsync("/products?page=10000&pageSize=100");

        Assert.Empty(page.Items);
        Assert.Equal(10000, page.Page);

        // Y sigue diciendo cuántos hay, que es lo que permite al cliente volver
        // a una página que sí existe en vez de creer que el catálogo está vacío.
        Assert.True(page.TotalItems >= 50, $"totalItems fue {page.TotalItems}");
    }

    /// <summary>
    /// El filtro que 1.4 dejó aplazado. Sobre Libretas, que ningún otro test
    /// escribe, el recuento se puede afirmar exacto.
    /// </summary>
    [Fact]
    public async Task GetAll_FilteredByCategory_ReturnsOnlyThatCategory()
    {
        var page = await GetPageAsync($"/products?categoryId={LibretasCategoryId}");

        Assert.Equal(LibretasProductCount, page.Items.Count);
        Assert.Equal(LibretasProductCount, page.TotalItems);
        Assert.Equal(1, page.TotalPages);

        Assert.All(page.Items, product =>
        {
            Assert.Equal(LibretasCategoryId, product.CategoryId);
            Assert.Equal("Libretas", product.CategoryName);
        });
    }

    /// <summary>
    /// **200 con la página vacía, no 400.** El <c>POST</c> sí devuelve 400 ante
    /// un <c>categoryId</c> inexistente porque allí ese id **escribe** una
    /// relación que tiene que existir; aquí solo **selecciona**, y "no hay nada"
    /// es una respuesta cierta. Es además lo que 6.2 ya había decidido y
    /// verificado desde el lado del frontend.
    /// </summary>
    [Fact]
    public async Task GetAll_FilteredByUnknownCategory_Returns200WithEmptyPage()
    {
        var response = await client.GetAsync($"/products?categoryId={UnknownCategoryId}", CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await response.Content.ReadFromJsonAsync<PagedResponse<ProductResponse>>(CancellationToken);

        Assert.NotNull(page);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalItems);

        // Cero elementos son CERO páginas, no una vacía: es la aritmética del
        // Math.Ceiling de PagedResponse.From, y es lo que deja al paginador del
        // frontend sin nada que pintar en vez de con un "página 1 de 1" mentiroso.
        Assert.Equal(0, page.TotalPages);
    }

    /// <summary>
    /// Filtro y paginación **compuestos en una sola acción**, que es el motivo
    /// por el que se descartó un sub-recurso <c>GET /categories/{id}/products</c>.
    /// La página 3 de 4 en 4 sobre 10 elementos es la última y es **parcial**:
    /// con un <c>Math.Ceiling</c> mal hecho (división entera) <c>totalPages</c>
    /// saldría 2 y esta página no existiría.
    /// </summary>
    [Fact]
    public async Task GetAll_FilterAndPagingCombined_ReturnsTheLastPartialPage()
    {
        var page = await GetPageAsync($"/products?categoryId={LibretasCategoryId}&pageSize=4&page=3");

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(LibretasProductCount, page.TotalItems);
        Assert.Equal(3, page.TotalPages);
        Assert.All(page.Items, product => Assert.Equal(LibretasCategoryId, product.CategoryId));
    }

    /// <summary>
    /// <c>400</c> y no un recorte silencioso: pedir la página 0 es un error del
    /// cliente y se le dice. Sin el DTO con DataAnnotations esto llegaría al
    /// <c>Skip</c> con un desplazamiento **negativo**.
    /// </summary>
    [Fact]
    public async Task GetAll_PageZero_Returns400NamingPage()
    {
        var response = await client.GetAsync("/products?page=0", CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(CancellationToken);

        Assert.NotNull(problem);

        var error = Assert.Single(problem.Errors);
        Assert.Contains(nameof(GetProductsRequest.Page), error.Key, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// El tope de <c>pageSize</c> existe porque sin él <c>?pageSize=1000000</c>
    /// es una forma perfectamente válida de pedir el catálogo entero, y entonces
    /// la paginación sería una sugerencia y no un límite.
    /// </summary>
    [Fact]
    public async Task GetAll_PageSizeAboveMaximum_Returns400NamingPageSize()
    {
        var response = await client.GetAsync(
            $"/products?pageSize={GetProductsRequest.MaxPageSize + 1}",
            CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(CancellationToken);

        Assert.NotNull(problem);

        var error = Assert.Single(problem.Errors);
        Assert.Contains(nameof(GetProductsRequest.PageSize), error.Key, StringComparison.OrdinalIgnoreCase);
    }

    // ── GET /products/{id} ───────────────────────────────────────────────────

    [Fact]
    public async Task GetById_SeededId_Returns200WithProduct()
    {
        var response = await client.GetAsync("/products/1", CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var product = await response.Content.ReadFromJsonAsync<ProductResponse>(CancellationToken);

        Assert.NotNull(product);
        Assert.Equal("TAZA-001", product.Sku);
        Assert.Equal("Taza Talavera Puebla", product.Name);
        Assert.Equal(249.00m, product.Price);
        Assert.Equal("Tazas", product.CategoryName);
    }

    [Fact]
    public async Task GetById_UnknownId_Returns404()
    {
        var response = await client.GetAsync($"/products/{UnknownProductId}", CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── POST /products ───────────────────────────────────────────────────────

    [Fact]
    public async Task Create_ValidRequest_Returns201WithLocationAndCategoryName()
    {
        var response = await client.PostAsJsonAsync("/products", NewProduct("TEST-040"), CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<ProductResponse>(CancellationToken);

        Assert.NotNull(created);

        // El Id lo asigna el IDENTITY y no empieza en 1 — el seed ocupa 1..50 con
        // IDENTITY_INSERT, que no mueve el contador. Lo único que se puede
        // afirmar es que llega asignado.
        Assert.True(created.Id > 0);

        // En minúsculas por el LowercaseUrls de Program.cs: sin él, CreatedAtAction
        // generaría "/Products/{id}".
        Assert.Equal($"/products/{created.Id}", response.Headers.Location?.AbsolutePath);

        // El 201 trae el nombre de la categoría sin una segunda consulta, porque
        // el controller busca la entidad (no un bool) y EF rellena la navegación
        // por fix-up.
        Assert.Equal("Tazas", created.CategoryName);
    }

    /// <summary>
    /// **El test que justifica Testcontainers.** El 409 no lo decide el
    /// controller mirando la tabla: lo decide SQL Server al rechazar el INSERT
    /// contra el índice único de 1.2, y el controller traduce el
    /// <c>SqlException</c> 2601/2627. Sin base de datos real este camino no se
    /// ejecuta nunca.
    /// </summary>
    [Fact]
    public async Task Create_DuplicateSku_Returns409()
    {
        var response = await client.PostAsJsonAsync("/products", NewProduct("TAZA-001"), CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken);

        Assert.NotNull(problem);
        Assert.Equal("Sku duplicado", problem.Title);
    }

    /// <summary>
    /// La entidad normaliza el Sku a mayúsculas, así que "lap-14" y "LAP-14" no
    /// pueden convertirse en dos productos. Se comprueba en la respuesta y
    /// releyendo, para que no baste con que el 201 lo maquille.
    /// </summary>
    [Fact]
    public async Task Create_LowercaseSku_IsPersistedUppercased()
    {
        var response = await client.PostAsJsonAsync("/products", NewProduct("test-060"), CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<ProductResponse>(CancellationToken);

        Assert.NotNull(created);
        Assert.Equal("TEST-060", created.Sku);

        var reread = await client.GetFromJsonAsync<ProductResponse>($"/products/{created.Id}", CancellationToken);

        Assert.NotNull(reread);
        Assert.Equal("TEST-060", reread.Sku);
    }

    /// <summary>
    /// Cuerpo sin <c>name</c>. Va como JSON crudo y no como DTO porque
    /// <see cref="CreateProductRequest"/> tiene los miembros <c>required</c>: en
    /// C# no se puede construir uno al que le falte un campo, que es justo lo
    /// que hay que enviar aquí.
    /// </summary>
    [Fact]
    public async Task Create_MissingRequiredField_Returns400()
    {
        const string body = """
            {
              "sku": "TEST-070",
              "description": "Sin name, que es required.",
              "price": 199.00,
              "stock": 7,
              "categoryId": 1
            }
            """;

        var response = await client.PostAsync(
            "/products",
            new StringContent(body, Encoding.UTF8, "application/json"),
            CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// 400 y no 404: lo que no existe es un valor del *cuerpo*, no el recurso de
    /// la URL. Y no es un 547 de clave foránea traducido — el controller
    /// consulta la tabla antes de guardar, para poder decir qué campo falla.
    /// </summary>
    [Fact]
    public async Task Create_UnknownCategoryId_Returns400NamingCategoryId()
    {
        var request = NewProduct("TEST-080") with { CategoryId = UnknownCategoryId };

        var response = await client.PostAsJsonAsync("/products", request, CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(CancellationToken);

        Assert.NotNull(problem);

        var error = Assert.Single(problem.Errors, entry => entry.Key == nameof(CreateProductRequest.CategoryId));
        Assert.Contains("/categories", string.Join(' ', error.Value));
    }

    /// <summary>
    /// El hueco medido en 1.3: <c>ImageUrl</c> es opcional, así que su DTO solo
    /// lleva <c>[MaxLength]</c> y un <c>"   "</c> pasa la validación del modelo.
    /// Lo para la guarda de la entidad, y el <c>catch (ArgumentException)</c> del
    /// controller lo convierte en 400. **Sin ese catch esto sería un 500**, que
    /// es lo que de verdad afirma este test.
    /// </summary>
    [Fact]
    public async Task Create_BlankImageUrl_Returns400AndNot500()
    {
        var request = NewProduct("TEST-090") with { ImageUrl = "   " };

        var response = await client.PostAsJsonAsync("/products", request, CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(CancellationToken);

        Assert.NotNull(problem);
        Assert.NotEmpty(problem.Errors);
    }

    [Fact]
    public async Task Create_NegativePrice_Returns400()
    {
        var request = NewProduct("TEST-100") with { Price = -1m };

        var response = await client.PostAsJsonAsync("/products", request, CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── PUT /products/{id} ───────────────────────────────────────────────────

    [Fact]
    public async Task Update_ExistingProduct_Returns204AndPersistsChanges()
    {
        var created = await CreateAsync("TEST-110");

        var request = ReplacementFor(created) with
        {
            Name = "Nombre reemplazado",
            Price = 399.00m,
            Stock = 3,
            CategoryId = LlaverosCategoryId,
        };

        var response = await client.PutAsJsonAsync($"/products/{created.Id}", request, CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, response.Content.Headers.ContentLength ?? 0);

        var updated = await client.GetFromJsonAsync<ProductResponse>($"/products/{created.Id}", CancellationToken);

        Assert.NotNull(updated);
        Assert.Equal("Nombre reemplazado", updated.Name);
        Assert.Equal(399.00m, updated.Price);
        Assert.Equal(3, updated.Stock);
        Assert.Equal("Llaveros", updated.CategoryName);
    }

    [Fact]
    public async Task Update_UnknownId_Returns404()
    {
        var request = NewReplacement("TEST-120");

        var response = await client.PutAsJsonAsync($"/products/{UnknownProductId}", request, CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Las dos cosas fallan a la vez y gana el 404: el recurso que se pretendía
    /// reemplazar no está, así que lo que trajera el cuerpo da igual. El orden
    /// está fijado en el controller, no es casualidad del compilador.
    /// </summary>
    [Fact]
    public async Task Update_UnknownIdAndUnknownCategory_Returns404NotBadRequest()
    {
        var request = NewReplacement("TEST-130") with { CategoryId = UnknownCategoryId };

        var response = await client.PutAsJsonAsync($"/products/{UnknownProductId}", request, CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_UnknownCategoryId_Returns400()
    {
        var created = await CreateAsync("TEST-140");

        var request = ReplacementFor(created) with { CategoryId = UnknownCategoryId };

        var response = await client.PutAsJsonAsync($"/products/{created.Id}", request, CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// El PUT choca con el mismo índice único que el POST, porque el Sku es
    /// modificable (decisión 9 de docs/fase_1_1.md). Es la mitad del motivo por
    /// el que <c>DbUpdateExceptionExtensions</c> se usa en dos acciones.
    /// </summary>
    [Fact]
    public async Task Update_SkuTakenByAnotherProduct_Returns409()
    {
        var created = await CreateAsync("TEST-150");

        var request = ReplacementFor(created) with { Sku = "TAZA-001" };

        var response = await client.PutAsJsonAsync($"/products/{created.Id}", request, CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    /// <summary>
    /// La otra cara de la decisión 9: el código de negocio se corrige y se
    /// renumera, y solo el Id es inmutable. Cambiar el Sku por uno libre es un
    /// 204, no un 409.
    /// </summary>
    [Fact]
    public async Task Update_ChangingOwnSku_Returns204()
    {
        var created = await CreateAsync("TEST-160");

        var request = ReplacementFor(created) with { Sku = "TEST-161" };

        var response = await client.PutAsJsonAsync($"/products/{created.Id}", request, CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var updated = await client.GetFromJsonAsync<ProductResponse>($"/products/{created.Id}", CancellationToken);

        Assert.NotNull(updated);
        Assert.Equal("TEST-161", updated.Sku);
        Assert.Equal(created.Id, updated.Id);
    }

    // ── DELETE /products/{id} ────────────────────────────────────────────────

    /// <summary>
    /// Borrado físico: la fila desaparece y el GET siguiente es un 404, no un
    /// 200 con una marca de borrado. Borra un producto creado por el propio test
    /// y nunca uno del seed — el resto de la clase cuenta con esas 50 filas.
    /// </summary>
    [Fact]
    public async Task Delete_ExistingProduct_Returns204AndTheProductIsGone()
    {
        var created = await CreateAsync("TEST-170");

        var response = await client.DeleteAsync($"/products/{created.Id}", CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var afterDelete = await client.GetAsync($"/products/{created.Id}", CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);
    }

    [Fact]
    public async Task Delete_UnknownId_Returns404()
    {
        var response = await client.DeleteAsync($"/products/{UnknownProductId}", CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Ayudas ───────────────────────────────────────────────────────────────

    private static CreateProductRequest NewProduct(string sku) => new()
    {
        Sku = sku,
        Name = "Producto de prueba",
        Description = "Creado por Catalog.Tests. No pertenece al seed de 1.4.",
        Price = 199.00m,
        Stock = 7,
        CategoryId = TazasCategoryId,
        ImageUrl = "/img/products/test.jpg",
    };

    private static UpdateProductRequest NewReplacement(string sku)
    {
        var product = NewProduct(sku);

        return new UpdateProductRequest
        {
            Sku = product.Sku,
            Name = product.Name,
            Description = product.Description,
            Price = product.Price,
            Stock = product.Stock,
            CategoryId = product.CategoryId,
            ImageUrl = product.ImageUrl,
        };
    }

    /// <summary>
    /// El PUT es un reemplazo completo, así que el cuerpo parte de lo que el
    /// producto ya tiene y el test solo cambia con <c>with</c> lo que quiere
    /// probar. Escribir los siete campos en cada test escondería cuál es el que
    /// importa.
    /// </summary>
    private static UpdateProductRequest ReplacementFor(ProductResponse product) => new()
    {
        Sku = product.Sku,
        Name = product.Name,
        Description = product.Description,
        Price = product.Price,
        Stock = product.Stock,
        CategoryId = product.CategoryId,
        ImageUrl = product.ImageUrl,
    };

    /// <summary>
    /// Lee una página y afirma de paso que la respuesta fue <c>200</c>. Existe
    /// porque los ocho tests de 6.2.1 repetirían las mismas cuatro líneas, y lo
    /// que cada uno quiere enseñar es la aserción de después, no el plumbing.
    /// Los tests que esperan un <c>400</c> **no** lo usan: ahí el código de
    /// estado es el sujeto del test, no un prerrequisito.
    /// </summary>
    private async Task<PagedResponse<ProductResponse>> GetPageAsync(string url)
    {
        var response = await client.GetAsync(url, CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var page = await response.Content.ReadFromJsonAsync<PagedResponse<ProductResponse>>(CancellationToken);

        Assert.NotNull(page);

        return page;
    }

    private async Task<ProductResponse> CreateAsync(string sku)
    {
        var response = await client.PostAsJsonAsync("/products", NewProduct(sku), CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<ProductResponse>(CancellationToken);

        Assert.NotNull(created);

        return created;
    }
}
