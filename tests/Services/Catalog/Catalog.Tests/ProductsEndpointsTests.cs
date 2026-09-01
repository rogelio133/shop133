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
    /// </summary>
    [Fact]
    public async Task GetAll_AfterMigrations_ReturnsSeededCatalog()
    {
        var response = await client.GetAsync("/products", CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var products = await response.Content.ReadFromJsonAsync<IReadOnlyList<ProductResponse>>(CancellationToken);

        Assert.NotNull(products);

        // "Contiene", no "son exactamente 50": otros tests de la clase pueden
        // haber creado productos antes que este.
        var seededIds = products.Select(product => product.Id).ToHashSet();
        Assert.All(Enumerable.Range(1, 50), id => Assert.Contains(id, seededIds));

        // El nombre de la categoría viaja resuelto (1.4), no solo su id: es lo
        // que evita que cada consumidor tenga que cruzar contra GET /categories.
        var firstMug = Assert.Single(products, product => product.Sku == "TAZA-001");
        Assert.Equal("Tazas", firstMug.CategoryName);
        Assert.Equal(TazasCategoryId, firstMug.CategoryId);
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

    private async Task<ProductResponse> CreateAsync(string sku)
    {
        var response = await client.PostAsJsonAsync("/products", NewProduct(sku), CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<ProductResponse>(CancellationToken);

        Assert.NotNull(created);

        return created;
    }
}
