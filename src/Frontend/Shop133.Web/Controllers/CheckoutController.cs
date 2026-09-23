using Microsoft.AspNetCore.Mvc;

using Shop133.Web.Cart;
using Shop133.Web.Gateway;
using Shop133.Web.Models;
using Shop133.Web.Orders;

namespace Shop133.Web.Controllers;

/// <summary>
/// El checkout: el formulario, el alta del pedido y la confirmacion.
///
/// **Aqui sale el primer POST de este proyecto hacia el Gateway**, y con el la primera vez que la
/// saga entera —cinco servicios, doce mensajes— arranca desde un formulario de navegador en vez
/// de desde un <c>curl</c>. Todo lo que <c>Shop133.Web</c> hacia hasta 6.3 eran lecturas.
///
/// <c>[AutoValidateAntiforgeryToken]</c> en la CLASE, precedente literal de
/// <c>CartController</c>: el atributo por accion es una lista que hay que acordarse de ampliar, y
/// una accion nueva que se olvide queda desprotegida sin un solo aviso.
///
/// **La accion del formulario se llama <c>Index</c> y no <c>Checkout</c>** por un motivo concreto:
/// el boton *Reintentar* de <c>Views/Shared/Unavailable.cshtml</c> es
/// <c>asp-action="Index"</c> SIN <c>asp-controller</c>, asi que resuelve contra el controller
/// actual. Con cualquier otro nombre, caerse el Gateway aqui dejaria al usuario con un boton que
/// da 404.
/// </summary>
[AutoValidateAntiforgeryToken]
public sealed class CheckoutController(
    CartStore cartStore,
    OrdersClient ordersClient,
    RecentOrdersStore recentOrders,
    ILogger<CheckoutController> logger)
    : Controller
{
    /// <summary>
    /// Las dos claves de <c>TempData</c> que cruzan el redirect del POST-Redirect-GET.
    ///
    /// Constantes privadas y no un tipo propio al estilo de <see cref="CartNotice"/>: aquel existe
    /// porque quien escribe (un controller) y quien pinta (<c>_Layout.cshtml</c>) estan en
    /// archivos distintos, de modo que una errata daria un aviso que simplemente no aparece. Aqui
    /// las escribe y las lee la MISMA clase, y el compilador vigila la errata.
    ///
    /// **Lo que viaja en esa cookie**: el correo que el usuario acaba de teclear y un total ya
    /// formateado. Ninguno de los dos es una autoridad que nadie pueda reenviar —el pedido ya
    /// existe y su importe ya lo congelo Orders—, y la foto de precios que 6.3 protegio salio de
    /// la sesion al vaciarse el carrito, un paso antes de esto.
    /// </summary>
    private const string PlacedEmailKey = "PlacedOrderEmail";

    private const string PlacedTotalKey = "PlacedOrderTotal";

    /// <summary>
    /// El formulario. **No llama al Gateway**, igual que <c>/cart</c>: todo lo que pinta ya esta
    /// en la sesion.
    ///
    /// Un carrito vacio devuelve al carrito con su aviso en vez de ensenar un formulario que no
    /// puede tramitar nada. Es alcanzable sin hacer nada raro: basta con vaciar el carrito en otra
    /// pestana, o volver atras despues de tramitar.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var cart = await cartStore.GetAsync(cancellationToken);

        if (cart.IsEmpty)
        {
            return RedirectToCartBecauseEmpty();
        }

        return View(WithCartSummary(new CheckoutViewModel(), cart));
    }

    /// <summary>
    /// Tramita el pedido.
    ///
    /// El orden de los pasos es el entregable, no una secuencia cualquiera:
    ///
    /// 1. **Releer el carrito de la SESION**, nunca del cuerpo del formulario. El navegador manda
    ///    un correo y un token antiforgery; el sku, el nombre, el precio y la cantidad de cada
    ///    linea salen de donde los congelo <c>CartController.Add</c>. Es 6.3 cobrandose: si esto
    ///    leyera las lineas del <c>&lt;form&gt;</c>, el cliente volveria a dictar el importe del
    ///    pedido y guardar el carrito en el servidor no habria servido de nada.
    /// 2. Validar. El <c>ModelState</c> repite lo que el navegador ya comprobo, porque la
    ///    validacion de cliente es comodidad y no una defensa.
    /// 3. Mandar el pedido.
    /// 4. **Vaciar el carrito SOLO despues del 201.** Si el POST falla —Gateway caido, cupo
    ///    agotado, 400—, el carrito tiene que seguir intacto para poder reintentar. Vaciarlo antes
    ///    seria perder la compra por un fallo de red.
    /// 5. Redirect. POST-Redirect-GET, como las cuatro mutaciones de <c>CartController</c>: sin el,
    ///    un F5 sobre la respuesta volveria a tramitar.
    ///
    /// **El carrito se vacia aunque el pedido acabe cancelandose** (precio caducado, sin stock,
    /// pago rechazado). Es deliberado: el pedido EXISTE y su desenlace es asincrono, asi que
    /// retener el carrito "por si acaso" dejaria al usuario con dos copias de la misma compra.
    /// Enterarse del desenlace es el correo de Notifications (4.6) y la pagina de 6.5.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Index(CheckoutViewModel form, CancellationToken cancellationToken)
    {
        var cart = await cartStore.GetAsync(cancellationToken);

        if (cart.IsEmpty)
        {
            return RedirectToCartBecauseEmpty();
        }

        if (!ModelState.IsValid)
        {
            return View(WithCartSummary(form, cart));
        }

        PlacedOrder placed;

        try
        {
            placed = await ordersClient.CreateOrderAsync(ToNewOrder(form, cart), cancellationToken);
        }
        catch (GatewayUnavailableException exception)
        {
            return Unavailable(exception);
        }
        catch (OrderRejectedException exception)
        {
            // Clave vacia a proposito: son errores DEL PEDIDO, no de un campo del formulario —el
            // unico campo es el correo, y si fuera el, el ModelState ya habria cortado antes—. Con
            // clave vacia caen en el asp-validation-summary="ModelOnly" de la vista, que es el
            // sitio donde se leen. Ver OrderRejectedException para por que esto no es una caida.
            foreach (var error in exception.Errors)
            {
                ModelState.AddModelError(string.Empty, error);
            }

            return View(WithCartSummary(form, cart));
        }

        // ANTES del Clear, o siempre diria cero lineas.
        logger.LogInformation(
            "Pedido {OrderId} tramitado desde el carrito con {LineCount} linea(s) por {Total}.",
            placed.Id,
            cart.Lines.Count,
            placed.Total);

        cart.Clear();

        // Un carrito vacio se BORRA de la sesion en lugar de guardarse vacio; lo decide CartStore.
        await cartStore.SaveAsync(cart, cancellationToken);

        // 6.5 — el pedido queda apuntado en la sesion para que se pueda volver a el desde el
        // navbar. Va DESPUES del 201, como el Clear y por el mismo motivo: si el POST hubiera
        // fallado no habria pedido que recordar.
        //
        // Es lo que hace que la pagina de estado sea alcanzable sin copiar un Guid a mano — el
        // TempData de abajo solo dura una peticion, asi que un F5 sobre la confirmacion ya pierde
        // el correo y el total.
        await recentOrders.RememberAsync(
            new RecentOrder
            {
                Id = placed.Id,
                PlacedAt = DateTimeOffset.UtcNow,
                FormattedTotal = Money.Format(placed.Total),
            },
            cancellationToken);

        TempData[PlacedEmailKey] = placed.CustomerEmail;

        // Formateado AQUI y no en la vista: TempData no sabe serializar un decimal y revienta en
        // tiempo de ejecucion. Ver PlacedOrderViewModel.FormattedTotal.
        TempData[PlacedTotalKey] = Money.Format(placed.Total);

        // El id sale del CUERPO del 201, no de la cabecera Location — que sigue apuntando al
        // backend sin el prefijo del Gateway (deuda de 5.1, sin dueno). Ver PlacedOrder.Id.
        return RedirectToAction(nameof(Placed), new { id = placed.Id });
    }

    /// <summary>
    /// La confirmacion. **No llama al Gateway ni una vez** — ver
    /// <see cref="PlacedOrderViewModel"/>: releer el pedido gastaria un permiso del cupo
    /// <c>orders-write</c> por cada vista de una pagina que solo podria decir <c>Pending</c>, y es
    /// el entregable de 6.5.
    ///
    /// El id viene de la RUTA y el detalle de <c>TempData</c>, que dura una peticion: al recargar
    /// queda el numero de pedido, que es lo que hace falta para reclamar.
    /// </summary>
    [HttpGet]
    public IActionResult Placed(Guid id) => View(new PlacedOrderViewModel
    {
        OrderId = id,
        CustomerEmail = TempData[PlacedEmailKey] as string,
        FormattedTotal = TempData[PlacedTotalKey] as string,
    });

    /// <summary>
    /// El carrito traducido al cuerpo del pedido: las cinco primeras propiedades de cada
    /// <see cref="CartLine"/>, copiadas tal cual. Es la promesa que aquel tipo lleva escrita desde
    /// 6.3, cumplida en una expresion.
    ///
    /// <c>ImageUrl</c> se queda fuera porque no forma parte de la foto — un pedido no guarda la
    /// miniatura del catalogo.
    ///
    /// No hace falta agrupar por <c>ProductId</c>: <see cref="ShoppingCart.Add"/> suma las
    /// cantidades al anadir, asi que el carrito no puede tener dos lineas del mismo producto. Esa
    /// invariante es la misma que el constructor de <c>Order</c> exige desde 2.1, y es el motivo
    /// de que aquel metodo sume en vez de anadir una segunda linea.
    /// </summary>
    private static NewOrder ToNewOrder(CheckoutViewModel form, ShoppingCart cart) => new()
    {
        // El ! es seguro: solo se llega aqui con el ModelState valido, y [Required] ya lo miro.
        CustomerEmail = form.CustomerEmail!,
        Items = [.. cart.Lines.Select(line => new NewOrderLine
        {
            ProductId = line.ProductId,
            ProductSku = line.ProductSku,
            ProductName = line.ProductName,
            UnitPrice = line.UnitPrice,
            Quantity = line.Quantity,
        })],
    };

    /// <summary>
    /// Rellena el resumen desde el carrito.
    ///
    /// **Hay que llamarlo en TODOS los caminos que devuelven la vista**, el de error incluido:
    /// esas tres propiedades llevan <c>[BindNever]</c>, asi que en un POST llegan vacias por
    /// diseno. Olvidarlo pinta un resumen en blanco y parece que el carrito se vacio solo.
    /// </summary>
    private static CheckoutViewModel WithCartSummary(CheckoutViewModel form, ShoppingCart cart)
    {
        form.Lines = cart.Lines;
        form.Total = cart.Total;
        form.UnitCount = cart.UnitCount;

        return form;
    }

    private IActionResult RedirectToCartBecauseEmpty()
    {
        TempData[CartNotice.ErrorKey] = "El carrito está vacío, así que no hay nada que tramitar.";

        return RedirectToAction(nameof(CartController.Index), "Cart");
    }

    /// <summary>
    /// El aviso de que el Gateway no contesta. Era la **tercera copia** del helper privado de
    /// <c>CatalogController</c> y <c>CartController</c>, y su comentario decia <i>"la cuarta
    /// decide"</i>, siguiendo el criterio que 2.4 fijo y 3.7 aplico con
    /// <c>SqlServerContainerFixture</c>.
    ///
    /// **6.5 trajo la cuarta y decidio: el cuerpo vive en <see cref="GatewayFailureExtensions"/>.**
    /// Aqui solo queda el texto del log, que resulto ser lo unico que las tres copias no
    /// compartian — que es exactamente lo que aquel precedente pedia comprobar antes de extraer.
    /// </summary>
    private IActionResult Unavailable(GatewayUnavailableException exception) =>
        this.GatewayUnavailable(exception, logger, "No se pudo tramitar el pedido.");
}
