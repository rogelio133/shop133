using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Shop133.Gateway.Tests.Infrastructure;

using Xunit;

namespace Shop133.Gateway.Tests;

/// <summary>
/// Los dos invariantes que 5.2 y 5.3 dejaron anotados como <i>"sin vigilar"</i> y le
/// pasaron por escrito a este punto.
///
/// <para>
/// A diferencia del resto de la suite, estos no mandan ni una petición: leen la
/// <b>configuración ya cargada por el Gateway</b>
/// (<c>factory.Services.GetRequiredService&lt;IConfiguration&gt;()</c>), con la fábrica
/// construida <b>sin sobreescribir nada</b>. Así se comprueba lo que se despliega de
/// verdad y no un fichero suelto — y de paso no hay que parsear a mano un
/// <c>appsettings.json</c> que lleva comentarios <c>//</c>.
/// </para>
///
/// <para>
/// <i>Descartado</i> ponerlos en <c>Shop133.ArchitectureTests</c>. Esa suite lee
/// <c>.csproj</c> y rutas de fichero y nada más; meterle un lector de JSON sería
/// estrenar una capacidad nueva en <c>ProjectGraph</c> y desmentir lo que CLAUDE.md
/// dice que son esas reglas. La suite de arquitectura se queda en <b>17</b>.
/// </para>
///
/// <para>
/// Su valor no está en las dos rutas de hoy —el resto de la suite ya las ejerce— sino
/// en <b>la tercera que alguien añada mañana</b>: enumeran las rutas que haya, así que
/// una nueva que se olvide de su política falla aquí en vez de nacer sin CORS o sin
/// límite.
/// </para>
/// </summary>
[Trait("Category", "Fast")]
public sealed class GatewayConfigurationTests : IAsyncDisposable
{
    private readonly GatewayFactory factory = new();

    private IConfiguration Configuration => factory.Services.GetRequiredService<IConfiguration>();

    [Fact]
    public void GlobalQuota_StaysAboveEveryRouteQuota()
    {
        // El detalle que 5.2 dejó documentado y sin vigilancia: cuando una ruta
        // declara su política se aplican LAS DOS, la del endpoint y la global. Si el
        // cupo global quedara por debajo, sería él quien limita de verdad y las dos
        // políticas por ruta quedarían decorativas — con los números de
        // appsettings.json pareciendo perfectamente razonables y nada que lo delatara.
        var global = Configuration.GetValue<int>("RateLimiting:Global:PermitLimit");

        // Se enumeran todas las secciones de cupo en vez de nombrar CatalogRead y
        // OrdersWrite: así una política de cupo nueva queda cubierta el día que entre,
        // que es cuando el error es más fácil de cometer.
        var offenders = Configuration.GetSection("RateLimiting").GetChildren()
            .Where(quota => quota.Key != "Global")
            .Select(quota => (quota.Key, Permits: quota.GetValue<int>("PermitLimit")))
            .Where(quota => quota.Permits >= global)
            .Select(quota => $"{quota.Key} ({quota.Permits})")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"El cupo global (RateLimiting:Global:PermitLimit = {global}) tiene que quedar POR " +
            "ENCIMA del de cada ruta: cuando una ruta declara su RateLimiterPolicy se aplican " +
            "las dos, así que un global más bajo sería el limitador real y dejaría decorativas " +
            "a las políticas por ruta. Cupos que lo alcanzan o lo superan: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void EveryRoute_DeclaresACorsPolicy()
    {
        // La decisión 8 de 5.3: sin "CorsPolicy" YARP ni siquiera engancha el
        // preflight de la ruta ("If not set then the route won't be automatically
        // matched for cors preflight requests"), así que el navegador la bloquea.
        //
        // 5.3 descartó a propósito una política por defecto como red de seguridad
        // —el fallo es ruidoso, no silencioso— y eso deja esta comprobación como lo
        // único que cubre una ruta futura.
        var offenders = RoutesMissing("CorsPolicy");

        Assert.True(
            offenders.Count == 0,
            "Toda ruta de ReverseProxy:Routes declara su \"CorsPolicy\": sin ella YARP no " +
            "engancha el preflight, el navegador bloquea la petición y el Gateway no dice nada. " +
            "5.3 descartó una política CORS por defecto, así que no hay red de seguridad detrás. " +
            "Rutas sin política: " + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryRoute_DeclaresARateLimiterPolicy()
    {
        // El mismo hueco por el lado de 5.2. Aquí SÍ hay red de seguridad —el
        // GlobalLimiter—, así que una ruta sin política no queda sin límite; queda
        // con el cupo genérico de 120/min, que para un POST que arranca la saga
        // entera es tanto como no tener límite. El fallo es silencioso por partida
        // doble: ni error, ni log, ni 429 hasta muy tarde.
        var offenders = RoutesMissing("RateLimiterPolicy");

        Assert.True(
            offenders.Count == 0,
            "Toda ruta de ReverseProxy:Routes declara su \"RateLimiterPolicy\". Sin ella la ruta " +
            "cae en el GlobalLimiter (120/min), que es la red de seguridad de 5.2 y no un cupo " +
            "pensado para el coste de esa ruta. Rutas sin política: " + string.Join(", ", offenders));
    }

    private List<string> RoutesMissing(string key)
    {
        var routes = Configuration.GetSection("ReverseProxy:Routes").GetChildren().ToList();

        // Si esto se queda a cero, el resto del test pasaría en vacío: es el "filtro
        // que nunca engancha" de 3.2. La guarda de Program.cs ya revienta antes por
        // este mismo motivo, pero aquí la lectura es de otra fuente y conviene
        // afirmarlo donde se usa.
        Assert.NotEmpty(routes);

        return [.. routes
            .Where(route => string.IsNullOrWhiteSpace(route[key]))
            .Select(route => route.Key)];
    }

    public async ValueTask DisposeAsync() => await factory.DisposeAsync();
}
