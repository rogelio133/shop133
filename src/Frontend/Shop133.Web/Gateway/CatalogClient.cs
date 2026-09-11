namespace Shop133.Web.Gateway;

/// <summary>
/// El unico camino de salida de este proyecto, y la regla 3 de CLAUDE.md hecha codigo: habla
/// con el Gateway y con nadie mas. En ninguna parte de <c>Shop133.Web</c> aparece el puerto de
/// un servicio — ni 5124 (Catalog) ni 5189 (Orders) —, solo <c>Gateway:BaseUrl</c>.
///
/// Que este cliente conozca el prefijo publico <c>catalog/</c> no rompe esa regla: ese prefijo
/// es el contrato PUBLICO del Gateway (5.1), no la direccion de un servicio.
///
/// Cliente TIPADO, registrado con <c>AddHttpClient&lt;CatalogClient&gt;</c>. Se descarto un
/// <c>HttpClient</c> nuevo por peticion (agota los sockets, que quedan en TIME_WAIT) y uno
/// <c>static</c> (no se entera nunca de un cambio de DNS); un cliente CON NOMBRE devolveria la
/// URL base al controller, que es justo el acoplamiento que el tipado quita de en medio.
///
/// No hay reintentos, ni circuit breaker, ni cache — a proposito. Eso es 6.6, y el
/// <c>IHttpClientBuilder</c> que devuelve el registro es el punto exacto donde se engancha.
/// </summary>
public sealed class CatalogClient(HttpClient httpClient, ILogger<CatalogClient> logger)
{
    /// <summary>
    /// Cuantos productos pide por pagina. Es una decision de PRESENTACION —12 son tres filas
    /// exactas en la rejilla de cuatro columnas— y por eso vive aqui y no en la API, que tiene su
    /// propio valor por defecto de 12 y un tope de 100. Que NO coincidan es la prueba de que son
    /// dos decisiones de dos duenos distintos: este numero cambia con la maquetacion y la API no
    /// se entera.
    /// </summary>
    public const int PageSize = 12;

    /// <summary>
    /// Una pagina del catalogo, opcionalmente filtrada por categoria (6.2.1).
    ///
    /// <paramref name="page"/> se RECORTA a 1 como minimo, y eso no es paranoia: la API devuelve
    /// 400 a un <c>page=0</c> —es un error del cliente y se lo dice—, pero un 400 aqui lo
    /// convertiria <see cref="ReadAsync"/> en <see cref="GatewayUnavailableException"/> y la
    /// pagina diria "el Gateway no responde" senalando a un proceso perfectamente vivo. Un
    /// <c>?page=0</c> escrito a mano en la barra del navegador es alcanzable, y merece la primera
    /// pagina, no un diagnostico seguro de si mismo y equivocado.
    /// </summary>
    public Task<CatalogPage<CatalogProduct>> GetProductsAsync(
        int? categoryId,
        int page,
        CancellationToken cancellationToken)
    {
        var path = $"catalog/products?page={Math.Max(page, 1)}&pageSize={PageSize}";

        if (categoryId is int id)
        {
            path += $"&categoryId={id}";
        }

        return GetAsync<CatalogPage<CatalogProduct>>(path, cancellationToken)!;
    }

    public Task<IReadOnlyList<CatalogCategory>> GetCategoriesAsync(CancellationToken cancellationToken) =>
        GetAsync<IReadOnlyList<CatalogCategory>>("catalog/categories", cancellationToken)!;

    /// <summary>
    /// Devuelve <c>null</c> cuando el producto no existe. El 404 se comprueba ANTES que
    /// <c>IsSuccessStatusCode</c> y a proposito: es una respuesta valida y esperada del
    /// catalogo, no un fallo — precedente literal de 2.3. Confundirlos haria que un producto
    /// borrado se le contara al usuario como "el Gateway esta caido".
    /// </summary>
    public async Task<CatalogProduct?> FindProductOrNullAsync(int productId, CancellationToken cancellationToken)
    {
        var response = await SendAsync($"catalog/products/{productId}", cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        return await ReadAsync<CatalogProduct>(response, cancellationToken);
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        var response = await SendAsync(path, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            // La ruta es RELATIVA y SIN barra inicial. Con "/catalog/products", Uri la trata
            // como absoluta desde la raiz y se come el "/api" de la BaseAddress en silencio.
            // En 2.3 esto era hipotetico porque la base era un host pelado; aqui la base TIENE
            // segmento de ruta desde el primer dia, asi que es un 404 esperando a ocurrir.
            return await httpClient.GetAsync(path, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "No se pudo conectar con el Gateway para {Path}.", path);

            throw new GatewayUnavailableException(
                $"No se pudo conectar con el Gateway para '{path}'.", innerException: exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // El filtro es lo que separa un TIMEOUT de un cliente que se fue: los dos llegan
            // como TaskCanceledException. En 2.3 era buena costumbre; aqui es imprescindible por
            // primera vez, porque quien cancela es un humano cerrando una pestana — y sin el
            // filtro, cerrar el navegador se loguearia como una caida del Gateway.
            logger.LogWarning(exception, "El Gateway agoto el tiempo de espera para {Path}.", path);

            throw new GatewayUnavailableException(
                $"El Gateway agoto el tiempo de espera para '{path}'.", innerException: exception);
        }
    }

    private async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            // El 429 se separa del resto porque tiene una causa y un remedio distintos, y
            // porque es alcanzable de verdad: el cupo catalog-read de 5.2 son 60 lecturas por
            // minuto y POR IP, y como este MVC renderiza en servidor, todos los visitantes son
            // una sola IP para el Gateway.
            var retryAfter = response.Headers.RetryAfter?.Delta;

            logger.LogWarning(
                "El Gateway contesto {StatusCode} a {Path}.",
                (int)response.StatusCode, response.RequestMessage?.RequestUri);

            throw new GatewayUnavailableException(
                $"El Gateway contesto {(int)response.StatusCode}.",
                (int)response.StatusCode,
                retryAfter);
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }
}
