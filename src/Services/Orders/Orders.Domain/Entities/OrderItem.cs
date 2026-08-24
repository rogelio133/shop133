namespace Orders.Domain.Entities;

/// <summary>
/// Una línea de un <see cref="Order"/>: qué producto, cuántas unidades y a qué
/// precio se cerró la compra.
///
/// Es la entidad de la que <c>Shop133.Contracts.OrderLine</c> es la
/// representación de transporte. Los cinco campos son los mismos y eso es
/// deliberado, pero son **dos tipos distintos**: la entidad puede ganar columnas
/// (un descuento, un número de línea) sin que eso sea un breaking change del
/// contrato, y al revés.
///
/// ── Es una foto, no una consulta ──
///
/// <see cref="ProductSku"/>, <see cref="ProductName"/> y <see cref="UnitPrice"/>
/// se congelan en el instante del pedido y nadie los vuelve a leer de Catalog.
/// No es una optimización para ahorrar llamadas: es lo que hace que el pedido
/// sea correcto. Con una base de datos por servicio (regla 1) no hay clave
/// foránea posible — SQL Server no soporta FK entre bases y <c>orders_user</c>
/// no tiene permiso sobre <c>CatalogDb</c> — y Catalog borra productos
/// **físicamente** (1.3). Sin estos campos, borrar un producto vendido dejaría
/// al pedido sin poder decir qué se compró.
///
/// <see cref="ProductId"/> es entonces un puntero débil, no una FK: sirve para
/// que Inventory sepa qué reservar (3.4) y para enlazar a la ficha. Que ese
/// enlace dé 404 es un resultado aceptado, no un bug.
/// </summary>
public sealed class OrderItem
{
    /// <summary>
    /// Longitudes máximas del texto congelado.
    ///
    /// **Coinciden con las de <c>Product</c> pero no se importan de allí**, y esa
    /// duplicación es la decisión, no un descuido. Reutilizar
    /// <c>Product.SkuMaxLength</c> obligaría a Orders.Domain a referenciar
    /// Catalog.Infrastructure: sería la regla 1 rota en tiempo de compilación —
    /// Orders dependiendo del modelo interno de Catalog — y la regla 5 rota de
    /// plano, porque la capa de dominio solo puede ver Shop133.Contracts.
    ///
    /// Y son independientes de verdad: una foto solo tiene que aguantar lo que
    /// Catalog mandó *ese día*. Si Catalog amplía su Sku a 80 caracteres, esta
    /// constante puede quedarse en 50 sin que ningún pedido histórico se rompa;
    /// lo que fallaría es un pedido *nuevo* de un producto con código largo, y
    /// ese fallo es correcto — significa que los dos servicios ya no encajan y
    /// alguien tiene que enterarse.
    /// </summary>
    public const int ProductSkuMaxLength = 50;

    public const int ProductNameMaxLength = 200;

    public OrderItem(
        int productId,
        string productSku,
        string productName,
        int quantity,
        decimal unitPrice)
    {
        // No hay Id ni OrderId entre los parámetros, y tampoco entre las
        // propiedades: ver la nota de <see cref="Order.Items"/>. Una línea no
        // tiene identidad fuera de su pedido.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(productId);

        // Trim() sí, ToUpperInvariant() **no** — al contrario que Product.Sku.
        // Una foto copia, no corrige: normalizar el código de producto es
        // trabajo de quien es dueño del dato, y ese es Catalog. Hoy es un no-op
        // porque Catalog ya lo emite en mayúsculas; el día que llegue algo en
        // minúsculas, lo correcto es que el pedido enseñe lo que le mandaron y
        // no una versión maquillada por Orders.
        var validatedSku = Validated(productSku, ProductSkuMaxLength, nameof(productSku));
        var validatedName = Validated(productName, ProductNameMaxLength, nameof(productName));

        // Cantidad mayor que cero: una línea de cero unidades no es una línea,
        // es una línea que sobra. Precio mayor que cero, igual que Product.Price
        // (decisión 4 de 1.1) — si algún día hay promociones a coste cero, esta
        // guarda es la que hay que releer, con el caso real delante.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(unitPrice);

        ProductId = productId;
        ProductSku = validatedSku;
        ProductName = validatedName;
        Quantity = quantity;
        UnitPrice = unitPrice;
    }

    /// <summary>
    /// Constructor que usa EF Core al materializar filas (2.2). Mismo motivo que
    /// el de <c>Product</c>: una fila ya persistida no se vuelve a validar. Si
    /// el dato es inválido, la excepción tiene que salir al escribirlo, no al
    /// leerlo tres meses después.
    /// </summary>
    private OrderItem()
    {
        ProductSku = null!;
        ProductName = null!;
    }

    /// <summary>
    /// La clave sustituta del producto en <c>CatalogDb</c>. Puntero débil, no
    /// clave foránea — ver la nota de la clase.
    ///
    /// Es un <c>int</c> mientras que <see cref="Order.Id"/> es un <c>Guid</c>.
    /// La asimetría es deliberada: el tipo del id lo decide quién lo acuña y
    /// cuándo. Ver la decisión 2 de docs/fase_1_1.md.
    /// </summary>
    public int ProductId { get; private set; }

    /// <summary>Congelado. El código tal y como estaba al comprar.</summary>
    public string ProductSku { get; private set; }

    /// <summary>Congelado. Lo que permite leer el pedido sin llamar a Catalog.</summary>
    public string ProductName { get; private set; }

    public int Quantity { get; private set; }

    /// <summary>
    /// Congelado. El precio al que se cerró la compra, no el del catálogo hoy.
    /// </summary>
    public decimal UnitPrice { get; private set; }

    /// <summary>
    /// Lo que cuesta esta línea. Calculado, no persistido — misma razón que
    /// <see cref="Order.Total"/>: multiplicar dos columnas que ya están en la
    /// fila no necesita una tercera que pueda desincronizarse.
    /// </summary>
    public decimal Subtotal => UnitPrice * Quantity;

    private static string Validated(string value, int maxLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        var trimmed = value.Trim();

        if (trimmed.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                trimmed.Length,
                $"El valor supera el máximo de {maxLength} caracteres.");
        }

        return trimmed;
    }
}
