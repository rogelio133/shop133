namespace Shop133.Contracts;

/// <summary>
/// Una línea de pedido tal y como viaja entre servicios: qué producto, cuántas
/// unidades y a qué precio se cerró la compra.
///
/// No es la entidad OrderItem de Orders.Domain — es su representación de
/// transporte. La entidad puede cambiar sin romper el contrato, y al revés.
///
/// UnitPrice es el precio *congelado* en el momento del pedido, no el precio
/// actual del catálogo. Si Catalog cambia el precio después, el pedido ya
/// cobrado no se ve afectado.
/// </summary>
public sealed record OrderLine
{
    public required Guid ProductId { get; init; }
    public required int Quantity { get; init; }
    public required decimal UnitPrice { get; init; }
}
