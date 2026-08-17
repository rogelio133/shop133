namespace Shop133.Contracts.Events;

/// <summary>
/// Publicado por la saga cuando el pedido llega a su estado final feliz: stock
/// reservado y pago cobrado.
///
/// Lo consume Notifications.API, que "envía" el email de confirmación.
///
/// Lleva CustomerEmail porque Notifications.API no tiene base de datos propia y
/// no puede leer OrdersDb — regla de una base de datos por servicio. O el dato
/// viaja en el evento, o el servicio no puede hacer su trabajo.
/// </summary>
public sealed record OrderConfirmed
{
    public required Guid OrderId { get; init; }
    public required string CustomerEmail { get; init; }
}
