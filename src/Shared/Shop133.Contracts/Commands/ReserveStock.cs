namespace Shop133.Contracts.Commands;

/// <summary>
/// Enviado por la saga a Inventory.API para que descuente stock de forma
/// provisional. Es un comando, no un evento: va dirigido a un destinatario
/// concreto y le pide que haga algo, en vez de anunciar un hecho consumado.
///
/// Inventory.API responde con StockReserved o StockRejected.
///
/// Lleva las líneas completas (no solo el OrderId) porque Inventory no puede
/// leer OrdersDb — regla de una base de datos por servicio.
///
/// De los cinco campos de OrderLine, a Inventory le sobran **tres**: UnitPrice,
/// ProductSku y ProductName. Solo mira ProductId y Quantity. La decisión 6 de
/// docs/fase_0_3.md aceptó esa redundancia cuando sobraba un campo de tres, a
/// cambio de no mantener dos tipos casi iguales; su nota de revisión explica
/// por qué se sostiene ahora que sobran tres de cinco. Se decide de nuevo en
/// 3.4, con Inventory.API delante: hoy no existe y partir el tipo sería
/// adivinar qué necesita.
/// </summary>
public sealed record ReserveStock
{
    public required Guid OrderId { get; init; }
    public required IReadOnlyList<OrderLine> Lines { get; init; }
}
