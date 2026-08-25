using System.Globalization;

using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Orders.Tests.Infrastructure;

/// <summary>
/// PHASE-2 DEBT: suplanta a Catalog.API sobre HTTP de verdad.
///
/// Existe solo mientras exista la llamada síncrona de 2.3. Cuando 3.3 la
/// sustituya por el evento <c>OrderCreated</c>, esta clase, el paquete
/// WireMock.Net y los tests que la usan se borran juntos en 3.7 — ese diff es la
/// documentación del cambio de arquitectura.
///
/// *Descartado* un <c>HttpMessageHandler</c> falso inyectado en el
/// <c>AddHttpClient&lt;CatalogClient&gt;</c>. Habría salido gratis y en
/// milisegundos, pero cortocircuita justo lo que 2.4 quiere ejercitar: los tres
/// caminos de fallo de CatalogClient nacen de la pila HTTP real —un socket que
/// rechaza la conexión, un <c>Timeout</c> del HttpClient, un cuerpo que no
/// deserializa— y con un handler falso se estarían simulando las excepciones en
/// vez de provocándolas. Además el roadmap nombra WireMock.Net explícitamente.
/// </summary>
public sealed class CatalogStub : IDisposable
{
    private readonly WireMockServer server = WireMockServer.Start();

    /// <summary>
    /// La URL que se le da a Orders.API, con el literal <c>127.0.0.1</c> en vez
    /// del <c>localhost</c> que devuelve <c>server.Url</c>.
    ///
    /// No es cosmético. Medido en 2.3 y anotado en CLAUDE.md: <c>localhost</c>
    /// resuelve a <c>::1</c> **y** a <c>127.0.0.1</c>, así que una conexión
    /// rechazada se intenta dos veces y tarda ~4,13 s en darse por vencida. Con
    /// el <c>Timeout</c> de 5 s del HttpClient eso deja 0,9 s de margen entre
    /// "Catalog no escucha" y "Catalog no contesta a tiempo" — dos ramas
    /// distintas de CatalogClient que estos tests tienen que poder distinguir.
    /// Con el literal IPv4 el rechazo es inmediato y no hay ambigüedad.
    /// </summary>
    public string Url => $"http://127.0.0.1:{server.Ports[0]}";

    /// <summary>Respuesta 200 con los cuatro campos que <c>CatalogProduct</c> declara <c>required</c>.</summary>
    public void StubProduct(int productId, string sku, string name, decimal price) =>
        StubRawBody(productId, $$"""
            {
              "id": {{productId}},
              "sku": "{{sku}}",
              "name": "{{name}}",
              "description": "Producto servido por CatalogStub.",
              "price": {{price.ToString(CultureInfo.InvariantCulture)}},
              "stock": 42,
              "categoryId": 1,
              "categoryName": "Tazas",
              "imageUrl": "/img/products/stub.jpg"
            }
            """);

    /// <summary>
    /// Cuerpo 200 tal cual, para el camino en el que Catalog contesta algo que no
    /// se puede interpretar.
    /// </summary>
    public void StubRawBody(int productId, string json) =>
        server
            .Given(Request.Create().WithPath(PathFor(productId)).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(json));

    /// <summary>El producto no existe. CatalogClient lo traduce a <c>null</c>, no a una excepción.</summary>
    public void StubProductNotFound(int productId) =>
        server
            .Given(Request.Create().WithPath(PathFor(productId)).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

    /// <summary>Catalog contesta, pero mal. Rama <c>!IsSuccessStatusCode</c>.</summary>
    public void StubProductError(int productId, int statusCode) =>
        server
            .Given(Request.Create().WithPath(PathFor(productId)).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(statusCode));

    /// <summary>
    /// Catalog tarda más de lo que el HttpClient está dispuesto a esperar. Es la
    /// única forma de ejercitar la rama del <c>TaskCanceledException</c> de
    /// CatalogClient, que hasta 2.4 no había ejecutado nadie.
    /// </summary>
    public void StubProductSlow(int productId, TimeSpan delay, string sku, string name, decimal price) =>
        server
            .Given(Request.Create().WithPath(PathFor(productId)).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithDelay(delay)
                .WithBody($$"""
                    {"id":{{productId}},"sku":"{{sku}}","name":"{{name}}","price":{{price.ToString(CultureInfo.InvariantCulture)}}}
                    """));

    /// <summary>
    /// Cuántas veces se ha pedido este producto. Es lo que convierte en
    /// verificable la agrupación de líneas repetidas del controller: dos entradas
    /// del mismo producto tienen que costar **una** petición, no dos.
    /// </summary>
    public int RequestCountFor(int productId) =>
        // RequestMessage es anulable en la interfaz de WireMock, de ahí el `?.`:
        // el repositorio compila con 0 warnings y CS8602 no se silencia.
        server.LogEntries.Count(entry => entry.RequestMessage?.Path == PathFor(productId));

    /// <summary>Peticiones totales recibidas, para afirmar que a veces son cero.</summary>
    public int TotalRequests => server.LogEntries.Count();

    /// <summary>
    /// Cierra el puerto sin perder la <see cref="Url"/>: es la simulación de
    /// "Catalog.API está caído". Arrancar y parar es más fiel que inventarse un
    /// puerto libre — este se sabe cerrado porque acaba de cerrarse.
    /// </summary>
    public void Stop() => server.Stop();

    public void Dispose() => server.Dispose();

    private static string PathFor(int productId) => $"/products/{productId}";
}
