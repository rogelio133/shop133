using Microsoft.AspNetCore.Mvc;

using Shop133.Web.Cart;

namespace Shop133.Web.ViewComponents;

/// <summary>
/// El contador de unidades que el navbar pinta junto a "Carrito".
///
/// **Es un view component y no otra cosa, y las dos alternativas se descartaron por el mismo
/// motivo: las dos fallan en silencio.**
///
/// *Descartado* leer <c>Context.Session</c> desde <c>_Layout.cshtml</c>: meteria deserializacion
/// de JSON dentro de un <c>.cshtml</c> y duplicaria la clave de sesion y el formato fuera de
/// <see cref="CartStore"/>, que existe justamente para ser el unico sitio que los conoce.
///
/// *Descartado* un filtro o un controller base que dejara el numero en <c>ViewData</c>: el layout
/// lo pintan TODAS las vistas del proyecto, asi que un controller que se olvidara de heredar o un
/// filtro que no se aplicara dejarian un badge a cero — un carrito con tres cosas dentro
/// anunciando que esta vacio, sin una linea de error en ninguna parte.
///
/// Un view component trae su propia dependencia por DI y se pinta donde se le invoca: si el
/// registro faltara, la pagina revienta nombrandolo en vez de mentir.
/// </summary>
public sealed class CartBadgeViewComponent(CartStore cartStore) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        // El CartStore es scoped y cachea durante la peticion, asi que esto NO vuelve a
        // deserializar el carrito que el controller ya leyo — y, mas importante, ve la MISMA
        // instancia, de modo que el badge no puede contradecir a la pagina que lo rodea.
        var cart = await cartStore.GetAsync(HttpContext.RequestAborted);

        // Se pasan las unidades y no el carrito entero: la vista solo tiene que pintar un numero,
        // y darle el objeto completo la invitaria a recorrer las lineas dentro del layout.
        return View(cart.UnitCount);
    }
}
