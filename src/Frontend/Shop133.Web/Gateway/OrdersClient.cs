using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc;

namespace Shop133.Web.Gateway;

/// <summary>
/// El alta de pedidos, y **el primer POST que sale de este proyecto**. Hasta 6.4 todo lo que
/// <c>Shop133.Web</c> hacia contra el Gateway eran lecturas.
///
/// Segundo cliente TIPADO, hermano de <see cref="CatalogClient"/> y registrado igual con
/// <c>AddHttpClient&lt;OrdersClient&gt;</c>. *Descartado* meter el POST en aquel: se llama
/// <c>CatalogClient</c> y hablaria con Orders, y el dia que 6.6 le ponga politicas de Polly
/// distintas a cada uno —un reintento automatico de una LECTURA es gratis, el de una ESCRITURA
/// crea pedidos duplicados— haria falta separarlos igualmente.
///
/// Como en aquel: sin reintentos, sin circuit breaker y sin cache. Eso es 6.6.
///
/// Conocer el prefijo publico <c>orders</c> no rompe la regla 3: ese prefijo es el contrato
/// PUBLICO del Gateway (5.1), no la direccion de un servicio. En todo <c>Shop133.Web</c> no
/// aparece el puerto 5189 ni una vez.
/// </summary>
public sealed class OrdersClient(HttpClient httpClient, ILogger<OrdersClient> logger)
{
    /// <summary>
    /// El cupo que este cliente puede agotar. Son DIEZ por minuto, no los 60 de Catalog, y la
    /// asimetria es deliberada en 5.2: el limite protege el COSTE, y cada pedido arranca la saga
    /// entera —cinco servicios, doce mensajes, cinco bases de datos—. Ver
    /// <see cref="GatewayUnavailableException.Quota"/>.
    /// </summary>
    private const string Quota = "la creación de pedidos a 10 por minuto y por IP";

    /// <summary>
    /// Manda el pedido y devuelve lo que contesto el 201.
    ///
    /// **La ruta es relativa y SIN barra inicial**, como en <see cref="CatalogClient"/>: la
    /// <c>BaseAddress</c> acaba en <c>/api/</c>, y con <c>"/orders"</c> el tipo <c>Uri</c> la
    /// trataria como absoluta desde la raiz y se comeria el <c>/api</c> en silencio.
    ///
    /// Tres desenlaces, y separarlos es el trabajo de este metodo:
    /// - <b>201</b> — el pedido existe y su saga esta en marcha.
    /// - <b>400</b> — <see cref="OrderRejectedException"/>. El Gateway y el servicio estan vivos;
    ///   lo que esta mal es el cuerpo. Ver alli por que no puede pintarse como una caida.
    /// - <b>lo demas</b> — <see cref="GatewayUnavailableException"/>, con el 429 ya distinguido
    ///   por <c>IsRateLimited</c>.
    ///
    /// **No se mira la cabecera <c>Location</c>**, que apunta al backend sin prefijo — ver la nota
    /// de <see cref="PlacedOrder.Id"/>. El id sale del cuerpo.
    /// </summary>
    public async Task<PlacedOrder> CreateOrderAsync(NewOrder order, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        try
        {
            response = await httpClient.PostAsJsonAsync("orders", order, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "No se pudo conectar con el Gateway para crear el pedido.");

            throw new GatewayUnavailableException(
                "No se pudo conectar con el Gateway para crear el pedido.", innerException: exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // El filtro separa un TIMEOUT de un humano que cerro la pestana; los dos llegan como
            // TaskCanceledException. Aqui importa mas que en una lectura: sin el, alguien que
            // cierra el navegador mientras tramita quedaria registrado como una caida del Gateway
            // — y ademas su pedido PUEDE haberse creado igual, porque lo que se cancela es la
            // espera de la respuesta, no el trabajo que Orders ya hizo.
            logger.LogWarning(exception, "El Gateway agoto el tiempo de espera al crear el pedido.");

            throw new GatewayUnavailableException(
                "El Gateway agoto el tiempo de espera al crear el pedido.", innerException: exception);
        }

        // ANTES que IsSuccessStatusCode, igual que CatalogClient.FindProductOrNullAsync comprueba
        // el 404 antes: es una respuesta valida y documentada del servicio, no un fallo suyo.
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw await ToRejectionAsync(response, cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta;

            logger.LogWarning(
                "El Gateway contesto {StatusCode} al crear el pedido.", (int)response.StatusCode);

            throw new GatewayUnavailableException(
                $"El Gateway contesto {(int)response.StatusCode} al crear el pedido.",
                (int)response.StatusCode,
                retryAfter,
                Quota);
        }

        // Un 2xx con un cuerpo ilegible es un fallo de la dependencia, no del cuerpo que se mando:
        // el ?? deja que sea indisponibilidad y no un NullReferenceException tres capas mas
        // arriba. No deberia ocurrir nunca; por eso es una linea y no una rama con vista propia.
        return await response.Content.ReadFromJsonAsync<PlacedOrder>(cancellationToken)
            ?? throw new GatewayUnavailableException(
                "El Gateway contesto al alta del pedido con un cuerpo vacio.");
    }

    /// <summary>
    /// Aplana el <c>ValidationProblemDetails</c> del 400 en mensajes sueltos.
    ///
    /// **Se tiran las CLAVES a proposito.** Nombran campos del DTO de Orders
    /// (<c>Items[0].ProductId</c>, <c>CustomerEmail</c>) que este proyecto no declara y que no se
    /// corresponden con ningun campo del formulario salvo uno; ensenarselas a quien compra seria
    /// filtrar la forma interna de otro servicio. Los textos si se ensenan porque estan escritos
    /// para leerse — es el mismo criterio con el que <c>StockRejected.Reason</c> viaja como prosa.
    ///
    /// Si el cuerpo no es un problem+json legible queda el <c>Title</c>, y si tampoco, un mensaje
    /// generico: un 400 sin explicacion sigue siendo un 400, y taparlo con una pagina de caida es
    /// justo lo que <see cref="OrderRejectedException"/> existe para impedir.
    /// </summary>
    private async Task<OrderRejectedException> ToRejectionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        List<string> errors = [];

        try
        {
            var problem = await response.Content
                .ReadFromJsonAsync<ValidationProblemDetails>(cancellationToken);

            if (problem is not null)
            {
                errors.AddRange(problem.Errors.SelectMany(entry => entry.Value));

                if (errors.Count == 0 && !string.IsNullOrWhiteSpace(problem.Title))
                {
                    errors.Add(problem.Title);
                }
            }
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or NotSupportedException)
        {
            // NotSupportedException es la que salta cuando el Content-Type no es JSON — que es
            // exactamente lo que devuelve una capa intermedia que se cruza por medio.
            logger.LogWarning(exception, "El 400 del alta del pedido no traia un problem+json legible.");
        }

        if (errors.Count == 0)
        {
            errors.Add("Orders rechazó el pedido y no dijo por qué.");
        }

        // LogError y no LogWarning: esto no es un usuario equivocandose. El unico dato que el
        // usuario aporta es el correo, ya validado dos veces antes de salir; el resto del cuerpo
        // lo construyo el carrito. Ver OrderRejectedException.
        logger.LogError(
            "Orders rechazo el pedido con 400: {Errors}", string.Join(" | ", errors));

        return new OrderRejectedException("Orders rechazo el cuerpo del pedido.", errors);
    }
}
