namespace Shop133.Contracts.Events;

/// <summary>
/// Publicado por Payments.API cuando el cobro se rechaza.
///
/// Este es el evento que justifica todo el proyecto: llega *después* de que el
/// stock ya se haya reservado, así que la saga no puede limitarse a cancelar —
/// tiene que compensar enviando ReleaseStock antes de pasar a Cancelled.
/// </summary>
public sealed record PaymentFailed
{
    public required Guid OrderId { get; init; }
    public required string Reason { get; init; }
}
