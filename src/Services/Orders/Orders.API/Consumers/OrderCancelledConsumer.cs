using MassTransit;

using Microsoft.EntityFrameworkCore;

using Orders.Domain.Entities;
using Orders.Infrastructure.Entities;
using Orders.Infrastructure.Persistence;

using Shop133.Contracts.Events;

namespace Orders.API.Consumers;

/// <summary>
/// El gemelo de <see cref="OrderConfirmedConsumer"/> para el otro desenlace:
/// escucha el <c>OrderCancelled</c> que la saga publica desde 4.3 por sus dos
/// caminos de error y deja el pedido en <c>Cancelled</c> en <c>OrdersDb</c>.
///
/// ── Dos consumers y no uno con las dos interfaces ──
///
/// Un solo <c>OrderOutcomeConsumer : IConsumer&lt;OrderConfirmed&gt;,
/// IConsumer&lt;OrderCancelled&gt;</c> compilaría, tendría una sola cola
/// (<c>order-outcome</c>) con los dos exchanges ligados y escribiría la guarda de
/// idempotencia una sola vez. Se descartó por dos motivos:
///
/// - **La convención del proyecto nombra el consumer por el mensaje que
///   consume** — <c>OrderCreatedConsumer</c> en Inventory,
///   <c>StockReservedConsumer</c> en Payments. Con dos mensajes en una clase, el
///   nombre deja de decir qué escucha y la cola tampoco.
/// - **Separa los dos desenlaces en dos colas**, así que un fallo procesando
///   cancelaciones no atasca las confirmaciones, y en la UI de RabbitMQ se ve por
///   separado cuántos pedidos terminan de cada forma.
///
/// El precio, dicho en voz alta: la guarda de transporte está duplicada casi
/// línea por línea en los dos archivos. Es el mismo tipo de duplicación
/// deliberada que el bloque AddMassTransit y que <see cref="ProcessedMessage"/>,
/// y de momento **son dos copias, que no son un patrón** (precedente de 2.4). Si
/// 4.6 o la Fase 6 traen un tercer consumer a Orders con la misma cabecera, ahí
/// se decide la extracción con tres diffs delante.
///
/// Y es también lo que estrena de verdad la clave compuesta de
/// <c>ProcessedMessages</c>: por primera vez en el proyecto hay dos
/// <c>ConsumerName</c> distintos escribiendo en la misma tabla.
/// </summary>
public sealed class OrderCancelledConsumer(
    OrdersDbContext db,
    ILogger<OrderCancelledConsumer> logger) : IConsumer<OrderCancelled>
{
    private const string ConsumerName = nameof(OrderCancelledConsumer);

    public async Task Consume(ConsumeContext<OrderCancelled> context)
    {
        var message = context.Message;
        var cancellationToken = context.CancellationToken;

        // Idempotencia de transporte, por MessageId del sobre (3.6). Ver el
        // comentario largo de OrderConfirmedConsumer: aquí se repite la mecánica,
        // no el razonamiento.
        var messageId = context.MessageId
            ?? throw new InvalidOperationException(
                $"El mensaje OrderCancelled del pedido {message.OrderId} llegó sin MessageId en el " +
                "sobre, así que no se puede deducir si es un duplicado. Todo mensaje publicado por " +
                "MassTransit lo lleva; si esto se ve, el mensaje se inyectó a mano sin la propiedad " +
                "message_id.");

        var alreadyProcessed = await db.ProcessedMessages
            .AsNoTracking()
            .AnyAsync(
                processed => processed.MessageId == messageId && processed.ConsumerName == ConsumerName,
                cancellationToken);

        if (alreadyProcessed)
        {
            logger.LogInformation(
                "El mensaje {MessageId} ya lo procesó {ConsumerName} (pedido {OrderId}); se descarta.",
                messageId,
                ConsumerName,
                message.OrderId);

            return;
        }

        var order = await db.Orders
            .FirstOrDefaultAsync(candidate => candidate.Id == message.OrderId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Llegó OrderCancelled del pedido {message.OrderId}, que no existe en OrdersDb.");

        // Idempotencia de negocio, por estado del pedido.
        if (order.Status == OrderStatus.Cancelled)
        {
            logger.LogInformation(
                "El pedido {OrderId} ya estaba en Cancelled; no se vuelve a mover.",
                order.Id);

            MarkProcessed(messageId);
            await db.SaveChangesAsync(cancellationToken);

            return;
        }

        // Sobre un pedido ya confirmado esto lanza, y es lo correcto: sería la saga
        // cancelando algo que OrdersDb da por bueno.
        order.Cancel();

        MarkProcessed(messageId);

        await db.SaveChangesAsync(cancellationToken);

        // El Reason se registra pero **no se guarda**: el pedido no distingue por
        // qué se canceló (decisión de 2.1, repetida en Order.Cancel()), así que el
        // motivo vive en el log y en el evento que lee Notifications (4.6). Es el
        // único sitio de Orders donde se puede leer por qué cayó un pedido, así que
        // conviene que esté en el mensaje del log y no solo en el del evento.
        logger.LogInformation(
            "Pedido {OrderId} cancelado en OrdersDb ({Reason}); su estado pasa de Pending a Cancelled.",
            order.Id,
            message.Reason);
    }

    private void MarkProcessed(Guid messageId) =>
        db.ProcessedMessages.Add(
            new ProcessedMessage(messageId, ConsumerName, typeof(OrderCancelled).FullName!));
}
