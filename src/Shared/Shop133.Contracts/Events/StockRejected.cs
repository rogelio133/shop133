namespace Shop133.Contracts.Events;

/// <summary>
/// Publicado por Inventory.API cuando no puede reservar el stock: alguna línea
/// no tiene unidades suficientes o el producto no existe.
///
/// No hay nada que compensar — la reserva es atómica, o entra entera o no entra
/// nada. La saga va directa a Cancelled.
///
/// Reason es texto para diagnóstico y para el email de Notifications, no un
/// código que nadie deba parsear para decidir.
/// </summary>
public sealed record StockRejected
{
    public required Guid OrderId { get; init; }
    public required string Reason { get; init; }
}
