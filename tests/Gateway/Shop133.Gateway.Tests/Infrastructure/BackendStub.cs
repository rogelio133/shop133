using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Xunit;

namespace Shop133.Gateway.Tests.Infrastructure;

/// <summary>
/// Una petición tal y como la recibió el servicio de destino. Es lo único que
/// esta suite puede afirmar del enrutado: <b>qué path le llegó al backend</b>.
/// </summary>
/// <param name="Method">El verbo, para distinguir el preflight del POST real.</param>
/// <param name="Path">
/// El path YA transformado por YARP. Es el campo entero del punto: la diferencia
/// entre "/products" y "/orders" —y entre "/orders" y la cadena vacía que en 5.1
/// costó un 404— vive aquí.
/// </param>
/// <param name="QueryString">Incluye el '?'; cadena vacía si no había.</param>
/// <param name="Body">El cuerpo tal cual, para comprobar que el POST se reenvía íntegro.</param>
internal sealed record RecordedRequest(string Method, string Path, string QueryString, string Body);

/// <summary>
/// El sustituto de Catalog.API / Orders.API al otro lado del Gateway.
///
/// <para>
/// Tiene que ser un servidor HTTP <b>de verdad</b>, escuchando en un puerto de
/// verdad, y eso no es una comodidad: bajo <c>WebApplicationFactory</c> la entrada
/// al Gateway es en memoria, pero el reenvío de YARP sale por un socket real. El
/// destino no puede ser otro <c>TestServer</c>.
/// </para>
///
/// <para>
/// <b>Se anuncia en el literal 127.0.0.1 y nunca en "localhost"</b>, que es la
/// misma trampa que este repositorio lleva anotando desde 2.3 (una conexión a
/// localhost resuelve a ::1 y a 127.0.0.1) y que 5.2 volvió a medir en otra forma:
/// son DOS claves de partición distintas para el rate limiter. Aquí se ata
/// directamente al literal, así que no hay ambigüedad que arrastrar.
/// </para>
///
/// <para>
/// <b>Arranca en el constructor, de forma síncrona, y eso es deliberado.</b> La
/// fábrica del Gateway necesita la URL del stub para poder configurarse, así que
/// el stub tiene que existir antes — exactamente el orden que 2.4 descubrió con
/// <c>CatalogStub</c>. Con un arranque asíncrono habría que montarlo en
/// <c>InitializeAsync</c> y las clases de test no podrían construir su fábrica en
/// el constructor.
/// </para>
///
/// <para>
/// <i>Descartado</i> WireMock.Net, el paquete que 3.3 borró junto con la deuda
/// síncrona de la Fase 2. Lo que hace falta aquí es apuntar el path recibido y
/// devolver una respuesta fija: dos líneas. Resucitar un framework de mocking para
/// eso volvería a meter en el repositorio una dependencia que se quitó a propósito.
/// </para>
/// </summary>
internal sealed class BackendStub : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly List<RecordedRequest> requests = [];
    private readonly object gate = new();

    public BackendStub()
    {
        var builder = WebApplication.CreateBuilder();

        // Sin esto, cada stub escupe sus propias líneas de Kestrel en la salida de
        // la suite y el fallo de verdad se pierde de vista.
        builder.Logging.ClearProviders();

        // Puerto 0 = "el que haya libre". Nada de puertos fijos: el 5124 y el 5189
        // los ocupan Catalog.API y Orders.API cuando alguien los tiene arrancados,
        // y esta suite tiene que poder correr con el IDE abierto. Es el mismo
        // criterio con el que SqlServerContainerFixture no fija el 1433.
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        app = builder.Build();

        // Un único endpoint que engancha con TODO —cualquier verbo, cualquier
        // path, incluida la raíz—, porque lo que se prueba es justamente qué path
        // llega. MapFallback NO vale: su patrón lleva la restricción ':nonfile',
        // así que un path que parezca un fichero no engancharía y el stub
        // contestaría 404 como si el Gateway se hubiera equivocado de sitio.
        app.Map("/{**catch-all}", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(context.RequestAborted);

            lock (gate)
            {
                requests.Add(new RecordedRequest(
                    context.Request.Method,
                    context.Request.Path.Value ?? string.Empty,
                    context.Request.QueryString.Value ?? string.Empty,
                    body));
            }

            foreach (var (name, value) in ResponseHeaders)
            {
                context.Response.Headers[name] = value;
            }

            context.Response.StatusCode = StatusCode;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(ResponseBody, context.RequestAborted);
        });

        app.Start();
        Url = ResolveUrl();
    }

    /// <summary>Dirección real del stub, con barra final, lista para el cluster de YARP.</summary>
    public string Url { get; }

    /// <summary>Código que devuelve. Mutable: la ruta de orders necesita un 201.</summary>
    public int StatusCode { get; set; } = StatusCodes.Status200OK;

    /// <summary>Cuerpo que devuelve, tal cual.</summary>
    public string ResponseBody { get; set; } = """{"stub":true}""";

    /// <summary>
    /// Cabeceras extra de la respuesta. Sirve para el <c>Location</c> del 201, que es
    /// lo que 5.3 expone con <c>WithExposedHeaders</c> para que 6.5 pueda leerlo.
    /// </summary>
    public Dictionary<string, string> ResponseHeaders { get; } = [];

    /// <summary>
    /// Lo que le llegó, en orden. Se devuelve una copia: un test que compruebe
    /// "no le llegó nada" no debe poder ver una lista que sigue mutando.
    /// </summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (gate)
            {
                return [.. requests];
            }
        }
    }

    /// <summary>La única petición que recibió. Falla si recibió cero o más de una.</summary>
    public RecordedRequest SingleRequest() => Assert.Single(Requests);

    private string ResolveUrl()
    {
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;

        var address = addresses?.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "El stub arrancó pero no publicó ninguna dirección. Sin ella no se puede " +
                "configurar el destino del cluster de YARP.");

        return address.EndsWith('/') ? address : address + "/";
    }

    public async ValueTask DisposeAsync() => await app.DisposeAsync();
}
