using System.Net;
using System.Net.Http.Json;

using Catalog.API.Models;
using Catalog.Tests.Infrastructure;

using Xunit;

namespace Catalog.Tests;

/// <summary>
/// <c>GET /categories</c> (1.4). Un solo endpoint, de solo lectura: el catálogo
/// de categorías es una tabla de consulta que se siembra con las migraciones y
/// no se administra por HTTP.
/// </summary>
[Collection(CatalogApiCollection.Name)]
[Trait("Category", "Docker")]
public sealed class CategoriesEndpointsTests(SqlServerContainerFixture container) : IAsyncLifetime
{
    private readonly CatalogApiFactory factory = new(container);
    private HttpClient client = null!;

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

    /// <summary>
    /// El orden esperado es alfabético y no por Id, porque el controller ordena
    /// por nombre. No es un detalle cosmético: los ids del seed van
    /// Tazas=1, Llaveros=2, Playeras=3, Pines=4, Libretas=5, así que si alguien
    /// cambiara el OrderBy este assert lo delataría en vez de pasar por
    /// casualidad.
    /// </summary>
    [Fact]
    public async Task GetAll_AfterMigrations_ReturnsSeededCategoriesOrderedByName()
    {
        // El CancellationToken sale de TestContext.Current y no es opcional: el
        // analizador xUnit1051 lo exige (warning, y aquí el build va a 0), y es
        // lo que permite que un test colgado contra el contenedor se cancele en
        // vez de agotar el timeout de la suite entera.
        var cancellationToken = TestContext.Current.CancellationToken;

        var response = await client.GetAsync("/categories", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var categories = await response.Content.ReadFromJsonAsync<IReadOnlyList<CategoryResponse>>(cancellationToken);

        Assert.NotNull(categories);
        Assert.Equal(
            ["Libretas", "Llaveros", "Pines", "Playeras", "Tazas"],
            categories.Select(category => category.Name));

        // Todas traen un Id utilizable: es lo que POST /products pide en su
        // CategoryId, y el mensaje del 400 de categoría desconocida remite justo
        // a este endpoint.
        Assert.All(categories, category => Assert.True(category.Id > 0));
    }
}
