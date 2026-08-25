using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Orders.Infrastructure.Catalog;

/// <summary>
/// PHASE-2 DEBT: replaced by OrderCreated event in Phase 3.
///
/// La llamada síncrona Orders → Catalog. Es la regla 2 de CLAUDE.md en su fase
/// de "deuda deliberada": el acoplamiento tiene que **doler** para que la Fase 3
/// resuelva un problema real y no uno hipotético. En 3.3, Orders publicará
/// <c>OrderCreated</c> y esta carpeta entera se borra.
///
/// ── Por qué vive en Orders.Infrastructure y no en Orders.API ──
///
/// CLAUDE.md pone en <c>.Infrastructure</c> todo lo que no es traducir HTTP de
/// entrada. Hablar con un servicio externo es acceso a datos, igual que el
/// <c>DbContext</c>: el controller no debería saber si el precio viene de una
/// tabla, de una llamada HTTP o de un evento — que es exactamente lo que va a
/// cambiar en la Fase 3.
///
/// *Descartado* ponerlo junto al controller que lo usa, siguiendo el precedente
/// de 1.3 (Catalog inyecta su DbContext directo en el controller). Ahí la
/// excepción se justificaba porque una capa más sería un passthrough sobre un
/// CRUD; aquí sí hay lógica propia — traducir tres desenlaces HTTP a tres
/// desenlaces de dominio — y además interesa que la deuda esté aislada en una
/// carpeta que la Fase 3 pueda borrar de una pieza.
///
/// ── Typed client, no HttpClient a pelo ──
///
/// Se registra con <c>AddHttpClient&lt;CatalogClient&gt;</c> en Program.cs, que
/// inyecta un <c>HttpClient</c> gestionado por <c>IHttpClientFactory</c>. Eso
/// evita los dos errores clásicos: instanciar <c>new HttpClient()</c> por
/// petición (agota los sockets, quedan en TIME_WAIT) y guardarlo en un
/// <c>static</c> eterno (no se entera de un cambio de DNS).
///
/// *No hay reintentos ni circuit breaker*, y es a propósito: Polly aquí
/// amortiguaría justo el dolor que el punto quiere enseñar. Entra en 6.6, del
/// lado del Frontend, donde el problema que resuelve sí es el suyo.
///
/// **Sin paquete NuGet**: <c>HttpClient</c> y <c>System.Net.Http.Json</c> vienen
/// en el shared framework, y <c>AddHttpClient</c> en Microsoft.Extensions.Http,
/// que arrastra el SDK Web de Orders.API. Este proyecto no cambió su .csproj.
/// </summary>
public sealed class CatalogClient(HttpClient httpClient)
{
    /// <summary>
    /// Pide un producto a Catalog. Tres desenlaces, y distinguirlos **es** el
    /// contenido de este método:
    ///
    /// - <b>200</b> → el producto. Sus campos se congelan en la línea de pedido.
    /// - <b>404</b> → <c>null</c>. El producto no existe; el controller lo
    ///   convierte en un 400 que nombra el campo del cuerpo.
    /// - <b>cualquier otra cosa</b> → <see cref="CatalogUnavailableException"/>,
    ///   que el controller convierte en 502.
    ///
    /// La ruta es **relativa y sin barra inicial** a propósito. Con
    /// <c>BaseAddress</c> puesto, un <c>"/products/1"</c> se interpreta como
    /// absoluto desde la raíz del host y descarta cualquier segmento de ruta que
    /// traiga la base — hoy no se nota porque la base es la raíz, pero rompería
    /// el día que Catalog viva detrás del Gateway en <c>/api/catalog/</c>
    /// (Fase 5).
    /// </summary>
    public async Task<CatalogProduct?> FindProductOrNullAsync(int productId, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        try
        {
            response = await httpClient.GetAsync($"products/{productId}", cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            // Conexión rechazada, DNS que no resuelve, socket cortado a media
            // respuesta. Es el caso "Catalog caído" literal, el que 2.4 tiene que
            // hacer reproducible.
            throw new CatalogUnavailableException(
                $"No se pudo contactar con Catalog.API para consultar el producto {productId}.",
                exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Un timeout de HttpClient llega como TaskCanceledException, igual que
            // una cancelación del llamante. La guarda del filtro es lo que los
            // separa: si el token del request NO está cancelado, nadie pidió
            // parar — se agotó el Timeout configurado en Program.cs.
            //
            // Sin el filtro, cerrar la pestaña del navegador a mitad de un POST
            // se registraría como "Catalog no disponible", que es mentira. Con
            // él, esa cancelación se propaga y ASP.NET Core la trata como lo que
            // es: el cliente se fue.
            throw new CatalogUnavailableException(
                $"Catalog.API no respondió a tiempo al consultar el producto {productId}.",
                exception);
        }

        // El 404 se comprueba ANTES de EnsureSuccessStatusCode: es una respuesta
        // válida y esperada, no un fallo. Catalog lo devuelve desnudo, sin cuerpo
        // (ProductsController.GetById), así que no hay nada que leer.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            // 500 de Catalog, 502 de un proxy, 401 el día que 8.1 ponga
            // autenticación... Todo lo que no sea 200 ni 404 significa que la
            // dependencia no está dando servicio, y Orders no puede inventarse un
            // precio.
            throw new CatalogUnavailableException(
                $"Catalog.API respondió {(int)response.StatusCode} al consultar el producto {productId}.");
        }

        try
        {
            // ReadFromJsonAsync usa JsonSerializerDefaults.Web: insensible a
            // mayúsculas, así que el "sku" camelCase de Catalog casa con la
            // propiedad Sku sin configurar nada.
            //
            // Los miembros de CatalogProduct son 'required', de modo que un JSON
            // al que le falte un campo lanza JsonException en vez de dejar pasar
            // un precio en cero. Un null aquí solo puede venir del literal "null"
            // en el cuerpo.
            return await response.Content.ReadFromJsonAsync<CatalogProduct>(cancellationToken)
                ?? throw new CatalogUnavailableException(
                    $"Catalog.API devolvió un cuerpo vacío para el producto {productId}.");
        }
        catch (JsonException exception)
        {
            // 200 con un cuerpo que no es el contrato esperado. Se trata como
            // indisponibilidad y no como 500 de Orders: el que incumplió el
            // contrato fue el otro servicio. Es el modo de fallo que la Fase 3
            // hace imposible, porque Shop133.Contracts lo verifica el compilador.
            throw new CatalogUnavailableException(
                $"Catalog.API devolvió una respuesta que no se pudo interpretar para el producto {productId}.",
                exception);
        }
    }
}
