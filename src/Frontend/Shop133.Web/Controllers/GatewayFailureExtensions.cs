using Microsoft.AspNetCore.Mvc;

using Shop133.Web.Gateway;

namespace Shop133.Web.Controllers;

/// <summary>
/// El aviso de que el Gateway no contesta, en un solo sitio.
///
/// ── La cuarta copia decidio, como estaba escrito ──
///
/// Este helper era un metodo privado repetido en <c>CatalogController</c> (6.2),
/// <c>CartController</c> (6.3) y <c>CheckoutController</c> (6.4). Aquel ultimo dejo la regla por
/// escrito —<i>"Tercera copia... La cuarta decide"</i>— siguiendo el precedente de
/// <c>SqlServerContainerFixture</c>, que 2.4 dejo duplicada diciendo que "dos copias no son un
/// patron" y 3.7 extrajo al llegar a cuatro con un <c>diff</c> delante. <c>OrdersController</c> de
/// 6.5 es la cuarta, asi que se extrae.
///
/// Y el <c>diff</c> daba lo que aquel precedente pedia comprobar: **las tres copias eran identicas
/// salvo el texto del log**. Nada divergio en tres puntos, que es lo que separa una duplicacion
/// que va a convertirse en tres comportamientos distintos de una que solo es ruido.
///
/// ── Metodo de extension y no una clase base ──
///
/// *Descartada* una <c>GatewayAwareController</c> de la que hereden los cuatro. Obligaria a
/// quitarles el <c>sealed</c> y a reescribir sus constructores primarios para heredar seis lineas,
/// y dejaria a cada controller dependiendo de una clase base por algo que no es su identidad. La
/// extension no toca la forma de nadie.
///
/// ── Lo que NO se movio: el codigo de estado ──
///
/// Sigue poniendose aqui dentro, sobre <c>Response</c>. La decision 3 de 6.2 —que la vista no se
/// devuelva con 200— no se revierte: **el codigo de estado es lo unico que hace estas ramas
/// comprobables desde la linea de comandos**, y con un 200 "Gateway caido" y "todo bien" se ven
/// iguales para un <c>curl</c>.
/// </summary>
public static class GatewayFailureExtensions
{
    /// <summary>
    /// Registra el fallo, pone el codigo (503, o 429 si lo que se agoto fue el cupo) y devuelve la
    /// vista compartida.
    ///
    /// <paramref name="logger"/> se pasa en vez de resolverse aqui para que la linea salga con la
    /// categoria del controller que fallo, que es como se sabe en que pagina estaba el usuario.
    ///
    /// <paramref name="message"/> es lo unico que distinguia a las tres copias: "No se pudo pintar
    /// el catalogo", "No se pudo anadir al carrito", "No se pudo tramitar el pedido".
    /// </summary>
    public static IActionResult GatewayUnavailable(
        this Controller controller,
        GatewayUnavailableException exception,
        ILogger logger,
        string message)
    {
        logger.LogWarning(exception, "{Message}", message);

        controller.Response.StatusCode = exception.IsRateLimited
            ? StatusCodes.Status429TooManyRequests
            : StatusCodes.Status503ServiceUnavailable;

        // La vista vive en Views/Shared/ desde 6.3, asi que la resuelve cualquier controller. Su
        // boton "Reintentar" es asp-action="Index" SIN asp-controller, de modo que vuelve al Index
        // del controller actual — y por eso los cuatro controllers que pueden llegar aqui tienen
        // uno.
        return controller.View("Unavailable", exception);
    }
}
