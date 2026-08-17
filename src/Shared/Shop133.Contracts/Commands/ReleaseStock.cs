namespace Shop133.Contracts.Commands;

/// <summary>
/// La compensación. Enviado por la saga a Inventory.API cuando el pago falla
/// después de que el stock se haya reservado, para devolver las unidades.
///
/// No existe ningún camino en el que un StockReserved acabe en Cancelled sin
/// pasar por aquí; si lo hubiera, el stock quedaría bloqueado para siempre sin
/// que nadie lo notara.
///
/// Su consumidor tiene que ser idempotente de verdad: un ReleaseStock
/// entregado dos veces devolvería el stock dos veces, creando unidades de la
/// nada. Es peor que un duplicado de ReserveStock, que solo bloquea de más.
/// </summary>
public sealed record ReleaseStock
{
    public required Guid OrderId { get; init; }
    public required IReadOnlyList<OrderLine> Lines { get; init; }
}
