using Shop133.Web.Gateway;

namespace Shop133.Web.Models;

/// <summary>
/// Lo que pinta <c>Views/Orders/Status.cshtml</c> en el PRIMER render, el del servidor.
///
/// **A partir de ahi la pagina se actualiza sola y este modelo deja de mandar**: el sondeo de
/// <c>wwwroot/js/order-status.js</c> vuelve a pedir el estado al Gateway cada dos segundos y
/// reescribe los mismos nodos. Este render existe para que la pagina ensene algo de inmediato —sin
/// esperar a la primera vuelta— y para que siga diciendo la verdad con JavaScript desactivado.
///
/// Que las dos mitades pinten lo mismo no lo garantiza nada automatico: el servidor usa
/// <see cref="OrderProgress"/> y el navegador tiene su propia copia del mapa en el <c>.js</c>. Es
/// duplicacion consciente y esta anotada en los dos sitios; la alternativa —que el servidor
/// devolviera HTML ya pintado en cada sondeo— gastaria el mismo cupo para mandar marcado en vez de
/// seis campos, y ademas obligaria a que el sondeo pasara por este proyecto, que es justo lo que
/// haria que todos los visitantes contaran como una sola IP.
/// </summary>
public sealed record OrderStatusViewModel
{
    public required OrderStatus Status { get; init; }

    /// <summary>
    /// La direccion del Gateway **tal y como la ve el NAVEGADOR**, que no tiene por que ser la
    /// misma que usa este servidor. Viaja a la vista para acabar en un <c>data-</c> del HTML.
    ///
    /// No se escribe dentro del <c>.js</c> con Razor a proposito: un archivo estatico con
    /// interpolacion de servidor deja de poder servirse como estatico y de poder cachearse.
    /// </summary>
    public required string GatewayBaseUrl { get; init; }

    /// <summary>El total, si esta sesion recuerda haber tramitado este pedido. Ver Index.</summary>
    public string? FormattedTotal { get; init; }

    public (Track Pricing, Track Fulfilment) Tracks => OrderProgress.Tracks(Status.Stage);

    /// <summary>
    /// Si la pagina tiene que arrancar el sondeo. Un pedido ya resuelto se pinta y se deja quieto:
    /// sondear un desenlace que no va a cambiar es gastar cupo por nada.
    /// </summary>
    public bool ShouldPoll => !Status.IsFinal;
}
