using Orders.Domain.Entities;

namespace Orders.API.Models;

/// <summary>
/// Una línea del pedido tal y como sale por HTTP.
///
/// Publica los cinco campos congelados **más** el <c>Subtotal</c>, que en la
/// entidad es calculado y no tiene columna. Que un dato sea derivado no es motivo
/// para esconderlo: quien lea el pedido lo necesita, y calcularlo aquí evita que
/// cada cliente reimplemente la multiplicación (y que alguno la haga con
/// <c>double</c>).
///
/// Sin validation attributes: son cosa de la entrada.
/// </summary>
public sealed record OrderItemResponse
{
    /// <summary>
    /// Puntero débil a <c>CatalogDb</c>, no clave foránea. Puede apuntar a un
    /// producto que Catalog ya borró — el borrado es físico (1.3) y ninguna FK
    /// puede cruzar dos bases. Que el enlace a la ficha dé 404 es un resultado
    /// aceptado; el pedido sigue sabiendo qué se compró gracias a los dos campos
    /// de abajo.
    /// </summary>
    public required int ProductId { get; init; }

    public required string ProductSku { get; init; }

    public required string ProductName { get; init; }

    public required int Quantity { get; init; }

    /// <summary>El precio del día de la compra, no el que Catalog publique hoy.</summary>
    public required decimal UnitPrice { get; init; }

    public required decimal Subtotal { get; init; }

    public static OrderItemResponse From(OrderItem item) => new()
    {
        ProductId = item.ProductId,
        ProductSku = item.ProductSku,
        ProductName = item.ProductName,
        Quantity = item.Quantity,
        UnitPrice = item.UnitPrice,
        Subtotal = item.Subtotal,
    };
}
