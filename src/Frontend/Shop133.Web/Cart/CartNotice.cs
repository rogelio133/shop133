namespace Shop133.Web.Cart;

/// <summary>
/// Las dos claves de <c>TempData</c> con las que <c>CartController</c> le cuenta al layout lo que
/// acaba de pasar.
///
/// **Es el contrato entre quien escribe el aviso y quien lo pinta**, y por eso es un tipo y no dos
/// literales repartidos: el que escribe esta en <c>Controllers/</c> y el que pinta en
/// <c>Views/Shared/_Layout.cshtml</c>, asi que una errata en uno de los dos daria un aviso que
/// simplemente no aparece — sin error, sin log y sin nada que mirar.
///
/// Vive en <c>Shop133.Web.Cart</c> y no como constantes de <c>CartController</c> por una razon
/// practica: <c>_ViewImports.cshtml</c> ya importa este namespace, y meter
/// <c>Shop133.Web.Controllers</c> ahi solo para leer dos cadenas dejaria todos los controllers
/// disponibles como <c>@@model</c> de cualquier vista para siempre — que es justo la cesion que el
/// comentario de <c>_ViewImports</c> dejo anotada al importar <c>Shop133.Web.Gateway</c>.
///
/// **6.7 relee este archivo.** Lo que ese punto cambia es como se PINTAN estos dos avisos (un
/// toast de Bootstrap en vez de un <c>alert</c>), no de donde salen: estas claves siguen siendo el
/// anclaje. <c>TempData</c> —y no <c>ViewData</c>— porque el aviso tiene que cruzar el 302 del
/// POST-Redirect-GET.
///
/// Dos claves y no una: un aviso en verde y uno en amarillo no se pintan igual, y distinguirlos
/// por el texto del mensaje seria adivinar.
/// </summary>
public static class CartNotice
{
    /// <summary>Algo salio bien: se anadio, se actualizo, se quito, se vacio.</summary>
    public const string MessageKey = "CartMessage";

    /// <summary>
    /// Algo no se pudo hacer y hay un motivo que leer. **No es una excepcion**: son los rechazos
    /// que devuelve <see cref="ShoppingCart"/> (topes de lineas o de cantidad) y el producto que
    /// ya no esta en el catalogo. Una caida del Gateway NO pasa por aqui — esa tiene su propia
    /// vista y su propio codigo de estado.
    /// </summary>
    public const string ErrorKey = "CartError";
}
