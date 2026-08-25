using System.Net;
using System.Net.Http.Json;
using System.Text;

using Microsoft.AspNetCore.Http;

using Orders.API.Models;
using Orders.Tests.Infrastructure;

using Xunit;

namespace Orders.Tests;

/// <summary>
/// El camino feliz de <c>POST /orders</c> y todo lo que decide Catalog cuando
/// Catalog sí contesta. Los caminos en los que Catalog no contesta viven en
/// <see cref="CatalogUnavailableTests"/>.
///
/// Lo que estos tests fijan, más allá de los códigos de estado, es **quién es
/// dueño de cada dato**: el cuerpo de la petición solo lleva <c>productId</c> y
/// <c>quantity</c>, y el sku, el nombre y el precio de cada línea salen del stub
/// de Catalog. Si algún día alguien "simplifica" el DTO añadiéndole el precio,
/// estos asserts dejan de tener sentido — que es justamente lo que se quiere.
///
/// Cada test estrena base de datos (ver <see cref="OrdersApiFactory"/>), así que
/// no hay disciplina de datos compartidos que mantener: se puede afirmar
/// "no hay ningún pedido" sin cualificarlo.
/// </summary>
[Collection(OrdersApiCollection.Name)]
[Trait("Category", "Docker")]
public sealed class CreateOrderTests : IAsyncLifetime
{
    private const string CustomerEmail = "cliente@shop133.test";

    // Dos productos del catálogo de 1.4. Los valores no tienen que coincidir con
    // los del seed real —aquí los sirve el stub— pero copiarlos hace los fallos
    // más legibles cuando se comparan con lo que devuelve Catalog de verdad.
    private const int MugId = 1;
    private const string MugSku = "TAZA-001";
    private const string MugName = "Taza Talavera Puebla";
    private const decimal MugPrice = 249.00m;

    private const int KeyringId = 2;
    private const string KeyringSku = "LLAV-001";
    private const string KeyringName = "Llavero Alebrije Oaxaca";
    private const decimal KeyringPrice = 89.50m;

    private const int UnknownProductId = 999_999;
    private const int AnotherUnknownProductId = 888_888;

    private readonly CatalogStub catalog;
    private readonly OrdersApiFactory factory;
    private HttpClient client = null!;

    /// <summary>
    /// El stub se construye antes que la fábrica porque la fábrica necesita su
    /// URL: Program.cs lee <c>Services:CatalogBaseUrl</c> durante la construcción
    /// del host, no después. Por eso hay constructor explícito y no un
    /// constructor primario como en Catalog.Tests — un inicializador de campo no
    /// puede leer otro campo de instancia.
    /// </summary>
    public CreateOrderTests(SqlServerContainerFixture container)
    {
        catalog = new CatalogStub();
        factory = new OrdersApiFactory(container, catalog.Url);
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await factory.InitializeAsync();

        client = factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        client?.Dispose();
        catalog.Dispose();

        await factory.DisposeAsync();
    }

    // ── Camino feliz ─────────────────────────────────────────────────────────

