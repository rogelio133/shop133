namespace Shop133.Contracts.Events;

/// <summary>
/// Publicado por Orders.API cuando acepta un pedido y lo persiste en estado
/// Pending. No significa que el pedido sea válido: significa que se ha
/// registrado la intención de comprar.
///
/// Lo consume la OrderStateMachine (Fase 4), que arranca una instancia de saga
/// y envía ReserveStock a Inventory.API.
///
/// OrderId es la clave de correlación de toda la saga: todos los mensajes
/// posteriores de este pedido lo llevan.
/// </summary>
public sealed record OrderCreated
{
    public required Guid OrderId { get; init; }
    public required string CustomerEmail { get; init; }
    public required IReadOnlyList<OrderLine> Lines { get; init; }
    public required decimal Total { get; init; }
}
