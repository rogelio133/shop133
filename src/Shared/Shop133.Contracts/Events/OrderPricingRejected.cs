namespace Shop133.Contracts.Events;

/// <summary>
/// Publicado por Catalog.API cuando la foto de precios de un
/// <see cref="OrderCreated"/> no es auténtica: algún producto no existe en
/// <c>CatalogDb</c>, algún <c>UnitPrice</c> no es un precio que Catalog llegara a
/// ofrecer, o el <c>Total</c> del pedido no cuadra con la suma de sus líneas.
///
/// Lo consume la saga (4.9), que estaba esperándolo en <c>PricingPending</c>: al
/// recibirlo cancela el pedido. Hasta que 4.9 exista se publica **al vacío**,
/// igual que su gemelo <see cref="OrderPricingValidated"/>.
///
/// ── Ojo: aquí NO se afirma que no haya nada que compensar ──
///
/// El <c>///</c> de <see cref="StockRejected"/> sí lo afirma, y puede hacerlo
/// porque la reserva de Inventory es atómica: un rechazo significa que ninguna
/// unidad se movió. **Este evento no puede prometer lo mismo**, y conviene
/// dejarlo escrito antes de que 4.9 dé por hecho lo contrario: Inventory sigue
/// consumiendo <c>OrderCreated</c> del mismo exchange fanout (decisión 2 de
/// docs/fase_4_1.md, sin cambios desde entonces), así que esta validación y la
/// reserva de stock corren **en paralelo** y no en secuencia. Un rechazo de
/// precio puede llegar a la saga con el stock ya reservado.
///
/// El título de 4.9 en el roadmap dice "sin nada que compensar". Puede ser falso,
/// y es 4.9 quien tiene que releerlo con la máquina de estados delante — el mismo
/// movimiento con el que 4.4 corrigió la nota de 4.3 sobre
/// <c>CompensatingStock</c>. Lo que 4.8 no hace es escribir aquí una promesa que
/// el diseño no sostiene.
///
/// ── Reason ──
///
/// Texto para diagnóstico y para el email de Notifications, **nunca un código que
/// nadie deba parsear para decidir**. Mismo criterio que
/// <see cref="StockRejected"/>: se acumulan todos los problemas del pedido en una
/// sola cadena, porque quien lea el mensaje quiere saber qué falló, no cuál falló
/// primero.
///
/// Es obligatorio y no opcional porque tiene destinatario: 4.9 lo arrastra a
/// <c>OrderCancelled.Reason</c>, que Notifications.API pone en el cuerpo del
/// correo desde 4.6. Sin él, el cliente recibiría un aviso de cancelación que no
/// dice nada — el mismo modo de fallo que 4.4 arregló añadiendo
/// <see cref="StockReleased"/>: un contrato que no puede cumplir lo que otro
/// afirma de él.
/// </summary>
public sealed record OrderPricingRejected
{
    public required Guid OrderId { get; init; }
    public required string Reason { get; init; }
}
