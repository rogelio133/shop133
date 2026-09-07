namespace Shop133.Contracts.Events;

/// <summary>
/// Publicado por Catalog.API cuando la foto de precios que viaja dentro de
/// <see cref="OrderCreated"/> es auténtica: los productos existen en
/// <c>CatalogDb</c>, cada <c>UnitPrice</c> es un precio que Catalog llegó a
/// ofrecer, y el <c>Total</c> del pedido cuadra con la suma de sus líneas.
///
/// Lo consume la saga (4.9), que estaba esperándolo en <c>PricingPending</c>: al
/// recibirlo pasa a <c>StockPending</c>. Hasta que 4.9 exista, este evento se
/// publica **al vacío** — su exchange tiene cero colas ligadas, igual que les
/// pasó a <c>StockRejected</c> y <c>PaymentFailed</c> entre 3.4 y 4.3.
///
/// ── El undécimo mensaje, y por qué 4.8 lo añade ──
///
/// La decisión 1 de docs/fase_0_3.md fijó nueve mensajes de golpe para que la
/// saga no tuviera que tocar Contracts; 4.4 añadió el décimo con el consumer de
/// la compensación delante, y éste es el mismo caso con el precedente ya sentado.
/// Lo que lo obliga es un agujero medido: la decisión 2b de docs/fase_3_3.md dejó
/// que el cuerpo del <c>POST /orders</c> traiga el precio y dio por hecho que la
/// comprobación "se mudaba a Inventory". Solo se mudó la de **existencia** —
/// Inventory guarda cantidades, no importes—, así que un pedido de un producto
/// que existe a <c>0.01</c> atravesaba la saga entera y **se cobraba un
/// céntimo**. El importe se había quedado sin dueño.
///
/// ── Qué significa "auténtica", que no es lo que parece ──
///
/// **No** significa que el precio coincida con el del catálogo *hoy*. Comparar
/// contra el precio actual rechazaría un pedido legítimo cuyo precio cambió a
/// mitad del checkout, y congelar el precio que el cliente vio es el
/// comportamiento correcto (todo el <c>///</c> de <see cref="OrderLine"/> existe
/// para decir eso). Significa que el precio de la foto es un precio que Catalog
/// **llegó a ofrecer**, dentro de una ventana de checkout. Por eso la validación
/// vive en el único servicio que puede firmar ese dato y no en Orders.
///
/// ── Por qué solo lleva el OrderId ──
///
/// Mismo criterio que <see cref="StockReleased"/>: su único consumidor es la
/// saga, a la que solo le hace falta saber *que* la espera terminó. Un importe
/// aquí sería una segunda fuente para un hecho que <c>OrderCreated.Total</c> ya
/// lleva — el argumento exacto por el que 4.4 le quitó las <c>Lines</c> a
/// <c>ReleaseStock</c>. El contrario es <c>StockReserved.Amount</c>, que carga un
/// dato ajeno solo porque su consumidor no tiene a quién preguntárselo.
/// </summary>
public sealed record OrderPricingValidated
{
    public required Guid OrderId { get; init; }
}
