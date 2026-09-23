using System.ComponentModel.DataAnnotations;

using Microsoft.AspNetCore.Mvc.ModelBinding;

using Shop133.Web.Cart;

namespace Shop133.Web.Models;

/// <summary>
/// El formulario de checkout: un campo que se teclea y un resumen que solo se lee.
///
/// **Es el primer modelo del proyecto que rellena el model binder**, y por eso es el unico con
/// propiedades de escritura publica. <c>CatalogIndexViewModel</c> es un <c>record</c> con
/// <c>init</c> porque lo construye el controller entero; esto entra por un <c>&lt;form&gt;</c>.
///
/// **Tiene UN solo campo editable porque el contrato tiene uno.** <c>CreateOrderRequest</c> son
/// <c>CustomerEmail</c> e <c>Items</c>, y las lineas las pone el carrito. Ver
/// <see cref="Gateway.NewOrder"/> para lo que se descarto (direccion de envio) y por que.
///
/// Los mensajes de error van escritos a mano: los de DataAnnotations salen en INGLES y nombrando
/// la propiedad ("The CustomerEmail field is required"), lo cual es doblemente malo aqui porque
/// son los que jquery-validation-unobtrusive copia a <c>data-val-*</c> y ensena en el navegador
/// sin pasar por el servidor.
/// </summary>
public sealed class CheckoutViewModel
{
    /// <summary>
    /// Copia local de <c>Order.CustomerEmailMaxLength</c>, que sale de RFC 5321.
    ///
    /// Duplicada a mano por lo mismo que <see cref="ShoppingCart.MaxLines"/>: importarla exigiria
    /// un <c>ProjectReference</c> a <c>Orders.API</c> y pondria roja
    /// <c>Frontend_DoesNotReference_ServicesOrGateway</c>. Precedente entre servicios:
    /// <c>OrderItem.ProductSkuMaxLength</c>. **Pueden divergir**, y el sintoma de que lo hagan es
    /// un 400 de Orders — que por eso tiene rama propia en
    /// <see cref="Gateway.OrderRejectedException"/> en vez de acabar en la pagina de "el Gateway
    /// no responde".
    /// </summary>
    public const int CustomerEmailMaxLength = 320;

    /// <summary>
    /// A donde se avisa del desenlace. Viaja dentro de <c>OrderCreated</c> y acaba en el correo
    /// que manda Notifications al confirmarse o cancelarse el pedido (4.6).
    ///
    /// Las tres anotaciones son EXACTAMENTE las de <c>CreateOrderRequest.CustomerEmail</c>, y esa
    /// duplicacion es el punto: son las que el tag helper convierte en <c>data-val-required</c>,
    /// <c>data-val-email</c> y <c>data-val-maxlength</c>, asi que el navegador rechaza lo mismo
    /// que rechazaria el servidor **sin gastar una ida y vuelta**. El servidor las vuelve a
    /// comprobar igual: la validacion de cliente es comodidad, nunca una defensa.
    ///
    /// <c>[EmailAddress]</c> es deliberadamente laxo —busca una arroba con algo a cada lado— y
    /// esta bien que lo sea: la unica validacion real de un correo es mandarle un mensaje.
    ///
    /// Anulable porque un formulario vacio es un estado legitimo: es como llega la primera vez.
    /// </summary>
    [Required(ErrorMessage = "Hace falta un correo para avisarte del pedido.")]
    [EmailAddress(ErrorMessage = "Ese correo no tiene una forma válida.")]
    [MaxLength(CustomerEmailMaxLength, ErrorMessage = "El correo no puede pasar de {1} caracteres.")]
    [Display(Name = "Correo electrónico")]
    public string? CustomerEmail { get; set; }

    /// <summary>
    /// El carrito congelado, solo para pintarlo. **Las cantidades se cambian en <c>/cart</c>**, no
    /// aqui: un checkout que deja editar el pedido es otra pagina de carrito con otro nombre, y
    /// duplicaria los cuatro formularios de 6.3 con sus topes y sus avisos.
    ///
    /// **<c>[BindNever]</c> no es adorno.** Sin el, un POST fabricado a mano puede mandar sus
    /// propias lineas y su propio total, y aunque nada de eso llegaria al cuerpo del pedido —el
    /// controller lo construye desde la sesion—, el resumen que se le re-pinta al usuario le
    /// estaria ensenando cifras que escribio el atacante. Es la propiedad que 6.3 defendio en el
    /// carrito, un piso mas arriba: lo que el navegador manda no decide lo que la tienda dice.
    ///
    /// El precio, y es la trampa clasica de este patron: en el camino de error el controller tiene
    /// que REPOBLAR estas tres desde el carrito antes de devolver la vista. Si se olvida, el
    /// resumen sale vacio y parece que el carrito se vacio solo.
    /// </summary>
    [BindNever]
    public IReadOnlyList<CartLine> Lines { get; set; } = [];

    [BindNever]
    public decimal Total { get; set; }

    [BindNever]
    public int UnitCount { get; set; }
}
