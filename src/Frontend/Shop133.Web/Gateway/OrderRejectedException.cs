namespace Shop133.Web.Gateway;

/// <summary>
/// Orders contesto 400: el cuerpo del pedido no vale.
///
/// **Existe para que un 400 NO se pinte como "el Gateway no responde".** Hasta 6.4 este proyecto
/// solo hacia GET, y <c>CatalogClient.ReadAsync</c> convierte CUALQUIER no-2xx en
/// <see cref="GatewayUnavailableException"/> — razonable mientras el unico fallo posible era de la
/// dependencia. Un 400 no lo es: el Gateway contesto, el servicio contesto, y lo que esta mal es
/// lo que se le mando. Ensenar la pagina de caida seria el diagnostico seguro de si mismo y
/// equivocado que la decision 3 de 6.2 se nego a dar, y por el que
/// <c>CatalogClient.GetProductsAsync</c> recorta el <c>page</c> a 1 en vez de dejar que la API
/// conteste 400.
///
/// **Y es una incoherencia del PROGRAMA, no un error del usuario.** Lo unico que se teclea en el
/// checkout es el correo, y eso se valida en cliente y en servidor antes de salir. Si aun asi
/// llega un 400, el cuerpo lo construyo el carrito: o las constantes duplicadas de
/// <c>ShoppingCart</c> divergieron de las <c>[MaxLength]</c>/<c>[Range]</c> de Orders, o hay una
/// linea que nunca debio entrar. Por eso se lanza y se loguea como <c>LogError</c>, mientras que
/// un tope de carrito devuelve un texto y no revienta (<c>ShoppingCart.Add</c>, precedente
/// <c>StockItem.CanReserve</c>): ahi se equivoca una persona, aqui nos equivocamos nosotros.
///
/// *Descartado* devolver un resultado con dos formas (pedido o motivos) desde
/// <see cref="OrdersClient"/>: obligaria a todo llamante a desempaquetar en el camino feliz, que
/// es el unico que deberia leerse de corrido, y el cliente ya comunica el fallo lanzando.
///
/// <see cref="Errors"/> son los mensajes de <c>ValidationProblemDetails</c> ya aplanados, sin sus
/// claves: nombran campos de un DTO (<c>Items[0].ProductId</c>) que este proyecto no declara y que
/// no le dicen nada a quien compra. Van al resumen del formulario con clave vacia.
/// </summary>
public sealed class OrderRejectedException(string message, IReadOnlyList<string> errors)
    : Exception(message)
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
