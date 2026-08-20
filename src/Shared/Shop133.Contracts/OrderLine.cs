namespace Shop133.Contracts;

/// <summary>
/// Una línea de pedido tal y como viaja entre servicios: qué producto, cuántas
/// unidades y a qué precio se cerró la compra.
///
/// No es la entidad OrderItem de Orders.Domain — es su representación de
/// transporte. La entidad puede cambiar sin romper el contrato, y al revés.
///
/// ── El principio: un pedido es un hecho histórico, no una vista del catálogo ──
///
/// ProductSku, ProductName y UnitPrice son una **foto del producto en el
/// instante del pedido**. Ninguno se vuelve a consultar: si Catalog cambia el
/// precio, corrige el código o renombra el producto mañana, el pedido de ayer
/// no se entera. Eso no es una optimización para ahorrar llamadas — es lo que
/// hace que el pedido sea *correcto*.
///
/// La razón de fondo es que aquí no existe la integridad referencial. Con una
/// base de datos por servicio (regla 1 de CLAUDE.md) no hay clave foránea
/// posible: SQL Server no soporta FK entre bases, y además orders_user no tiene
/// permiso sobre CatalogDb. En un monolito, OrderItems haría JOIN con Products
/// y el pedido de hace dos años se mostraría con el precio de hoy; ese bug
/// clásico es lo que la FK hace posible. Lo que sustituye a la FK es copiar.
///
/// La prueba de que hace falta: Catalog borra productos **físicamente** (1.3).
/// Sin estos campos, borrar un producto vendido dejaría al pedido sin poder
/// decir qué se compró. Con ellos, el pedido sigue íntegro y lo único que se
/// pierde es el enlace a la ficha — que puede dar 404 sin que nada se rompa.
///
/// ── Qué es ProductId entonces ──
///
/// No es una clave foránea: es un puntero débil. Sirve para que Inventory sepa
/// de qué producto reservar stock, para enlazar a la ficha desde el pedido y
/// para agrupar. Lo que **no** hace es ser la fuente del nombre o del precio de
/// la línea; eso viaja copiado, arriba.
///
/// Es la clave sustituta de Catalog, no el Sku, por el mismo motivo que el
/// precio va congelado: un código de producto se corrige y se renumera, la
/// clave sustituta no cambia nunca. Por eso viajan los dos — el Sku es lo que
/// una persona lee, el Id es lo que correlaciona.
///
/// Es un int, a diferencia de OrderId, que es Guid. La asimetría es deliberada:
/// el tipo del id lo decide quién lo acuña y cuándo. Orders.API necesita el
/// OrderId antes de tocar la base porque es la clave de correlación de la saga;
/// un producto lo crea Catalog con un POST síncrono y su base es el único
/// escritor. Ver docs/fase_1_1.md.
///
/// ── Consecuencia para quien construya un OrderLine ──
///
/// Los cinco campos hay que rellenarlos, y tres salen de Catalog. En la Fase 2
/// los trae la llamada HttpClient de 2.3 (deuda deliberada). Cuando 3.3 borre
/// esa llamada, alguien tendrá que seguir rellenándolos sin preguntar a nadie
/// — es el problema de propiedad del dato frente a localidad del dato, y está
/// anotado en la nota de revisión de la decisión 6 de docs/fase_0_3.md.
/// </summary>
public sealed record OrderLine
{
    public required int ProductId { get; init; }

    /// <summary>Congelado. El código tal y como estaba al comprar.</summary>
    public required string ProductSku { get; init; }

    /// <summary>Congelado. Lo que permite leer el pedido sin llamar a Catalog.</summary>
    public required string ProductName { get; init; }

    public required int Quantity { get; init; }

    /// <summary>Congelado. El precio al que se cerró la compra, no el del catálogo hoy.</summary>
    public required decimal UnitPrice { get; init; }
}
