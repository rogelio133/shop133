namespace Shop133.Web.Orders;

/// <summary>
/// Un pedido que se tramito desde ESTA sesion, apuntado para poder volver a el.
///
/// **Es un recibo, no el pedido.** Los tres campos se congelan en el momento del 201 y no se
/// releen nunca: el estado de verdad vive en Orders y lo consulta la pagina de seguimiento. Meter
/// aqui el estado seria guardar una copia que envejece — y la pagina de 6.5 existe precisamente
/// porque ese dato cambia solo.
///
/// Es el mismo criterio con el que el carrito de 6.3 es una foto y <c>/cart</c> no relee ningun
/// precio; la diferencia es que alli la foto es deliberadamente autoritativa (es lo que se va a
/// comprar) y aqui es solo una etiqueta para reconocer el pedido en una lista.
///
/// <c>FormattedTotal</c> viene ya formateado, igual que en <c>PlacedOrderViewModel</c> y por un
/// motivo parecido: <see cref="RecentOrdersStore"/> serializa esto a JSON y un <c>decimal</c>
/// sobreviviria, pero formatearlo al leerlo obligaria a que la vista conociera <c>Money</c> para
/// algo que ya se sabia al escribirlo. Se guarda la cadena que se va a ensenar.
/// </summary>
public sealed record RecentOrder
{
    public required Guid Id { get; init; }

    /// <summary>Cuando se tramito, en UTC. La vista lo pinta en local.</summary>
    public required DateTimeOffset PlacedAt { get; init; }

    public required string FormattedTotal { get; init; }
}
