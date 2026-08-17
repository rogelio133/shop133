namespace Shop133.Contracts.Events;

/// <summary>
/// Publicado por la saga cuando el pedido termina sin completarse, por
/// cualquiera de los dos caminos de error: StockRejected (no había stock) o
/// PaymentFailed (el pago se rechazó, y el stock ya se soltó con ReleaseStock).
///
/// Lo consume Notifications.API. El consumidor no distingue por qué falló: para
/// eso está Reason, que arrastra el motivo del evento que originó la
/// cancelación.
/// </summary>
public sealed record OrderCancelled
{
    public required Guid OrderId { get; init; }
    public required string CustomerEmail { get; init; }
    public required string Reason { get; init; }
}
