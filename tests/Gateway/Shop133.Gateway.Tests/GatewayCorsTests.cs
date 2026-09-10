using System.Net;
using System.Net.Http.Json;

using Shop133.Gateway.Tests.Infrastructure;

using Xunit;

namespace Shop133.Gateway.Tests;

/// <summary>
/// Lo que 5.3 le encargó por escrito a este punto: el preflight en las dos rutas, la
/// ausencia de cabeceras con un origen ajeno, y el 429 <b>con</b> cabeceras CORS.
///
/// <para>
/// Los dos tests que más valen son los dos últimos, y los dos vigilan la misma línea:
/// <b><c>app.UseCors()</c> va antes de <c>app.UseRateLimiter()</c></b>. Invertir ese
/// orden no rompe nada visible desde el servidor —los códigos de estado siguen siendo
/// los mismos— y sin embargo deja el límite indiagnosticable desde el navegador y
/// penaliza al cliente que respeta CORS. Nada más que estos dos tests lo verían.
/// </para>
/// </summary>
[Trait("Category", "Fast")]
public sealed class GatewayCorsTests : IAsyncDisposable
{
    // Los dos orígenes de Cors:AllowedOrigins en appsettings.json: los dos perfiles
    // de launchSettings.json de Shop133.Web, que es el único origen de navegador que
    // existirá cuando llegue la Fase 6.
    private const string AllowedOrigin = "http://localhost:5025";
    private const string SecondAllowedOrigin = "https://localhost:7227";
    private const string ForeignOrigin = "https://ajeno.example";

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

    [Theory]
    [InlineData("/api/catalog/products", "GET")]
    [InlineData("/api/orders", "POST")]
    public async Task Preflight_FromAllowedOrigin_Returns204WithCorsHeaders(string path, string method)
    {
        // Las DOS rutas, porque la política se declara por ruta con "CorsPolicy" y
        // olvidarla en una sola es el fallo que este test tiene que pillar. Sin esa
        // línea YARP ni siquiera engancha el preflight y el servicio contesta 405.
        var client = ClientWith();

        var response = await SendPreflightAsync(client, path, method, AllowedOrigin);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(AllowedOrigin, Single(response, "Access-Control-Allow-Origin"));
        Assert.Contains(method, Single(response, "Access-Control-Allow-Methods"));

        // SetPreflightMaxAge(10 min) de 5.3: sin él el navegador repetiría el
        // preflight en cada POST.
        Assert.Equal("600", Single(response, "Access-Control-Max-Age"));
    }

