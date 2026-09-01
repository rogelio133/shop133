namespace Inventory.Infrastructure.Entities;

/// <summary>
/// Una línea de una reserva: cuántas unidades de qué producto quedaron
/// comprometidas por un pedido.
///
/// **Sin Id propio y sin OrderId**, igual que <c>OrderItem</c> en Orders: no
/// tiene identidad fuera de su reserva, nadie la pide por id y ningún mensaje de
/// Shop133.Contracts la referencia. Se mapea como tipo *owned* en 3.4, así que
/// la FK al dueño la crea EF en la tabla y no existe en esta clase.
///
/// Es deliberadamente más pobre que <c>OrderLine</c>: de los cinco campos que
/// viajan en el mensaje, aquí se guardan **dos**. Sku, nombre y precio son la
/// foto que congeló el pedido y su dueño es Orders; copiarlos aquí sería una
/// tercera copia del mismo dato que nadie mantendría al día. Inventory guarda
/// cantidades.
/// </summary>
public sealed class StockReservationLine
{
    public StockReservationLine(int productId, int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(productId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);

        ProductId = productId;
        Quantity = quantity;
    }

    /// <summary>
    /// Constructor que usa EF Core al materializar filas. Sin <c>null!</c>:
    /// esta clase son dos enteros.
    /// </summary>
    private StockReservationLine()
    {
    }

    /// <summary>El producto de Catalog. Puntero débil, no clave foránea.</summary>
    public int ProductId { get; private set; }

    /// <summary>Las unidades comprometidas por esta línea.</summary>
    public int Quantity { get; private set; }
}
