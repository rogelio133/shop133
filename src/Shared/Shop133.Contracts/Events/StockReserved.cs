namespace Shop133.Contracts.Events;

/// <summary>
/// Publicado por Inventory.API cuando ha descontado el stock de todas las
/// líneas del pedido. La reserva es provisional: si el pago falla, la saga
/// tendrá que soltarla con ReleaseStock.
///
/// A partir de aquí existe estado que compensar. Es el punto donde la saga
/// deja de ser reversible por sí sola.
///
/// Lo consume la saga, que pasa a PaymentPending, y Payments.API.
///
/// ── Por qué lleva un importe que Inventory no usa ──
///
/// Amount es el total del pedido, y a Inventory no le sirve para nada: lo
/// recibe en OrderCreated.Total y lo reenvía tal cual. Es un servicio
/// intermedio acarreando un dato financiero que no le pertenece, y está puesto
/// a conciencia.
///
/// El motivo es que Payments.API tiene que publicar PaymentCompleted.Amount y
/// no tiene de dónde sacarlo. No puede leer OrdersDb —regla 1 de CLAUDE.md, una
/// base de datos por servicio— y en la Fase 3 no hay saga que se lo diga: la
/// comunicación es **coreografía**, cada servicio reacciona al evento del
/// anterior. O el dato viaja en el evento, o Payments no puede hacer su
/// trabajo. Es el mismo principio de la decisión 3 de docs/fase_0_3.md, el que
/// también obliga a que OrderConfirmed lleve el CustomerEmail.
///
/// Y aquí está la lección que este campo deja escrita: en coreografía, un
/// servicio termina transportando datos ajenos porque es el único que está en
/// medio del camino. Ese es justo el argumento a favor de la **orquestación**
/// de la Fase 4, donde el que sabe el total es la saga, que lo guardó al
/// arrancar con OrderCreated.
///
/// **No desaparece en la Fase 4.** La decisión 1 de docs/fase_0_3.md eligió los
/// 9 mensajes de golpe para que la saga no tuviera que tocar Contracts, así que
/// no existe un comando ProcessPayment: el flujo sigue siendo
/// StockReserved → PaymentCompleted y Payments.API consume este evento en las
/// dos fases. El campo hace falta en ambas.
///
/// Se llama Amount y no Total por simetría con PaymentCompleted.Amount, que es
/// el campo al que acaba alimentando.
/// </summary>
public sealed record StockReserved
{
    public required Guid OrderId { get; init; }

    /// <summary>
    /// El importe a cobrar, reenviado desde OrderCreated.Total. Inventory no lo
    /// mira; lo transporta porque Payments no puede preguntárselo a nadie.
    /// </summary>
    public required decimal Amount { get; init; }
}
