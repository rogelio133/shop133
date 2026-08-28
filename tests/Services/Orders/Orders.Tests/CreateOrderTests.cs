using System.Net;
using System.Net.Http.Json;
using System.Text;

using Microsoft.AspNetCore.Http;

using Orders.API.Models;
using Orders.Tests.Infrastructure;

using Xunit;

namespace Orders.Tests;

/// <summary>
/// <c>POST /orders</c> y <c>GET /orders/{id}</c> después de 3.3.
///
/// **Estos tests cambiaron de tesis, no solo de forma.** En 2.4 afirmaban quién
/// era dueño de cada dato: el cuerpo llevaba solo <c>productId</c> y
/// <c>quantity</c>, y el sku, el nombre y el precio salían de un stub de Catalog.
/// Ese acoplamiento ya no existe, así que la foto la manda el cliente y lo que
/// estos tests fijan ahora es lo contrario: que Orders **congela lo que recibe sin
/// contrastarlo con nadie**. Junto a ellos desaparecieron los seis de
/// <c>CatalogUnavailableTests</c> y el <c>CatalogStub</c>.
///
/// El caso que mejor resume el punto es
/// <see cref="Create_ProductThatCatalogDoesNotKnow_Returns201Anyway"/>: es el mismo
/// escenario que en 2.4 devolvía 400 y ahora devuelve 201. La comprobación no se
/// perdió, se mudó — la hará Inventory en 3.4 con un <c>StockRejected</c>.
///
/// Cada test estrena base de datos (ver <see cref="OrdersApiFactory"/>), así que
/// no hay disciplina de datos compartidos que mantener: se puede afirmar
/// "no hay ningún pedido" sin cualificarlo. Lo que sí comparten todos es el
/// **RabbitMQ del compose**, que desde 3.3 tiene que estar levantado.
/// </summary>
[Collection(OrdersApiCollection.Name)]
[Trait("Category", "Docker")]
public sealed class CreateOrderTests(SqlServerContainerFixture container) : IAsyncLifetime
{
    private const string CustomerEmail = "cliente@shop133.test";

    // Dos productos del catálogo de 1.4. Ya no los sirve ningún stub: son
    // literalmente lo que el cuerpo manda. Se copian del seed real porque hace los
    // fallos más legibles, no porque nada los compruebe contra él — que no se
    // comprueben es justo lo que 3.3 acepta.
    private const int MugId = 1;
    private const string MugSku = "TAZA-001";
    private const string MugName = "Taza Talavera Puebla";
    private const decimal MugPrice = 249.00m;

    private const int KeyringId = 2;
    private const string KeyringSku = "LLAV-001";
    private const string KeyringName = "Llavero Alebrije Oaxaca";
    private const decimal KeyringPrice = 89.50m;

    private const int UnknownProductId = 999_999;

    // Constructor primario otra vez, como en Catalog.Tests: en 2.4 hacía falta uno
    // explícito porque la fábrica necesitaba la URL del stub y un inicializador de
    // campo no puede leer otro campo de instancia. Sin stub, la restricción se fue.
    private readonly OrdersApiFactory factory = new(container);
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

    // ── Camino feliz ─────────────────────────────────────────────────────────

