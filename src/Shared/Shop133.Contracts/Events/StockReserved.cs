namespace Shop133.Contracts.Events;

/// <summary>
/// Publicado por Inventory.API cuando ha descontado el stock de todas las
/// líneas del pedido. La reserva es provisional: si el pago falla, la saga
/// tendrá que soltarla con ReleaseStock.
///
/// A partir de aquí existe estado que compensar. Es el punto donde la saga
/// deja de ser reversible por sí sola.
///
/// Lo consume la saga, que pasa a PaymentPending, y Payments.API.
/// </summary>
public sealed record StockReserved
{
    public required Guid OrderId { get; init; }
}
