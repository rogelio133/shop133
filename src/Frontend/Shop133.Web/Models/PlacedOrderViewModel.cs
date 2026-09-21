namespace Shop133.Web.Models;

/// <summary>
/// La confirmacion: lo que se le ensena a quien acaba de tramitar un pedido.
///
/// **Deliberadamente minima, y la ausencia es la decision.** No trae las lineas, ni el estado, ni
/// una barra de progreso de la saga. Eso es 6.5 — el punto entero — y adelantarlo aqui costaria
/// releer el pedido por el Gateway: un permiso del cupo <c>orders-write</c> (DIEZ por minuto, el
/// mismo que gasta el POST) por cada vista de una pagina que a los dos segundos solo puede decir
/// <c>Pending</c>.
///
/// <see cref="OrderId"/> sale de la RUTA y sobrevive a un F5; el correo y el total vienen de
/// <c>TempData</c>, que dura exactamente una peticion, asi que al recargar se pierden. La pagina
/// tiene que seguir siendo correcta sin ellos, y por eso los dos son anulables: el numero de
/// pedido es la parte que importa y es la que no se pierde. Ese es el precio de no llamar al
/// Gateway, dicho en voz alta en lugar de disimulado con una tercera llamada.
///
/// <see cref="FormattedTotal"/> llega YA FORMATEADO y no como <c>decimal</c> por una razon que no
/// es de estilo: el proveedor de <c>TempData</c> por cookie no sabe serializar un <c>decimal</c>
/// —admite <c>string</c>, <c>int</c>, <c>bool</c>, <c>DateTime</c>, <c>Guid</c> y listas de
/// esos— y revienta en tiempo de EJECUCION, no de compilacion. Se formatea con
/// <see cref="Money"/> antes de guardarlo, que ademas es donde vive la cultura.
/// </summary>
public sealed record PlacedOrderViewModel
{
    public required Guid OrderId { get; init; }

    public string? CustomerEmail { get; init; }

    public string? FormattedTotal { get; init; }

    /// <summary>
    /// Si sobrevivio el detalle del <c>TempData</c> o si esto es una recarga. La vista lo usa para
    /// no pintar una ficha a medias con huecos en blanco.
    /// </summary>
    public bool HasDetails => CustomerEmail is not null && FormattedTotal is not null;
}
