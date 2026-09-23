namespace Shop133.Web.Gateway;

/// <summary>
/// El cuerpo de <c>POST /api/orders</c>: a quien se le avisa, y que se lleva.
///
/// Re-declarado localmente por el mismo motivo que <see cref="NewOrderLine"/> — ver alli el
/// razonamiento completo sobre la regla 3.
///
/// **Solo dos propiedades, y esa escasez es la que decide la forma del formulario de 6.4.**
/// <c>CreateOrderRequest</c> tiene <c>CustomerEmail</c> e <c>Items</c> y nada mas, asi que el
/// checkout tiene exactamente UN campo editable. *Descartado* pedir nombre y direccion de envio:
/// no hay donde ponerlos, de modo que o se tiran al vacio —un formulario que miente sobre lo que
/// hace con lo que le teclean— o hay que tocar el contrato, la entidad y una migracion de Orders,
/// que es un punto de roadmap entero metido dentro de uno de frontend.
/// </summary>
public sealed record NewOrder
{
    public required string CustomerEmail { get; init; }

    public required IReadOnlyList<NewOrderLine> Items { get; init; }
}
