using Microsoft.AspNetCore.Mvc;

using Shop133.Web.Gateway;
using Shop133.Web.Models;
using Shop133.Web.Orders;

namespace Shop133.Web.Controllers;

/// <summary>
/// La pagina de estado del pedido (6.5), y con ella el item <i>Estado del pedido</i> del navbar,
/// que llevaba <c>disabled</c> desde 6.1 esperando a que existiera este controller.
///
/// ── Lo que este punto anade y no es evidente ──
///
/// Hasta 6.4 el frontend creaba pedidos y **se quedaba ciego**: la confirmacion no vuelve a
/// llamar al Gateway ni una vez, asi que quien compraba solo se enteraba del desenlace por el
/// correo de Notifications (4.6). Esta pagina cierra eso, y de paso es lo primero del proyecto
/// que le ensena a una persona que detras hay una saga.
///
/// ── El sondeo NO pasa por aqui ──
///
/// <see cref="Status"/> hace **una** llamada al Gateway, la del primer render. A partir de ahi
/// quien pregunta es el <c>fetch</c> de <c>wwwroot/js/order-status.js</c>, directo del navegador
/// al Gateway.
///
/// *Descartado* sondear contra una accion de este proyecto que reenviara al Gateway, que habria
/// evitado CORS y el contenido mixto. El motivo es el cupo: <c>Shop133.Web</c> renderiza en
/// servidor, asi que para el rate limiter de 5.2 **todos los visitantes son una sola IP** —6.2 lo
/// midio, el primer 429 llega en el render 30—. Con el sondeo en el navegador, cada visitante
/// gasta su propio cupo. Ademas es lo que 5.3 dejo escrito que CORS existia para servir: aquel
/// punto dice literalmente que quien lo necesita de verdad es "el JavaScript de 6.5 (el polling
/// del estado del pedido)", y hasta hoy no tenia ni un consumidor.
/// </summary>
public sealed class OrdersController(
    OrdersClient ordersClient,
    RecentOrdersStore recentOrders,
    IConfiguration configuration,
    ILogger<OrdersController> logger)
    : Controller
{
    /// <summary>
    /// Los pedidos tramitados desde esta sesion. **No llama al Gateway ni una vez**, igual que
    /// <c>/cart</c>: lo que pinta ya esta en la sesion.
    ///
    /// Esa ausencia de llamada es deliberada y tiene consecuencia visible: la lista **no** dice en
    /// que estado esta cada pedido. Decirlo costaria una peticion por fila, contra el cupo de
    /// lectura y en una pagina que no es la que sirve para seguirlos. Quien quiera el estado entra
    /// en el pedido.
    ///
    /// Tiene que llamarse <c>Index</c>: el boton *Reintentar* de
    /// <c>Views/Shared/Unavailable.cshtml</c> es <c>asp-action="Index"</c> sin
    /// <c>asp-controller</c>, asi que resuelve contra el controller actual. Es la misma razon por
    /// la que la accion del checkout se llama <c>Index</c> y no <c>Checkout</c>. Y aqui sale
    /// especialmente bien: como esta accion no habla con el Gateway, ese reintento funciona
    /// incluso con el Gateway caido — la misma propiedad que 6.3 anoto para <c>/cart</c>.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken) =>
        View(await recentOrders.GetAsync(cancellationToken));

    /// <summary>
    /// El seguimiento de un pedido: <c>/orders/status/{id}</c>.
    ///
    /// **Se renderiza en el servidor y despues se actualiza sola.** Las dos mitades no son
    /// redundantes: sin el render, la pagina aparece vacia hasta la primera vuelta del sondeo y no
    /// ensena nada con JavaScript desactivado; sin el sondeo, hay que recargar a mano para ver
    /// avanzar una saga que dura cientos de milisegundos.
    ///
    /// Un pedido que no existe devuelve **404** con vista propia, precedente literal de
    /// <c>CatalogController.Details</c>. No es lo mismo que "el Gateway no contesta" y no puede
    /// pintarse igual: el sistema esta perfectamente vivo y lo que falla es el identificador.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Status(Guid id, CancellationToken cancellationToken)
    {
        OrderStatus? status;

        try
        {
            status = await ordersClient.GetStatusAsync(id, cancellationToken);
        }
        catch (GatewayUnavailableException exception)
        {
            return this.GatewayUnavailable(exception, logger, "No se pudo leer el estado del pedido.");
        }

        if (status is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;

            return View("NotFound", id);
        }

        // El total sale de la sesion, no de una segunda llamada: GET /orders/{id}/status es ligero
        // a proposito y no lo trae. Es null para un pedido que esta sesion no tramito —un enlace
        // compartido, o despues de reiniciar el proceso— y la vista simplemente no lo pinta.
        var remembered = await recentOrders.GetAsync(cancellationToken);

        return View(new OrderStatusViewModel
        {
            Status = status,
            GatewayBaseUrl = BrowserGatewayBaseUrl(),
            FormattedTotal = remembered.FirstOrDefault(order => order.Id == id)?.FormattedTotal,
        });
    }

    /// <summary>
    /// La direccion del Gateway **para el navegador**, que no tiene por que ser la que usa este
    /// servidor.
    ///
    /// <c>Gateway:BaseUrl</c> es <c>http://127.0.0.1:5104</c> y esta elegida para llamadas
    /// servidor-a-servidor: 2.3 midio que rechazar una conexion en <c>localhost</c> cuesta 4,13 s
    /// —resuelve a <c>::1</c> Y a <c>127.0.0.1</c>— frente a 2,03 s en el literal. Al navegador
    /// esa optimizacion no le sirve de nada, y ademas hay un caso en el que la hereda rota: con el
    /// frontend servido por <c>https</c>, un <c>fetch</c> a una URL <c>http</c> es **contenido
    /// mixto** y el navegador lo bloquea sin mas rastro que una linea en su consola.
    ///
    /// De ahi <c>Gateway:PublicBaseUrl</c>. Es **opcional** y cae en la otra si falta, y eso no es
    /// el <c>?? ""</c> defensivo que 6.2 rechazo: aquello enmascaraba una clave ausente y dejaba
    /// todas las paginas acusando a un proceso vivo. Esto es un valor con significado —"si no se
    /// dice otra cosa, el navegador llega igual que el servidor"— y es cierto en el unico
    /// despliegue que hoy existe.
    /// </summary>
    private string BrowserGatewayBaseUrl() =>
        (configuration["Gateway:PublicBaseUrl"] ?? configuration["Gateway:BaseUrl"]!).TrimEnd('/');
}
