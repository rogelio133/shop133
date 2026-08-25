using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc;

using Orders.API.Models;
using Orders.Tests.Infrastructure;

using Xunit;

namespace Orders.Tests;

/// <summary>
/// PHASE-2 DEBT: "Catalog caído ⇒ el pedido no se crea", en forma ejecutable.
///
/// Esta clase entera se borra en 3.7, junto con <c>CatalogClient</c> y el paquete
/// WireMock.Net. No es un test que envejezca mal: es un test que **deja de tener
/// sentido** en cuanto Orders publique <c>OrderCreated</c> en lugar de preguntar,
/// porque entonces no habrá ningún servicio cuya caída pueda impedir que se cree
/// el pedido. El diff que lo elimina documenta el cambio de arquitectura mejor
/// que un párrafo.
///
/// Cubre los cinco modos de fallo de CatalogClient. Uno de ellos —el timeout— no
/// lo había ejecutado nadie hasta 2.4: la verificación manual de 2.3 solo pudo
/// forzar la conexión rechazada.
///
/// **Todos afirman además que la tabla Orders queda vacía.** El código de estado
/// dice lo que vio el cliente; el recuento dice lo que pasó de verdad, y es la
/// mitad que importa — un 502 sobre un pedido a medio escribir sería el peor de
/// los mundos.
/// </summary>
[Collection(OrdersApiCollection.Name)]
[Trait("Category", "Docker")]
public sealed class CatalogUnavailableTests : IAsyncLifetime
{
    private const string CustomerEmail = "cliente@shop133.test";

    private const int MugId = 1;
    private const int KeyringId = 2;
    private const int UnknownProductId = 999_999;

    /// <summary>
    /// El <c>Timeout</c> que Program.cs le pone al HttpClient de Catalog. Está
    /// duplicado aquí a propósito: si alguien lo cambia allí, este test falla y
    /// obliga a mirar por qué, que es exactamente lo que debe pasar con un número
    /// del que depende una rama entera del código.
    /// </summary>
    private static readonly TimeSpan CatalogTimeout = TimeSpan.FromSeconds(5);

    private readonly CatalogStub catalog;
    private readonly OrdersApiFactory factory;
    private HttpClient client = null!;

    public CatalogUnavailableTests(SqlServerContainerFixture container)
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

    /// <summary>
    /// Catalog no escucha en su puerto. Es el escenario que el roadmap pide por su
    /// nombre y el que da sentido a toda la Fase 3.
    ///
    /// El stub se arranca y se para en vez de inventarse un puerto libre: este se
    /// sabe cerrado porque acaba de cerrarse. La conexión se rechaza al instante
    /// porque <see cref="CatalogStub.Url"/> usa el literal 127.0.0.1 — con
    /// <c>localhost</c> costaría ~4,13 s y este test sería indistinguible del del
    /// timeout.
    /// </summary>
    [Fact]
    public async Task Create_CatalogRefusesConnection_Returns502AndCreatesNothing()
    {
        catalog.Stop();

        var response = await client.PostAsJsonAsync("/orders", NewOrder((MugId, 2)), CancellationToken);

        await AssertCatalogUnavailableAsync(response);
    }

    /// <summary>
    /// **La rama que nunca se había ejecutado.** Catalog contesta, pero tarda más
    /// de lo que el HttpClient está dispuesto a esperar: eso llega a CatalogClient
    /// como <c>TaskCanceledException</c>, la misma excepción que produce un cliente
    /// que cierra la pestaña. Lo que las separa es el filtro
    /// <c>when (!cancellationToken.IsCancellationRequested)</c> — sin él, cerrar
    /// una pestaña se registraría como una caída de Catalog.
    ///
    /// El cronómetro es la prueba de que cortó el cliente y no el servidor: si
    /// alguien quitara el <c>Timeout</c> de 5 s de Program.cs (el defecto son 100 s),
    /// el test tardaría los 10 s del stub y fallaría por la cota superior.
    /// Umbrales en segundos, nunca en milisegundos: CLAUDE.md ya explica por qué.
    /// </summary>
    [Fact]
    public async Task Create_CatalogTimesOut_Returns502AfterTheClientTimeout()
    {
        var serverDelay = CatalogTimeout * 2;

        catalog.StubProductSlow(MugId, serverDelay, "TAZA-001", "Taza Talavera Puebla", 249.00m);

        var stopwatch = Stopwatch.StartNew();

        var response = await client.PostAsJsonAsync("/orders", NewOrder((MugId, 1)), CancellationToken);

        stopwatch.Stop();

        await AssertCatalogUnavailableAsync(response);

        // Un poco por debajo de los 5 s exactos: entre que arranca el cronómetro y
        // que sale la petición hay milisegundos de MVC que no cuentan para el
        // Timeout, y no merece la pena que el test sea frágil por eso.
        Assert.True(
            stopwatch.Elapsed > CatalogTimeout - TimeSpan.FromMilliseconds(500),
            $"Cortó demasiado pronto ({stopwatch.Elapsed}); el Timeout del HttpClient es {CatalogTimeout}.");

        Assert.True(
            stopwatch.Elapsed < serverDelay - TimeSpan.FromSeconds(1),
            $"Esperó {stopwatch.Elapsed}: no cortó el cliente, contestó el servidor tras {serverDelay}.");
    }

