using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Http;

using Shop133.Gateway.Tests.Infrastructure;

using Xunit;

namespace Shop133.Gateway.Tests;

/// <summary>
/// La primera mitad del título de 5.4: <i>"cada ruta de 5.1 alcanza su servicio"</i>.
///
/// <para>
/// Lo que estos tests afirman es <b>qué path le llega al backend</b>, no que el
/// backend sepa contestarlo. Es la distinción que hace falta tener presente al
/// leerlos: el stub apunta lo que recibió, así que un cambio en el
/// <c>[Route("[controller]")]</c> de Catalog dejaría esta suite en verde. Ese enlace
/// es de 8.6 (los smoke E2E sobre docker compose) y se anota en la sección Pendiente
/// de docs/fase_5_4.md.
/// </para>
///
/// <para>
/// El test que más valor tiene de los ocho es
/// <see cref="Post_Orders_RemovesOnlyApiAndKeepsTheOrdersSegment"/>: la asimetría
/// entre los dos <c>PathRemovePrefix</c> es la que costó un 404 en 5.1, no se deduce
/// leyendo la configuración y nada más que esto la vigila.
/// </para>
/// </summary>
[Trait("Category", "Fast")]
public sealed class GatewayRoutingTests : IAsyncDisposable
{
    private readonly BackendStub catalog = new();
    private readonly BackendStub orders = new();
    private readonly GatewayFactory factory;
    private readonly HttpClient client;

    // Constructor explícito y no inicializadores de campo, por el orden: la fábrica
    // necesita las URLs de los dos stubs, así que tienen que estar arrancados antes.
    // Es la misma restricción que 2.4 encontró con CatalogStub.
    public GatewayRoutingTests()
    {
        factory = new GatewayFactory(catalog.Url, orders.Url);
        client = factory.CreateClient();
    }

    [Fact]
    public async Task Get_CatalogPath_ReachesCatalogWithTheCatalogPrefixRemoved()
    {
        var response = await client.GetAsync("/api/catalog/products", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/products", catalog.SingleRequest().Path);
        Assert.Empty(orders.Requests);
    }

    [Fact]
    public async Task Get_CatalogProductById_KeepsTheRestOfThePath()
    {
        await client.GetAsync("/api/catalog/products/7", TestContext.Current.CancellationToken);

        Assert.Equal("/products/7", catalog.SingleRequest().Path);
    }

    [Fact]
    public async Task Get_CatalogCategories_ReachesCatalogToo()
    {
        // La segunda superficie de Catalog, y la que prueba que el catch-all de la
        // ruta no está atado a /products.
        await client.GetAsync("/api/catalog/categories", TestContext.Current.CancellationToken);

        Assert.Equal("/categories", catalog.SingleRequest().Path);
    }

    [Fact]
    public async Task Get_CatalogProducts_PreservesTheQueryString()
    {
        // PathRemovePrefix toca el path y nada más. Si algún día alguien lo cambia
        // por un transform de path completo, la query se perdería en silencio: el
        // servicio contestaría 200 con la página equivocada.
        await client.GetAsync("/api/catalog/products?page=2&size=10", TestContext.Current.CancellationToken);

        var received = catalog.SingleRequest();
        Assert.Equal("/products", received.Path);
        Assert.Equal("?page=2&size=10", received.QueryString);
    }

    [Fact]
    public async Task Post_Orders_RemovesOnlyApiAndKeepsTheOrdersSegment()
    {
        // EL test del punto. La ruta de orders quita "/api" y NO "/api/orders",
        // porque el último segmento del prefijo público es a la vez el recurso que
        // sirve el servicio. Con el prefijo entero quitado, el path que llegaría
        // aquí sería "/" o la cadena vacía — que es exactamente el 404 sin un solo
        // mensaje de log que costó tiempo en 5.1.
        //
        // Copiar aquí el transform de catalog "por simetría" es el error que este
        // test existe para hacer imposible.
        await client.PostAsJsonAsync(
            "/api/orders",
            new { customerEmail = "cliente@example.com" },
            TestContext.Current.CancellationToken);

        var received = orders.SingleRequest();
        Assert.Equal("/orders", received.Path);
        Assert.Empty(catalog.Requests);
    }

    [Fact]
    public async Task Get_OrderById_KeepsTheIdInThePath()
    {
        var orderId = Guid.NewGuid();

        await client.GetAsync($"/api/orders/{orderId}", TestContext.Current.CancellationToken);

        Assert.Equal($"/orders/{orderId}", orders.SingleRequest().Path);
    }

    [Fact]
    public async Task Post_Orders_ForwardsTheBodyAndReturnsTheBackendResponse()
    {
        orders.StatusCode = StatusCodes.Status201Created;
        orders.ResponseBody = """{"id":"11111111-1111-1111-1111-111111111111"}""";
        orders.ResponseHeaders["Location"] = "http://127.0.0.1/orders/11111111-1111-1111-1111-111111111111";

        var response = await client.PostAsJsonAsync(
            "/api/orders",
            new { customerEmail = "cliente@example.com", items = new[] { new { productId = 1, quantity = 2 } } },
            TestContext.Current.CancellationToken);

        // De ida: el cuerpo llega entero, no solo la línea de estado.
        var received = orders.SingleRequest();
        Assert.Equal("POST", received.Method);
        Assert.Contains("\"productId\":1", received.Body);

        // De vuelta: el Gateway devuelve lo que contestó el servicio, código y
        // cabeceras incluidos. El Location es el que 5.3 expone con
        // WithExposedHeaders para que el JavaScript de 6.5 pueda leerlo.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(
            "http://127.0.0.1/orders/11111111-1111-1111-1111-111111111111",
            response.Headers.Location?.ToString());
        Assert.Contains("11111111", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Get_UnroutedPrefix_Returns404AndReachesNoBackend()
    {
        // Inventory, Payments y Notifications no tienen ni carpeta Controllers/, así
        // que 5.1 decidió NO declararles ruta: una ruta que solo puede devolver 404
        // es el "filtro que nunca engancha" que 3.2 rechazó. Este test afirma esa
        // ausencia, que es una decisión y no un olvido.
        //
        // Los dos Assert.Empty son la mitad que importa: sin ellos, un catch-all
        // demasiado goloso que mandara esto a Catalog daría igualmente 404 —el del
        // stub— y el test pasaría.
        var response = await client.GetAsync("/api/inventory/anything", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(catalog.Requests);
        Assert.Empty(orders.Requests);
    }

    [Fact]
    public async Task Get_Root_Returns404()
    {
        // 5.1 borró el MapGet("/") de la plantilla: el Gateway solo es dueño de
        // /api/*. La sonda de vida es /health y llega en 8.4.
        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(catalog.Requests);
        Assert.Empty(orders.Requests);
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await factory.DisposeAsync();
        await catalog.DisposeAsync();
        await orders.DisposeAsync();
    }
}