    /// <summary>
    /// El pedido se crea con el sku, el nombre y el precio **que dijo Catalog**,
    /// no con ninguno que haya mandado el cliente: el cuerpo no los lleva. Eso es
    /// lo que convierte a OrderLine en una foto y no en un puntero, y lo que hace
    /// que el pedido siga siendo legible cuando 1.3 borre el producto.
    /// </summary>
    [Fact]
    public async Task Create_ValidRequest_Returns201WithTheSnapshotCatalogDictated()
    {
        catalog.StubProduct(MugId, MugSku, MugName, MugPrice);
        catalog.StubProduct(KeyringId, KeyringSku, KeyringName, KeyringPrice);

        var response = await client.PostAsJsonAsync("/orders", NewOrder((MugId, 2), (KeyringId, 1)), CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<OrderResponse>(CancellationToken);

        Assert.NotNull(created);

        // El Id lo acuña la entidad con Guid.NewGuid(), no la base: existe antes
        // del INSERT porque desde la Fase 4 es la clave de correlación de la saga.
        Assert.NotEqual(Guid.Empty, created.Id);

        // En minúsculas por el LowercaseUrls de Program.cs.
        Assert.Equal($"/orders/{created.Id}", response.Headers.Location?.AbsolutePath);

        // En la Fase 2 no hay nada que mueva el estado: lo hará la máquina de
        // estados de 4.2.
        Assert.Equal("Pending", created.Status);
        Assert.Equal(CustomerEmail, created.CustomerEmail);

        var mug = Assert.Single(created.Items, item => item.ProductId == MugId);
        Assert.Equal(MugSku, mug.ProductSku);
        Assert.Equal(MugName, mug.ProductName);
        Assert.Equal(MugPrice, mug.UnitPrice);
        Assert.Equal(2, mug.Quantity);
        Assert.Equal(498.00m, mug.Subtotal);

        var keyring = Assert.Single(created.Items, item => item.ProductId == KeyringId);
        Assert.Equal(KeyringSku, keyring.ProductSku);
        Assert.Equal(KeyringPrice, keyring.UnitPrice);

        // Total y Subtotal se calculan, no se persisten (2.1): una sola fuente de
        // verdad. Si alguien les diera columna, este assert seguiría pasando —
        // el que lo detectaría es la migración, no el test.
        Assert.Equal(587.50m, created.Total);
    }

    /// <summary>
    /// El 201 podría estar maquillando la respuesta en memoria, así que el pedido
    /// se relee por HTTP. Comprueba de paso que las líneas vuelven **sin
    /// <c>Include</c>**: son un tipo owned desde 2.2, y esa es la razón por la que
    /// se eligió OwnsMany frente a una entidad con clave sombra.
    /// </summary>
    [Fact]
    public async Task Create_ValidRequest_IsRetrievableByGetById()
    {
        catalog.StubProduct(MugId, MugSku, MugName, MugPrice);

        var created = await CreateOrderAsync((MugId, 3));

        var response = await client.GetAsync($"/orders/{created.Id}", CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var reread = await response.Content.ReadFromJsonAsync<OrderResponse>(CancellationToken);

        Assert.NotNull(reread);
        Assert.Equal(created.Id, reread.Id);
        Assert.Equal(created.Total, reread.Total);

        var line = Assert.Single(reread.Items);
        Assert.Equal(MugSku, line.ProductSku);
        Assert.Equal(MugName, line.ProductName);
        Assert.Equal(3, line.Quantity);
        Assert.Equal(747.00m, line.Subtotal);
    }

    /// <summary>
    /// Dos entradas del mismo producto salen como **una** línea con las cantidades
    /// sumadas, y —lo que de verdad importa— cuestan **una sola** petición a
    /// Catalog, no dos.
    ///
    /// Las dos mitades del assert responden a dos motivos distintos: agrupar es un
    /// invariante de <c>Order</c> (un ReserveStock con dos entradas del mismo
    /// producto obligaría a Inventory a adivinar), y no repetir la petición es el
    /// coste del acoplamiento síncrono, que 2.3 decidió dejar a la vista.
    /// </summary>
    [Fact]
    public async Task Create_RepeatedProductId_GroupsLinesAndQueriesCatalogOnce()
    {
        catalog.StubProduct(MugId, MugSku, MugName, MugPrice);
        catalog.StubProduct(KeyringId, KeyringSku, KeyringName, KeyringPrice);

        var request = NewOrder((MugId, 2), (KeyringId, 1), (MugId, 3));

        var response = await client.PostAsJsonAsync("/orders", request, CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<OrderResponse>(CancellationToken);

        Assert.NotNull(created);
        Assert.Equal(2, created.Items.Count);

        var mug = Assert.Single(created.Items, item => item.ProductId == MugId);
        Assert.Equal(5, mug.Quantity);
        Assert.Equal(1245.00m, mug.Subtotal);

        Assert.Equal(1, catalog.RequestCountFor(MugId));
        Assert.Equal(2, catalog.TotalRequests);
    }

    // ── Producto inexistente ─────────────────────────────────────────────────

    /// <summary>
    /// 400 y no 404: lo que no existe es un valor del *cuerpo*, no el recurso de
    /// la URL — el mismo criterio que el categoryId desconocido de 1.3. Y el error
    /// nombra la línea concreta, no el pedido entero.
    /// </summary>
    [Fact]
    public async Task Create_UnknownProduct_Returns400NamingTheLine()
    {
        catalog.StubProductNotFound(UnknownProductId);

        var response = await client.PostAsJsonAsync("/orders", NewOrder((UnknownProductId, 1)), CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(CancellationToken);

        Assert.NotNull(problem);

        // La clave sale en PascalCase, como la que genera MVC para una colección:
        // el error añadido a mano y los de las DataAnnotations son indistinguibles.
        var error = Assert.Single(problem.Errors, entry => entry.Key == "Items[0].ProductId");
        Assert.Contains(UnknownProductId.ToString(), string.Join(' ', error.Value));
    }

    /// <summary>
    /// Los productos desconocidos se acumulan y salen **todos en un solo
    /// ValidationProblemDetails**, cada uno en el índice de su primera aparición.
    /// El bucle del controller hace <c>continue</c> en vez de cortar, y eso es lo
    /// que evita que el cliente arregle una línea para descubrir la siguiente.
    /// </summary>
    [Fact]
    public async Task Create_SeveralUnknownProducts_ReturnsThemAllInOneProblem()
    {
        catalog.StubProductNotFound(UnknownProductId);
        catalog.StubProductNotFound(AnotherUnknownProductId);
        catalog.StubProduct(MugId, MugSku, MugName, MugPrice);

        var request = NewOrder((UnknownProductId, 1), (MugId, 2), (AnotherUnknownProductId, 1));

        var response = await client.PostAsJsonAsync("/orders", request, CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(CancellationToken);

        Assert.NotNull(problem);
        Assert.Equal(2, problem.Errors.Count);
        Assert.Contains("Items[0].ProductId", problem.Errors.Keys);
        Assert.Contains("Items[2].ProductId", problem.Errors.Keys);
    }

    /// <summary>
    /// Un 400 no deja rastro. Se comprueba contando filas en la base y no
    /// preguntando por un id —no hay id que preguntar— porque es la única forma
    /// de distinguir "no se guardó" de "se guardó y no se devolvió".
    /// </summary>
    [Fact]
    public async Task Create_UnknownProduct_DoesNotPersistAnything()
    {
        catalog.StubProductNotFound(UnknownProductId);
        catalog.StubProduct(MugId, MugSku, MugName, MugPrice);

        var response = await client.PostAsJsonAsync(
            "/orders",
            NewOrder((MugId, 1), (UnknownProductId, 1)),
            CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await factory.CountOrdersAsync(CancellationToken));
    }

    // ── Validación del cuerpo ────────────────────────────────────────────────

    /// <summary>
    /// Cuerpo sin <c>customerEmail</c>. Va como JSON crudo y no como DTO porque
    /// <see cref="CreateOrderRequest"/> tiene los miembros <c>required</c>: en C#
    /// no se puede construir uno al que le falte un campo, que es justo lo que hay
    /// que enviar aquí.
    ///
    /// El segundo assert es el que aporta: la validación del modelo cortocircuita
    /// **antes** del controller, así que Catalog no llega a recibir ni una
    /// petición. Un cuerpo mal formado no debe costar viajes de red.
    /// </summary>
    [Fact]
    public async Task Create_MissingRequiredField_Returns400WithoutCallingCatalog()
    {
        const string body = """
            {
              "items": [ { "productId": 1, "quantity": 2 } ]
            }
            """;

        var response = await client.PostAsync(
            "/orders",
            new StringContent(body, Encoding.UTF8, "application/json"),
            CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, catalog.TotalRequests);
    }

    /// <summary>
    /// El formato del correo se valida en el DTO con <c>[EmailAddress]</c> y no en
    /// la entidad: <c>Order.CustomerEmail</c> comprueba longitud y que no venga en
    /// blanco, deliberadamente nada más. Este test fija dónde vive esa
    /// responsabilidad.
    /// </summary>
    [Fact]
    public async Task Create_InvalidEmail_Returns400()
    {
        catalog.StubProduct(MugId, MugSku, MugName, MugPrice);

        var request = NewOrder((MugId, 1)) with { CustomerEmail = "esto-no-es-un-correo" };

        var response = await client.PostAsJsonAsync("/orders", request, CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(CancellationToken);

        Assert.NotNull(problem);
        Assert.Contains(nameof(CreateOrderRequest.CustomerEmail), problem.Errors.Keys);
    }

    /// <summary>
    /// Un pedido sin líneas lo rechazan dos guardas independientes: el
    /// <c>[MinLength(1)]</c> del DTO y el constructor de <c>Order</c>. Gana la
    /// primera, y está bien que sea así — pero la segunda es la que no se puede
    /// saltar construyendo la entidad desde otro sitio.
    /// </summary>
    [Fact]
    public async Task Create_EmptyItems_Returns400()
    {
        var response = await client.PostAsJsonAsync("/orders", NewOrder(), CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, catalog.TotalRequests);
    }

    /// <summary>
    /// **El caso "los dos servicios dejaron de encajar".** Catalog devuelve un sku
    /// de 51 caracteres, uno más de lo que admite <c>OrderItem.ProductSkuMaxLength</c>
    /// —los dos servicios duplican esa constante a propósito, y CLAUDE.md deja
    /// escrito que pueden divergir—. La guarda de la entidad lanza
    /// <c>ArgumentOutOfRangeException</c> y el <c>catch (ArgumentException)</c> del
    /// controller lo convierte en 400.
    ///
    /// **Sin ese catch esto sería un 500**, y eso es lo que de verdad afirma el
    /// test. Que además no se escriba nada es la otra mitad.
    /// </summary>
    [Fact]
    public async Task Create_CatalogReturnsOversizedSku_Returns400AndNot500()
    {
        catalog.StubProduct(MugId, new string('X', 51), MugName, MugPrice);

        var response = await client.PostAsJsonAsync("/orders", NewOrder((MugId, 1)), CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(CancellationToken);

        Assert.NotNull(problem);

        // La clave es el ParamName de la excepción, que el controller usa tal cual.
        var error = Assert.Single(problem.Errors, entry => entry.Key == "productSku");
        Assert.Contains("50", string.Join(' ', error.Value));

        Assert.Equal(0, await factory.CountOrdersAsync(CancellationToken));
    }

    // ── GET /orders/{id} ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetById_UnknownId_Returns404()
    {
        var response = await client.GetAsync($"/orders/{Guid.NewGuid()}", CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Ayudas ───────────────────────────────────────────────────────────────

    /// <summary>
    /// El cuerpo lleva solo productId y quantity: esa pobreza es el diseño, no una
    /// simplificación del test. Catalog es el dueño de los precios, y "validar
    /// precios" significa pedírselos, no comparar con un número que mandó el
    /// cliente.
    /// </summary>
    private static CreateOrderRequest NewOrder(params (int ProductId, int Quantity)[] lines) => new()
    {
        CustomerEmail = CustomerEmail,
        Items = [.. lines.Select(line => new CreateOrderItemRequest
        {
            ProductId = line.ProductId,
            Quantity = line.Quantity,
        })],
    };

    private async Task<OrderResponse> CreateOrderAsync(params (int ProductId, int Quantity)[] lines)
    {
        var response = await client.PostAsJsonAsync("/orders", NewOrder(lines), CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<OrderResponse>(CancellationToken);

        Assert.NotNull(created);

        return created;
    }
}
