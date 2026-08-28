using System.ComponentModel.DataAnnotations;

using Orders.Domain.Entities;

namespace Orders.API.Models;

/// <summary>
/// Una línea del cuerpo de <c>POST /orders</c>.
///
/// **Lleva la foto completa: qué, cuánto, y con qué sku, nombre y precio se
/// compró.** Eso *revierte* la decisión 4 de docs/fase_2_3.md, que dejó fuera esos
/// tres campos a propósito para que fuera Catalog quien los dictara. La reversión
/// es el precio de 3.3: al borrar la llamada síncrona, Orders se queda sin nadie a
/// quien preguntar —no puede leer <c>CatalogDb</c> por la regla 1— y los cinco
/// campos de <c>OrderLine</c> hay que rellenarlos igual, porque el pedido es un
/// hecho histórico y no una vista del catálogo.
///
/// *Descartada* la otra salida que docs/fase_0_3.md dejó anotada: que Orders
/// mantuviera una read-model del catálogo alimentada por eventos. Conserva la
/// autoridad de precios y permitiría seguir devolviendo 400 ante un producto que no
/// existe, pero exige MassTransit en Catalog.API, tres contratos nuevos —rompiendo
/// los nueve mensajes que fijó la decisión 1 de 0.3—, una migración en OrdersDb, un
/// consumer y un arranque en frío en el que la read-model está vacía y ningún
/// pedido se puede crear. Es un punto de roadmap entero, no un apartado de 3.3.
///
/// **Lo que se acepta a cambio, y conviene tenerlo escrito.** Orders ya no valida
/// nada de esto: un cliente puede pedir el producto 999999 a 0.01 y recibe un 201.
/// La comprobación no desaparece, se mueve — quien descubre que el producto no
/// existe es Inventory en 3.4, y su respuesta no es un código HTTP sino un
/// <c>StockRejected</c> que cancela el pedido. Eso es exactamente lo que la
/// coreografía cambia de sitio: la validación deja de ser síncrona y pasa a ser un
/// estado del pedido.
/// </summary>
public sealed record CreateOrderItemRequest
{
    /// <summary>
    /// El id de <c>CatalogDb</c>. El rango solo afirma que un id válido es
    /// positivo; **que exista ya no se comprueba aquí**. Es un puntero débil, no
    /// una clave foránea: sirve para que Inventory sepa qué reservar y para
    /// enlazar con la ficha del producto, y que esa ficha dé 404 es un desenlace
    /// aceptado desde 1.3, que borra productos físicamente.
    /// </summary>
    [Range(1, int.MaxValue)]
    public required int ProductId { get; init; }

    /// <summary>
    /// Cuántas unidades. El tope de 10.000 **no es una regla de negocio** —la
    /// entidad solo exige que sea positiva— sino una guarda de forma de entrada:
    /// al agrupar líneas repetidas se suman cantidades, y un tope explícito es lo
    /// que garantiza que esa suma no pueda desbordar el <c>int</c>. Con el
    /// máximo de 50 líneas de <see cref="CreateOrderRequest.Items"/>, el peor
    /// caso es 500.000, muy lejos de <c>int.MaxValue</c>.
    ///
    /// **No se comprueba contra el stock.** El <c>stock</c> que publica Catalog es
    /// el que muestra el catálogo; el reservable vive en <c>InventoryDb</c> desde
    /// 3.4 y es la saga quien lo reserva. Descontar aquí crearía un segundo
    /// número que llevaría la cuenta de lo mismo.
    /// </summary>
    [Range(1, 10_000)]
    public required int Quantity { get; init; }

    /// <summary>
    /// El código del producto tal y como estaba al comprar. Se congela.
    ///
    /// La longitud sale de <see cref="OrderItem.ProductSkuMaxLength"/>, nunca del
    /// literal 50 — misma disciplina que el <c>customerEmail</c>. Y esa constante
    /// es la copia que Orders mantiene de la de Catalog: CLAUDE.md deja escrito
    /// que pueden divergir, porque una foto solo tiene que aguantar lo que le
    /// mandaron ese día.
    /// </summary>
    [Required]
    [MaxLength(OrderItem.ProductSkuMaxLength)]
    public required string ProductSku { get; init; }

    /// <summary>
    /// El nombre del producto al comprar. Se congela: es lo que permite leer el
    /// pedido sin llamar a Catalog, y lo que hace que siga siendo legible después
    /// de que el producto se borre.
    /// </summary>
    [Required]
    [MaxLength(OrderItem.ProductNameMaxLength)]
    public required string ProductName { get; init; }

    /// <summary>
    /// El precio al que se cierra la compra, no el que tenga el catálogo mañana.
    ///
    /// Se usa el overload de <c>[Range]</c> con <c>typeof(decimal)</c> y los
    /// límites en cadena, y no el de <c>double</c>: ese convierte los extremos a
    /// coma flotante para comparar, que es justo lo que no se quiere hacer con un
    /// importe. El mínimo es 0.01 y no 0 porque la columna es
    /// <c>decimal(18,2)</c> desde 2.2 — por debajo de un céntimo, SQL Server
    /// redondearía a cero en silencio y el guard de la entidad
    /// (<c>ThrowIfNegativeOrZero</c>) ni se enteraría.
    ///
    /// **Este es el campo que hace visible lo que 3.3 cede.** El importe que
    /// acabará cobrando Payments en 3.5 sale de aquí, vía OrderCreated.Total y
    /// StockReserved.Amount, sin que ningún servicio lo contraste contra el
    /// catálogo.
    ///
    /// **Y a diferencia de la existencia, este hueco no lo cierra 3.4** — Inventory
    /// guarda cantidades, no precios, así que un producto que sí existe pedido a
    /// 0.01 atraviesa la saga entera y se cobra un céntimo. Lo cierra 4.8: Catalog
    /// consume OrderCreated y contesta OrderPricingValidated/OrderPricingRejected,
    /// y la saga gana un PricingPending previo al stock (4.9). Lo que se valida
    /// allí **no es la igualdad contra el precio actual** —eso rechazaría un pedido
    /// legítimo cuyo precio cambió a mitad del checkout— sino que la foto sea una
    /// que Catalog emitió. Ver la decisión 2b de docs/fase_3_3.md. Quién puede
    /// mandarla es otro problema, y es de 6.3 (carrito en sesión de servidor) y 8.1.
    /// </summary>
    [Range(typeof(decimal), "0.01", "1000000")]
    public required decimal UnitPrice { get; init; }
}