    [Fact]
    public async Task Preflight_FromSecondAllowedOrigin_IsAlsoAccepted()
    {
        // El segundo perfil de Shop133.Web (https). Que la lista tenga dos entradas y
        // solo se pruebe la primera es la forma clásica de que la segunda esté mal
        // escrita durante meses.
        var client = ClientWith();

        var response = await SendPreflightAsync(
            client, "/api/catalog/categories", "GET", SecondAllowedOrigin);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(SecondAllowedOrigin, Single(response, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Preflight_FromForeignOrigin_HasNoAllowOriginHeader()
    {
        var client = ClientWith();

        var response = await SendPreflightAsync(
            client, "/api/catalog/products", "GET", ForeignOrigin);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Preflight_DoesNotReachTheBackend()
    {
        // Lo corta el middleware de CORS en el Gateway. Medido en 5.3 por la vía
        // contraria: un OPTIONS que SÍ llega al servicio recibe un 405, porque los
        // controllers no saben nada de CORS.
        var client = ClientWith();

        await SendPreflightAsync(client, "/api/catalog/products", "GET", AllowedOrigin);

        Assert.Empty(catalog.Requests);
    }

    [Fact]
    public async Task Response_FromAllowedOrigin_ExposesLocationAndRetryAfter()
    {
        // La parte menos obvia de 5.3. El navegador solo deja que el JavaScript lea
        // seis cabeceras de respuesta, y ni Location ni Retry-After están entre
        // ellas: sin este WithExposedHeaders, el polling de 6.5 no puede saber qué
        // pedido acaba de crear.
        var client = ClientWith();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/catalog/products");
        request.Headers.Add("Origin", AllowedOrigin);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var exposed = Single(response, "Access-Control-Expose-Headers");
        Assert.Contains("Location", exposed);
        Assert.Contains("Retry-After", exposed);
    }

    [Fact]
    public async Task Response_FromForeignOrigin_HasNoCorsHeadersButFullBody()
    {
        // LA verificación que resume 5.3: **CORS no es autorización**. El cuerpo
        // entero sale igual; lo único que falta es la cabecera que autoriza al
        // navegador a dejar que su JavaScript lo lea. Un curl se lo salta por
        // completo. Quien controla el acceso de verdad es 8.1.
        //
        // Escrito como test para que nadie lea el 200 sin cabeceras como un fallo y
        // "lo arregle" creyendo que aquí hay un control de acceso que no existe.
        catalog.ResponseBody = """{"total":50}""";
        var client = ClientWith();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/catalog/products");
        request.Headers.Add("Origin", ForeignOrigin);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "\"total\":50",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task RateLimitRejection_CarriesTheCorsHeaders()
    {
        // Vigila el orden del pipeline. Con UseRateLimiter antes que UseCors, el
        // middleware de CORS ni llega a ejecutarse en el rechazo: el 429 sale sin
        // cabeceras, el navegador solo ve un error de red opaco y el JavaScript no
        // puede leer ni el código ni el Retry-After. El límite seguiría funcionando
        // y sería indiagnosticable desde el cliente, que es lo contrario de lo que
        // el 429 existe para decirle.
        var client = ClientWith(("RateLimiting:CatalogRead:PermitLimit", "1"));

        await SendWithOriginAsync(client, "/api/catalog/products");
        var rejected = await SendWithOriginAsync(client, "/api/catalog/products");

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(AllowedOrigin, Single(rejected, "Access-Control-Allow-Origin"));
        Assert.Contains("Retry-After", Single(rejected, "Access-Control-Expose-Headers"));
    }

    [Fact]
    public async Task Preflight_DoesNotSpendRateLimitQuota()
    {
        // La otra mitad del orden del pipeline, y la consecuencia salió peor de lo
        // previsto al medirla invertida en 5.3: con el cupo en 1, el OPTIONS se come
        // el único permiso y el POST de detrás recibe 429 — o sea que un navegador no
        // consigue crear NI UN pedido mientras un curl con el mismo cupo lo crea sin
        // problema. **El cliente que respeta CORS sale penalizado por preguntar.**
        var client = ClientWith(("RateLimiting:OrdersWrite:PermitLimit", "1"));

        var firstPreflight = await SendPreflightAsync(client, "/api/orders", "POST", AllowedOrigin);
        var secondPreflight = await SendPreflightAsync(client, "/api/orders", "POST", AllowedOrigin);

        var created = await client.PostAsJsonAsync(
            "/api/orders",
            new { customerEmail = "cliente@example.com" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, firstPreflight.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, secondPreflight.StatusCode);

        // Dos preflights antes y el único permiso sigue intacto.
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
    }

    private static Task<HttpResponseMessage> SendPreflightAsync(
        HttpClient client, string path, string method, string origin)
    {
        // Un OPTIONS SIN "Access-Control-Request-Method" no es un preflight: se
        // reenvía al servicio y devuelve 405. Medido en 5.3, y es la forma más fácil
        // de escribir aquí un test que falle por el motivo equivocado.
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", "content-type");

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> SendWithOriginAsync(HttpClient client, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Origin", AllowedOrigin);

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string Single(HttpResponseMessage response, string header)
    {
        Assert.True(
            response.Headers.Contains(header),
            $"Falta la cabecera '{header}'. Presentes: " +
            string.Join(", ", response.Headers.Select(h => h.Key)));

        return string.Join(",", response.Headers.GetValues(header));
    }

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
