using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Shop133.Gateway.Tests.Infrastructure;

/// <summary>
/// Levanta el Gateway de verdad —su <c>Program.cs</c> entero, con sus tres guardas,
/// su pipeline y su <c>appsettings.json</c>— y le cambia solo dos cosas: a dónde
/// reenvía y, cuando el test lo pide, cuánto cupo tiene.
///
/// <para>
/// <b>Todo entra por <c>UseSetting</c> y no por <c>ConfigureTestServices</c></b>, por
/// el motivo que las fábricas de Catalog y Orders llevan documentado desde 3.1:
/// <c>Program.cs</c> lee sus claves y lanza <b>antes de <c>app.Build()</c></b>, así que
/// sustituir servicios llega tarde — el host ni se construye. Aquí además no hay
/// nada que sustituir: el Gateway no tiene DbContext ni bus, solo configuración.
/// </para>
///
/// <para>
/// Los destinos son sobreescribibles por diseño, no por una concesión a los tests:
/// el <c>appsettings.json</c> del Gateway documenta ese patrón desde 5.1 pensando en
/// el día que tenga contenedor, y la cabecera de su <c>Program.cs</c> nombra a 5.4
/// como el motivo de que los cupos vivan en configuración.
/// </para>
///
/// <para>
/// <b>El estado del rate limiter vive en el host, así que cada test necesita su
/// propia fábrica.</b> Bajo <c>TestServer</c> no hay socket y
/// <c>Connection.RemoteIpAddress</c> es null, de modo que <c>ClientPartitionKey</c>
/// devuelve "unknown" para todo el proceso: una sola partición compartida. Dos tests
/// que compartieran fábrica se filtrarían los 429 el uno al otro y el segundo pasaría
/// —o fallaría— por lo que hizo el primero. Como xUnit construye la clase de test una
/// vez por método, basta con que sea un campo de instancia; es el mismo mecanismo que
/// da una base de datos por test en Catalog y Orders.
/// </para>
/// </summary>
internal sealed class GatewayFactory : WebApplicationFactory<Program>
{
    private readonly string? catalogAddress;
    private readonly string? ordersAddress;
    private readonly IReadOnlyDictionary<string, string> settings;

    /// <summary>
    /// Los destinos son opcionales: <c>GatewayConfigurationTests</c> construye la
    /// fábrica <b>sin sobreescribir nada</b>, porque lo que comprueba es la
    /// configuración que se despliega de verdad.
    /// </summary>
    public GatewayFactory(
        string? catalogAddress = null,
        string? ordersAddress = null,
        IReadOnlyDictionary<string, string>? settings = null)
    {
        this.catalogAddress = catalogAddress;
        this.ordersAddress = ordersAddress;
        this.settings = settings ?? new Dictionary<string, string>();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Testing" y no el "Development" por defecto, igual que las otras dos
        // fábricas. Aquí no hay User Secrets que evitar —el Gateway no tiene
        // UserSecretsId— pero sí un appsettings.Development.json que podría ganar
        // overrides cualquier día, y lo que esta suite tiene que probar es la
        // configuración que se despliega.
        builder.UseEnvironment("Testing");

        if (catalogAddress is not null)
        {
            builder.UseSetting(
                "ReverseProxy:Clusters:catalog:Destinations:primary:Address", catalogAddress);
        }

        if (ordersAddress is not null)
        {
            builder.UseSetting(
                "ReverseProxy:Clusters:orders:Destinations:primary:Address", ordersAddress);
        }

        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }
    }
}
