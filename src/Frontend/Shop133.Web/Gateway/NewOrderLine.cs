namespace Shop133.Web.Gateway;

/// <summary>
/// Una linea del cuerpo de <c>POST /api/orders</c>, tal y como viaja por el cable.
///
/// **Son las CINCO primeras propiedades de <see cref="Cart.CartLine"/>, copiadas campo a campo.**
/// Eso no es un parecido: es la promesa que el <c>///</c> de aquel tipo lleva escrita desde 6.3
/// —"6.4 la copiara campo a campo al cuerpo del pedido"— y el motivo entero de que el carrito viva
/// en la sesion del servidor. El precio de cada linea lo acuno <c>CartController.Add</c> leyendo
/// Catalog por el Gateway; el navegador no lo ha visto ni lo manda.
///
/// **Se RE-DECLARA aqui en vez de importar <c>CreateOrderItemRequest</c> de Orders.API**, que
/// tiene exactamente esta forma. Importarlo exigiria un <c>ProjectReference</c> a un servicio: la
/// regla 3 de CLAUDE.md rota de frente, con <c>Frontend_DoesNotReference_ServicesOrGateway</c> en
/// rojo desde 0.6. Es el precedente literal de <c>CatalogProduct</c>, que re-declara 4 de los 9
/// campos de <c>ProductResponse</c>, y el de <c>OrderItem.ProductSkuMaxLength</c> entre servicios:
/// pueden divergir, y el dia que lo hagan el sintoma es un 400 — que por eso tiene rama propia en
/// <see cref="OrdersClient"/>.
///
/// El nombre NO es el del servidor, a proposito: mismo criterio que <see cref="CatalogPage{T}"/>
/// frente a <c>PagedResponse</c>. Un tipo del frontend con el nombre del DTO del servicio invita a
/// "unificarlos", que es justo lo que la regla 3 prohibe.
///
/// Aqui NO hay validation attributes. Las invariantes de forma (50 lineas, 1..10.000 unidades) las
/// sostiene <see cref="Cart.ShoppingCart"/>, que es quien puede contarselo al usuario en la
/// pantalla donde se equivoco; este tipo solo serializa.
/// </summary>
public sealed record NewOrderLine
{
    public required int ProductId { get; init; }

    public required string ProductSku { get; init; }

    public required string ProductName { get; init; }

    public required decimal UnitPrice { get; init; }

    public required int Quantity { get; init; }
}