    /// <summary>
    /// El pedido se crea con el sku, el nombre y el precio **que mandó el cliente**.
    /// En 2.4 este mismo test afirmaba lo contrario —que los dictaba Catalog— y ese
    /// giro es el contenido de 3.3.
    ///
    /// Que el 201 llegue demuestra además que el <c>Publish</c> de
    /// <c>OrderCreated</c> no lanzó ni se quedó colgado: ocurre antes de construir
    /// la respuesta. Que el mensaje llegue al exchange correcto no lo puede afirmar
    /// esta suite sin el harness de 3.7; se comprueba en el broker, a mano.
    /// </summary>
    [Fact]
    public async Task Create_ValidRequest_Returns201WithTheSnapshotTheClientSent()
    {
        var response = await client.PostAsJsonAsync(
            "/orders",
            NewOrder(Mug(2), Keyring(1)),
            CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<OrderResponse>(CancellationToken);

        Assert.NotNull(created);

        // El Id lo acuña la entidad con Guid.NewGuid(), no la base: existe antes
        // del INSERT porque desde la Fase 4 es la clave de correlación de la saga.
        // Desde 3.3 esa propiedad se cobra — es el OrderId que viaja en el evento.
        Assert.NotEqual(Guid.Empty, created.Id);

        // En minúsculas por el LowercaseUrls de Program.cs.
        Assert.Equal($"/orders/{created.Id}", response.Headers.Location?.AbsolutePath);

        // Sigue siendo Pending: 3.3 publica el evento, pero nadie lo consume aún
        // ni mueve el estado. Lo hará la máquina de estados de 4.2.
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
        // verdad. Este mismo número es el que viaja como OrderCreated.Total y el
        // que Payments acabará cobrando en 3.5 vía StockReserved.Amount.
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
        var created = await CreateOrderAsync(Mug(3));

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
    /// **El test que resume 3.3.** Un producto que no existe en el catálogo se
    /// acepta: en 2.4 esta misma petición devolvía 400 nombrando la línea, porque
    /// Orders preguntaba a Catalog y Catalog decía que no.
    ///
    /// Ya no pregunta a nadie, así que no lo puede saber. Quien lo descubrirá es
    /// Inventory en 3.4, que no encontrará stock reservable para ese ProductId y
    /// publicará <c>StockRejected</c>: el pedido no se rechaza, se **cancela**. Eso
    /// es lo que la coreografía mueve de sitio — una validación síncrona se
    /// convierte en un estado del pedido, y el cliente se entera después.
    ///
    /// Cuando 3.4 exista, este test tiene un hermano al otro lado del broker.
    /// </summary>
    [Fact]
    public async Task Create_ProductThatCatalogDoesNotKnow_Returns201Anyway()
    {
        var line = new CreateOrderItemRequest
        {
            ProductId = UnknownProductId,
            ProductSku = "NOPE-001",
            ProductName = "Producto que no existe",
            Quantity = 1,
            UnitPrice = 1.00m,
        };

        var response = await client.PostAsJsonAsync("/orders", NewOrder(line), CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, await factory.CountOrdersAsync(CancellationToken));
    }

    /// <summary>
    /// Dos entradas del mismo producto salen como **una** línea con las cantidades
    /// sumadas.
    ///
    /// En 2.4 la mitad interesante del assert era que costaba una sola petición a
    /// Catalog —el coste del acoplamiento, que 2.3 dejó a la vista—. Esa mitad se
    /// fue con el acoplamiento; queda la otra, que nunca dependió de él: agrupar es
    /// un invariante de <c>Order</c>, porque un <c>ReserveStock</c> con dos entradas
    /// del mismo producto obligaría a Inventory a adivinar si reserva la suma.
    /// </summary>
    [Fact]
    public async Task Create_RepeatedProductId_GroupsLinesSummingQuantities()
    {
        var request = NewOrder(Mug(2), Keyring(1), Mug(3));

        var response = await client.PostAsJsonAsync("/orders", request, CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<OrderResponse>(CancellationToken);

        Assert.NotNull(created);
        Assert.Equal(2, created.Items.Count);

        var mug = Assert.Single(created.Items, item => item.ProductId == MugId);
        Assert.Equal(5, mug.Quantity);
        Assert.Equal(1245.00m, mug.Subtotal);
    }

    // ── Validación del cuerpo ────────────────────────────────────────────────

    /// <summary>
    /// **La rama de error que 3.3 estrena**, y que ocupa el hueco del "producto
    /// desconocido" que este punto se llevó.
    ///
    /// Al venir la foto en el cuerpo, dos líneas del mismo producto pueden
    /// contradecirse. Antes no podían: la foto la ponía Catalog una sola vez por
    /// producto. Quedarse con la primera y seguir habría hecho que el cliente
    /// pagase un precio que no eligió sin enterarse.
    /// </summary>
    [Fact]
    public async Task Create_InconsistentSnapshotForSameProduct_Returns400()
    {
        var request = NewOrder(Mug(1), Mug(1) with { UnitPrice = 9.99m });

        var response = await client.PostAsJsonAsync("/orders", request, CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(CancellationToken);

        Assert.NotNull(problem);

        // La clave sale en PascalCase con la forma que genera MVC para una
        // colección, heredada del error de producto desconocido de 2.3: el error
        // añadido a mano y los de las DataAnnotations son indistinguibles. El
        // índice es el de la primera aparición.
        var error = Assert.Single(problem.Errors, entry => entry.Key == "Items[0].ProductId");
        Assert.Contains(MugId.ToString(), string.Join(' ', error.Value));

        Assert.Equal(0, await factory.CountOrdersAsync(CancellationToken));
    }

    /// <summary>
    /// Cuerpo sin <c>customerEmail</c>, con las líneas completas para que sea lo
    /// único que falte. Va como JSON crudo y no como DTO porque
    /// <see cref="CreateOrderRequest"/> tiene los miembros <c>required</c>: en C#
    /// no se puede construir uno al que le falte un campo, que es justo lo que hay
    /// que enviar aquí.
    ///
    /// En 2.4 el segundo assert era que Catalog no recibía ni una petición — un
    /// cuerpo mal formado no debía costar viajes de red. Ya no hay red que gastar;
    /// lo que queda por afirmar es que tampoco toca la base.
    /// </summary>
    [Fact]
    public async Task Create_MissingRequiredField_Returns400()
    {
        const string body = """
            {
              "items": [
                {
                  "productId": 1,
                  "productSku": "TAZA-001",
                  "productName": "Taza Talavera Puebla",
                  "quantity": 2,
                  "unitPrice": 249.00
                }
              ]
            }
            """;

        var response = await client.PostAsync(
            "/orders",
            new StringContent(body, Encoding.UTF8, "application/json"),
            CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await factory.CountOrdersAsync(CancellationToken));
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
        var request = NewOrder(Mug(1)) with { CustomerEmail = "esto-no-es-un-correo" };

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
        Assert.Equal(0, await factory.CountOrdersAsync(CancellationToken));
    }

    /// <summary>
    /// Un sku de 51 caracteres, uno más de lo que admite
    /// <c>OrderItem.ProductSkuMaxLength</c>.
    ///
    /// **Este test cambió de dueño en 3.3 y conviene saberlo.** En 2.4 el sku
    /// largo lo devolvía Catalog, así que ninguna DataAnnotation podía verlo: lo
    /// paraba el guard de la entidad y lo traducía a 400 el
    /// <c>catch (ArgumentException)</c> del controller — sin ese catch habría sido
    /// un 500, y eso era lo que el test afirmaba. Ahora el valor viene en el
    /// cuerpo, así que lo corta el <c>[MaxLength]</c> del DTO **antes de que la
    /// acción se ejecute**, y la clave del error ya no es el <c>ParamName</c> de la
    /// excepción sino la ruta del modelo.
    ///
    /// El catch sigue en el controller como defensa en profundidad, pero este test
    /// ya no lo ejerce. Quien lo ejerza vendrá de una invariante que el DTO no
    /// pueda ver.
    /// </summary>
    [Fact]
    public async Task Create_OversizedSku_Returns400()
    {
        var request = NewOrder(Mug(1) with { ProductSku = new string('X', 51) });

        var response = await client.PostAsJsonAsync("/orders", request, CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(CancellationToken);

        Assert.NotNull(problem);
        Assert.Contains(problem.Errors.Keys, key => key.Contains(nameof(CreateOrderItemRequest.ProductSku)));

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
    /// El cuerpo lleva ahora los cinco campos de cada línea. Esa abundancia es el
    /// diseño de 3.3, igual que la pobreza lo era de 2.3: al no haber a quién
    /// preguntar, la foto la trae quien pide y Orders la congela sin discutirla.
    /// </summary>
    private static CreateOrderRequest NewOrder(params CreateOrderItemRequest[] lines) => new()
    {
        CustomerEmail = CustomerEmail,
        Items = lines,
    };

    private static CreateOrderItemRequest Mug(int quantity) => new()
    {
        ProductId = MugId,
        ProductSku = MugSku,
        ProductName = MugName,
        Quantity = quantity,
        UnitPrice = MugPrice,
    };

    private static CreateOrderItemRequest Keyring(int quantity) => new()
    {
        ProductId = KeyringId,
        ProductSku = KeyringSku,
        ProductName = KeyringName,
        Quantity = quantity,
        UnitPrice = KeyringPrice,
    };

    private async Task<OrderResponse> CreateOrderAsync(params CreateOrderItemRequest[] lines)
    {
        var response = await client.PostAsJsonAsync("/orders", NewOrder(lines), CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<OrderResponse>(CancellationToken);

        Assert.NotNull(created);

        return created;
    }
}
