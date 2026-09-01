namespace Inventory.Infrastructure.Entities;

/// <summary>
/// El stock reservable de un producto. Es la primera pieza de código de negocio
/// de Inventory y la contraparte de <c>Product.Stock</c>, que desde 1.1 lleva
/// escrito que solo es el número que el catálogo *muestra*.
///
/// Vive en Inventory.Infrastructure y no en un Inventory.Domain porque Inventory
/// no tiene capa de dominio: la decisión 1 de docs/fase_1_1.md dejó a Catalog
/// sin ella por ser un CRUD, y aquí el criterio es el mismo — la saga vive en
/// Orders.Domain, no aquí. Este servicio suma y resta cantidades.
///
/// Estilo calcado de <c>Product</c> y <c>Order</c>: clase mutable con setters
/// privados, guardas en el constructor, constructor privado sin parámetros para
/// EF Core. Una entidad tiene identidad y vida; el stock de este producto va a
/// cambiar en cada reserva.
/// </summary>
public sealed class StockItem
{
    public StockItem(int productId, int quantityOnHand)
    {
        // ProductId es el id que acuñó Catalog con su IDENTITY, y aquí es
        // además la clave primaria. No hay ninguna FK posible: el producto vive
        // en CatalogDb, SQL Server no tiene claves foráneas entre bases e
        // inventory_user ni siquiera puede abrir CatalogDb (regla 1). Es un
        // puntero débil, exactamente como OrderItem.ProductId.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(productId);

        // Cero sí es válido: un producto agotado tiene fila con 0 unidades. Lo
        // que no puede es ser negativo.
        ArgumentOutOfRangeException.ThrowIfNegative(quantityOnHand);

        ProductId = productId;
        QuantityOnHand = quantityOnHand;
        QuantityReserved = 0;
    }

    /// <summary>
    /// Constructor que usa EF Core al materializar filas. Mismo motivo que en
    /// <c>Product</c> y <c>Order</c>: una fila ya persistida no se vuelve a
    /// validar — las guardas protegen la escritura, no la lectura.
    ///
    /// Aquí no hace falta ningún <c>null!</c> porque no hay ni un solo campo de
    /// referencia: <c>StockItem</c> es tres enteros.
    /// </summary>
    private StockItem()
    {
    }

    /// <summary>
    /// Clave primaria, y es el id del producto en Catalog — no un identificador
    /// propio de Inventory. Se mapea con <c>ValueGeneratedNever()</c> en 3.4:
    /// sin esa línea la convención de EF le pondría un IDENTITY a una columna
    /// cuyo valor lo decide otro servicio.
    /// </summary>
    public int ProductId { get; private set; }

    /// <summary>
    /// Las unidades que físicamente hay. **No baja al reservar** — bajar aquí
    /// haría indistinguible "vendido" de "apartado para un pedido que aún puede
    /// caerse", que es justo la distinción que la compensación de la Fase 4
    /// necesita poder deshacer.
    /// </summary>
    public int QuantityOnHand { get; private set; }

    /// <summary>
    /// Las unidades comprometidas con pedidos que todavía están en vuelo. Sube
    /// con <see cref="Reserve"/> en 3.4 y bajará con el <c>ReleaseStock</c> de
    /// 4.4.
    ///
    /// Nada las convierte nunca en una bajada de <see cref="QuantityOnHand"/>, y
    /// eso es un hueco conocido, no un olvido: el roadmap no tiene ningún paso
    /// que "confirme" una reserva contra el stock físico. Se anota en la sección
    /// Pendiente de docs/fase_3_4.md.
    /// </summary>
    public int QuantityReserved { get; private set; }

    /// <summary>
    /// Lo que queda por comprometer. **Calculado, no persistido**: una sola
    /// fuente de verdad, imposible de desincronizar de las otras dos columnas.
    /// Mismo criterio que <c>Order.Total</c> y <c>OrderItem.Subtotal</c>.
    ///
    /// La configuración de EF tiene que hacerle <c>Ignore()</c> o el modelo ni
    /// se construye: es una propiedad pública sin setter ni campo de respaldo.
    /// </summary>
    public int QuantityAvailable => QuantityOnHand - QuantityReserved;

    /// <summary>
    /// Si cabe reservar esa cantidad. Existe separado de <see cref="Reserve"/>
    /// porque la reserva de 3.4 es **atómica sobre todo el pedido**: hay que
    /// poder preguntar por todas las líneas antes de tocar ninguna.
    /// </summary>
    public bool CanReserve(int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);

        return quantity <= QuantityAvailable;
    }

    /// <summary>
    /// Compromete unidades para un pedido. Sube <see cref="QuantityReserved"/>
    /// y deja <see cref="QuantityOnHand"/> intacto.
    ///
    /// Lanza si no cabe, en vez de devolver <c>false</c>: quien llama ya debería
    /// haber preguntado con <see cref="CanReserve"/>, así que llegar aquí sin
    /// hueco es un error de programación y no un caso de negocio. El caso de
    /// negocio —no hay stock— se resuelve antes, publicando StockRejected.
    /// </summary>
    public void Reserve(int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);

        if (quantity > QuantityAvailable)
        {
            throw new InvalidOperationException(
                $"No hay stock suficiente del producto {ProductId}: se piden {quantity} " +
                $"y hay {QuantityAvailable} disponibles.");
        }

        QuantityReserved += quantity;
    }

    // Sin Release(). No tiene llamante en 3.4 — quien devuelve unidades es el
    // consumer de ReleaseStock, que llega en 4.4. Inventar aquí su firma sería
    // exactamente lo que 1.1 evitó dejando a Product sin Update() hasta que 1.3
    // lo necesitó, y lo que 2.1 evitó no escribiendo Order.Confirm().
    //
    // Y no es una firma obvia: 4.4 tiene que decidir antes si ReleaseStock puede
    // prescindir de Lines y soltar por OrderId leyendo StockReservations, en
    // cuyo caso el método que hace falta no recibe una cantidad.
}