    /// <summary>
    /// Catalog está vivo pero roto. Un 5xx no es "el producto no existe" —eso es el
    /// 404, que CatalogClient traduce a <c>null</c>— así que no puede acabar en un
    /// 400 culpando al cliente de un problema que no es suyo.
    /// </summary>
    [Fact]
    public async Task Create_CatalogReturnsServerError_Returns502AndCreatesNothing()
    {
        catalog.StubProductError(MugId, 500);

        var response = await client.PostAsJsonAsync("/orders", NewOrder((MugId, 1)), CancellationToken);

        await AssertCatalogUnavailableAsync(response);
    }

    /// <summary>
    /// Catalog contesta 200 con un cuerpo que no se puede interpretar: le faltan
    /// miembros que <c>CatalogProduct</c> declara <c>required</c>, así que
    /// System.Text.Json lanza <c>JsonException</c>.
    ///
    /// Sin el <c>catch (JsonException)</c> de CatalogClient esto sería un 500 sin
    /// traducir. Es también el escenario más parecido a un despliegue con
    /// contratos desalineados, que es de lo que protege Shop133.Contracts a partir
    /// de la Fase 3.
    /// </summary>
    [Fact]
    public async Task Create_CatalogReturnsMalformedBody_Returns502AndCreatesNothing()
    {
        catalog.StubRawBody(MugId, """{"id":1}""");

        var response = await client.PostAsJsonAsync("/orders", NewOrder((MugId, 1)), CancellationToken);

        await AssertCatalogUnavailableAsync(response);
    }

    /// <summary>
    /// **La lección central del acoplamiento síncrono.** La primera línea se
    /// resuelve bien y la segunda revienta: el <c>try</c> envuelve el bucle
    /// entero, así que un éxito parcial no escribe nada. No hay pedidos a medias.
    ///
    /// El assert sobre el contador del stub es el que demuestra que la primera
    /// petición sí llegó a hacerse — sin él, el test pasaría igual si el
    /// controller fallara antes de empezar.
    /// </summary>
    [Fact]
    public async Task Create_CatalogFailsOnTheSecondLine_CreatesNothing()
    {
        catalog.StubProduct(MugId, "TAZA-001", "Taza Talavera Puebla", 249.00m);
        catalog.StubProductError(KeyringId, 503);

        var response = await client.PostAsJsonAsync(
            "/orders",
            NewOrder((MugId, 1), (KeyringId, 1)),
            CancellationToken);

        await AssertCatalogUnavailableAsync(response);

        Assert.Equal(1, catalog.RequestCountFor(MugId));
    }

    /// <summary>
    /// Los dos fallos a la vez y gana el 502: el <c>catch</c> devuelve antes de que
    /// se mire el ModelState, así que el producto desconocido de la primera línea
    /// ni se menciona. Es lo correcto —no se le puede pedir al cliente que arregle
    /// un pedido cuya validación no ha terminado— pero hoy ese orden solo existe
    /// por la forma del método, y sin este test se rompería en silencio.
    /// </summary>
    [Fact]
    public async Task Create_UnknownProductAndCatalogDown_Returns502NotBadRequest()
    {
        catalog.StubProductNotFound(UnknownProductId);
        catalog.StubProductError(KeyringId, 500);

        var response = await client.PostAsJsonAsync(
            "/orders",
            NewOrder((UnknownProductId, 1), (KeyringId, 1)),
            CancellationToken);

        await AssertCatalogUnavailableAsync(response);
    }

    // ── Ayudas ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Lo que tiene que ser cierto en los seis escenarios: 502, cuerpo de problema
    /// bien formado y ni un pedido en la base.
    ///
    /// El <c>502</c> y no un <c>503</c> es deliberado (2.3): Orders está vivo, el
    /// que no está es su dependencia, y el 502 hace que quien lo lee se pregunte
    /// qué hay detrás de Orders. El <c>traceId</c> es lo que aporta construirlo con
    /// <c>Problem(...)</c> en vez de con <c>StatusCode(502, new ProblemDetails())</c>:
    /// pasa por el ProblemDetailsFactory, que pone el content-type y la traza que
    /// la Fase 7 va a querer.
    /// </summary>
    private async Task AssertCatalogUnavailableAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken);

        Assert.NotNull(problem);
        Assert.Equal("Catalog no disponible", problem.Title);
        Assert.Contains("no se ha creado", problem.Detail ?? string.Empty);
        Assert.True(problem.Extensions.ContainsKey("traceId"), "El ProblemDetails no trae traceId.");

        Assert.Equal(0, await factory.CountOrdersAsync(CancellationToken));
    }

    private static CreateOrderRequest NewOrder(params (int ProductId, int Quantity)[] lines) => new()
    {
        CustomerEmail = CustomerEmail,
        Items = [.. lines.Select(line => new CreateOrderItemRequest
        {
            ProductId = line.ProductId,
            Quantity = line.Quantity,
        })],
    };
}
