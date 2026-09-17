using Microsoft.AspNetCore.Mvc;

using Shop133.Web.Cart;
using Shop133.Web.Gateway;

namespace Shop133.Web.Controllers;

/// <summary>
/// El carrito: verlo, anadir, cambiar cantidades, quitar y vaciar.
///
/// **<c>[AutoValidateAntiforgeryToken]</c> va en la CLASE y no un
/// <c>[ValidateAntiForgeryToken]</c> por accion**, y no es un ahorro de teclas: el atributo por
/// accion es una lista que hay que acordarse de ampliar, y una accion nueva que se olvide de el
/// queda desprotegida sin un solo aviso. El automatico valida TODO lo que no sea GET/HEAD/
/// OPTIONS/TRACE, asi que la proteccion es la opcion por defecto y desactivarla es lo que exige
/// escribir algo. Es el mismo criterio con el que 5.2 anadio un <c>GlobalLimiter</c> que el
/// titulo no pedia: una red de seguridad contra un fallo SILENCIOSO.
///
/// **Todas las mutaciones son POST y todas acaban en un redirect** (POST-Redirect-GET). Un GET que
/// mutara seria cacheable y precargable: un navegador que precarga enlaces vaciaria el carrito
/// solo. Y sin el redirect, un F5 sobre la respuesta de "anadir" volveria a anadir.
/// </summary>
[AutoValidateAntiforgeryToken]
public sealed class CartController(
    CartStore cartStore,
    CatalogClient catalogClient,
    ILogger<CartController> logger)
    : Controller
{
    /// <summary>
    /// La pagina del carrito. **No llama al Gateway ni una vez** — decision 3 de 6.3: el carrito es
    /// una foto, y releer los precios linea a linea costaria una peticion por linea y por render
    /// contra el cupo <c>catalog-read</c> de 60/60 s de 5.2, ademas de cambiar el precio bajo los
    /// pies de quien esta comprando.
    /// </summary>
    public async Task<IActionResult> Index(CancellationToken cancellationToken) =>
        View(await cartStore.GetAsync(cancellationToken));

    /// <summary>
    /// Anade un producto al carrito. **Es la unica accion de este controller que sale al Gateway, y
    /// ahi esta el punto entero de 6.3.**
    ///
    /// El formulario manda SOLO <paramref name="productId"/> y <paramref name="quantity"/>. El sku,
    /// el nombre y el precio NO vienen del navegador: se leen de Catalog por el Gateway y se
    /// congelan aqui. Si el formulario mandara el precio, el cliente volveria a dictar el importe
    /// del pedido —que es lo que 3.3 abrio— y guardar el carrito en sesion no serviria de nada.
    ///
    /// Un producto que Catalog no conoce **no es un 503**: es un aviso y vuelta al catalogo. Es el
    /// mismo criterio con el que <c>CatalogClient.FindProductOrNullAsync</c> separa el 404 —una
    /// respuesta valida y esperada de un catalogo que borra fisicamente— de un fallo de la
    /// dependencia.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Add(int productId, int quantity, CancellationToken cancellationToken)
    {
        CatalogProduct? product;

        try
        {
            product = await catalogClient.FindProductOrNullAsync(productId, cancellationToken);
        }
        catch (GatewayUnavailableException exception)
        {
            return Unavailable(exception);
        }

        if (product is null)
        {
            TempData[CartNotice.ErrorKey] = $"El producto {productId} ya no está en el catálogo.";
            return RedirectToAction(nameof(CatalogController.Index), "Catalog");
        }

        var cart = await cartStore.GetAsync(cancellationToken);

        // La foto se acuna AQUI, con lo que acaba de contestar Catalog. Los cinco campos son los
        // cinco de OrderLine, y 6.4 los copiara tal cual al cuerpo de POST /orders.
        var rejection = cart.Add(new CartLine
        {
            ProductId = product.Id,
            ProductSku = product.Sku,
            ProductName = product.Name,
            UnitPrice = product.Price,
            Quantity = quantity,
            ImageUrl = product.ImageUrl,
        });

        if (rejection is not null)
        {
            TempData[CartNotice.ErrorKey] = rejection;
            return RedirectToAction(nameof(CatalogController.Details), "Catalog", new { id = productId });
        }

        await cartStore.SaveAsync(cart, cancellationToken);

        TempData[CartNotice.MessageKey] = quantity == 1
            ? $"Se añadió «{product.Name}» al carrito."
            : $"Se añadieron {quantity} unidades de «{product.Name}» al carrito.";

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Fija la cantidad de una linea. Una cantidad de 0 quita la linea — lo decide
    /// <see cref="ShoppingCart.SetQuantity"/>, no esta accion.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> SetQuantity(int productId, int quantity, CancellationToken cancellationToken)
    {
        var cart = await cartStore.GetAsync(cancellationToken);
        var rejection = cart.SetQuantity(productId, quantity);

        if (rejection is not null)
        {
            TempData[CartNotice.ErrorKey] = rejection;
            return RedirectToAction(nameof(Index));
        }

        await cartStore.SaveAsync(cart, cancellationToken);

        TempData[CartNotice.MessageKey] = "Se actualizó el carrito.";

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Remove(int productId, CancellationToken cancellationToken)
    {
        var cart = await cartStore.GetAsync(cancellationToken);
        var removed = cart.Remove(productId);

        if (removed is null)
        {
            TempData[CartNotice.ErrorKey] = "Ese producto ya no estaba en el carrito.";
            return RedirectToAction(nameof(Index));
        }

        await cartStore.SaveAsync(cart, cancellationToken);

        TempData[CartNotice.MessageKey] = $"Se quitó «{removed.ProductName}» del carrito.";

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Clear(CancellationToken cancellationToken)
    {
        var cart = await cartStore.GetAsync(cancellationToken);
        cart.Clear();

        // Un carrito vacio se BORRA de la sesion en lugar de guardarse vacio; lo decide CartStore.
        await cartStore.SaveAsync(cart, cancellationToken);

        TempData[CartNotice.MessageKey] = "Se vació el carrito.";

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// El aviso de que el Gateway no contesta. Es una **copia casi literal** del
    /// <c>Unavailable(...)</c> privado de <c>CatalogController</c>, y se queda copiada: son dos
    /// ocurrencias, y en este proyecto *dos copias no son un patron* (precedente de 2.4 con
    /// <c>SqlServerContainerFixture</c>, que espero a tener cuatro antes de extraerse). Lo que si
    /// se comparte ya es la VISTA, que 6.3 movio a <c>Views/Shared/</c> precisamente por esto.
    ///
    /// El codigo de estado se conserva porque es lo unico que hace la rama comprobable desde la
    /// linea de comandos — decision 3 de 6.2, que no se revierte.
    /// </summary>
    private IActionResult Unavailable(GatewayUnavailableException exception)
    {
        logger.LogWarning(exception, "No se pudo añadir al carrito.");

        Response.StatusCode = exception.IsRateLimited
            ? StatusCodes.Status429TooManyRequests
            : StatusCodes.Status503ServiceUnavailable;

        return View("Unavailable", exception);
    }
}
