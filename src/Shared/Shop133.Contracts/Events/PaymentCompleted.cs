namespace Shop133.Contracts.Events;

/// <summary>
/// Publicado por Payments.API cuando el cobro simulado sale bien. La saga pasa
/// a Confirmed y publica OrderConfirmed.
///
/// TransactionId es el identificador del cobro en el sistema de pago. Aquí es
/// un valor simulado, pero se incluye desde el principio porque es lo que
/// permitiría emitir la devolución si hubiera que compensar el pago.
/// </summary>
public sealed record PaymentCompleted
{
    public required Guid OrderId { get; init; }
    public required decimal Amount { get; init; }
    public required string TransactionId { get; init; }
}
