namespace Shop133.Contracts.Commands;

/// <summary>
/// Enviado por la saga a Inventory.API para que descuente stock de forma
/// provisional. Es un comando, no un evento: va dirigido a un destinatario
/// concreto y le pide que haga algo, en vez de anunciar un hecho consumado.
///
/// Inventory.API responde con StockReserved o StockRejected.
///
/// Lleva las líneas completas (no solo el OrderId) porque Inventory no puede
/// leer OrdersDb — regla de una base de datos por servicio. UnitPrice le sobra;
/// ver la decisión 5 de docs/fase_0_3.md.
/// </summary>
public sealed record ReserveStock
{
    public required Guid OrderId { get; init; }
    public required IReadOnlyList<OrderLine> Lines { get; init; }
}
