using System.Net;
using System.Net.Http.Json;

using Shop133.Gateway.Tests.Infrastructure;

using Xunit;

namespace Shop133.Gateway.Tests;

/// <summary>
/// La segunda mitad del título de 5.4: <i>"el rate limiting de 5.2 devuelve 429 al
/// superar el umbral"</i>.
///
/// <para>
/// <b>Cada test baja el cupo por configuración en vez de mandar sesenta peticiones.</b>
/// Eso no es un atajo del test: es el motivo por el que 5.2 dejó los cupos en
/// <c>appsettings.json</c> en lugar de como literales, y la cabecera del
/// <c>Program.cs</c> del Gateway nombra a este punto por escrito al explicarlo.
/// </para>
///
/// <para>
/// <b>Cada test estrena su propia fábrica</b> —y por tanto su propio limitador— porque
/// el cupo gastado vive en el host. Ver la explicación larga en
/// <see cref="GatewayFactory"/>: bajo <c>TestServer</c> la clave de partición es
/// "unknown" para todo el proceso, así que dos tests que compartieran host se
/// heredarían los 429.
/// </para>
///
/// <para>
/// Lo que <b>no</b> se prueba aquí, y se dice en voz alta en la sección Pendiente del
/// documento: la partición <i>por IP</i>. Sin socket no hay
/// <c>RemoteIpAddress</c>, así que todas las peticiones de un test comparten clave y
/// nadie comprueba que dos clientes distintos tengan cubos distintos.
/// </para>
/// </summary>
[Trait("Category", "Fast")]
public sealed class GatewayRateLimitingTests : IAsyncDisposable
{
    private readonly BackendStub catalog = new();
    private readonly BackendStub orders = new();
    private readonly List<GatewayFactory> factories = [];

    private HttpClient ClientWith(params (string Key, string Value)[] settings)
    {
        var factory = new GatewayFactory(
            catalog.Url,
            orders.Url,
            settings.ToDictionary(s => s.Key, s => s.Value));

        factories.Add(factory);
        return factory.CreateClient();
    }

    [Fact]
    public async Task CatalogRead_BeyondItsQuota_Returns429()
    {
        var client = ClientWith(("RateLimiting:CatalogRead:PermitLimit", "2"));

        var first = await client.GetAsync("/api/catalog/products", TestContext.Current.CancellationToken);
        var second = await client.GetAsync("/api/catalog/products", TestContext.Current.CancellationToken);
        var third = await client.GetAsync("/api/catalog/products", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);

        // La petición rechazada NO llega al servicio: el límite defiende el trabajo
        // que hay detrás, no solo el número de respuestas.
        Assert.Equal(2, catalog.Requests.Count);
    }

    [Fact]
    public async Task OrdersWrite_BeyondItsQuota_Returns429()
    {
        var client = ClientWith(("RateLimiting:OrdersWrite:PermitLimit", "1"));

        var first = await PostOrderAsync(client);
        var second = await PostOrderAsync(client);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);

        // El pedido rechazado no arranca la saga, que es exactamente lo que este
        // cupo —10/min frente a los 60 de lectura— existe para proteger.
        Assert.Single(orders.Requests);
    }

    [Fact]
    public async Task Rejection_CarriesRetryAfterAndProblemDetails()
    {
        var client = ClientWith(("RateLimiting:CatalogRead:PermitLimit", "1"));

        await client.GetAsync("/api/catalog/products", TestContext.Current.CancellationToken);
        var rejected = await client.GetAsync("/api/catalog/products", TestContext.Current.CancellationToken);

        // 429 y NO 503. El valor por defecto de RejectionStatusCode es 503, así que
        // sin la línea que lo pone a mano en Program.cs el punto entregaría un
        // limitador correcto con el código equivocado — medido en 5.2.
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

        // Retry-After sale de los metadatos del lease. Dice la ventana ENTERA y no
        // lo que queda de ella (medido en 5.2): es conservador, no un fallo.
        Assert.NotNull(rejected.Headers.RetryAfter);
        Assert.Equal(TimeSpan.FromSeconds(60), rejected.Headers.RetryAfter!.Delta);

        // Misma forma de error que producen los cinco servicios desde 2.3.
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);

        var body = await rejected.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("traceId", body);
        Assert.Contains("429", body);
    }

    [Fact]
    public async Task ExhaustingTheReadQuota_DoesNotAffectTheWriteQuota()
    {
        // La medición de 5.2 en forma ejecutable: son dos cubos, no uno. Si alguien
        // "simplificara" las dos políticas en una sola compartida, este es el único
        // test que se enteraría — los otros seguirían en verde.
        var client = ClientWith(("RateLimiting:CatalogRead:PermitLimit", "1"));

        await client.GetAsync("/api/catalog/products", TestContext.Current.CancellationToken);
        var readRejected = await client.GetAsync("/api/catalog/products", TestContext.Current.CancellationToken);
        var write = await PostOrderAsync(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, readRejected.StatusCode);
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);
    }

    [Fact]
    public async Task GlobalLimiter_AppliesToRequestsWithoutARoute()
    {
        // La red de seguridad que 5.2 añadió sin que el título la pidiera: una ruta
        // futura que se olvide de declarar su RateLimiterPolicy nace limitada igual,
        // en vez de nacer sin límite y sin que nadie se entere.
        //
        // Se comprueba contra un path SIN ruta, que es el único sitio donde el
        // limitador global actúa solo. Sobre una ruta con política se aplican las
        // dos, así que allí no se podría distinguir cuál de los dos rechazó.
        var client = ClientWith(("RateLimiting:Global:PermitLimit", "1"));

        var first = await client.GetAsync("/api/inventory/anything", TestContext.Current.CancellationToken);
        var second = await client.GetAsync("/api/inventory/anything", TestContext.Current.CancellationToken);

        // Sin ruta que engancharlo, el primero es un 404 del Gateway...
        Assert.Equal(HttpStatusCode.NotFound, first.StatusCode);

        // ...y el segundo ya ni llega a enrutarse: 429 y no 404. Ése es el orden que
        // prueba que el limitador global es anterior al enrutado y cubre lo que no
        // declara política.
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    private static Task<HttpResponseMessage> PostOrderAsync(HttpClient client) =>
        client.PostAsJsonAsync(
            "/api/orders",
            new { customerEmail = "cliente@example.com" },
            TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        foreach (var factory in factories)
        {
            await factory.DisposeAsync();
        }

        await catalog.DisposeAsync();
        await orders.DisposeAsync();
    }
}
