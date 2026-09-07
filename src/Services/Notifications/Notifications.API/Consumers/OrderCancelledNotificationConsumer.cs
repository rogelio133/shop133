using MassTransit;

using Microsoft.EntityFrameworkCore;

using Notifications.Infrastructure.Entities;
using Notifications.Infrastructure.Persistence;

using Shop133.Contracts.Events;

namespace Notifications.API.Consumers;

/// <summary>
/// El gemelo de <see cref="OrderConfirmedNotificationConsumer"/> para el otro
/// desenlace: escucha el <c>OrderCancelled</c> que la saga publica desde 4.3 por
/// sus dos caminos de error y avisa al cliente de que su pedido no salió adelante.
///
/// **El nombre de la clase no es OrderCancelledConsumer** por lo mismo que su
/// gemelo: Orders.API ya es dueño de la cola <c>order-cancelled</c> desde 4.3, y
/// repetir el nombre convertiría a los dos servicios en consumidores competidores
/// de una sola cola. Ver el comentario largo de
/// <see cref="OrderConfirmedNotificationConsumer"/>; aquí se repite la mecánica,
/// no el razonamiento. Esta cola es <c>order-cancelled-notification</c>.
///
/// ── Un solo tipo de aviso para los dos caminos de error ──
///
/// Este consumer **no distingue** si el pedido cayó por falta de stock
/// (<c>StockRejected</c>) o por un pago rechazado (<c>PaymentFailed</c>, con su
/// compensación de 4.4 ya ejecutada). Es exactamente lo que dice el <c>///</c> de
/// <c>OrderCancelled</c> desde 0.3: "el consumidor no distingue por qué falló:
/// para eso está Reason". Ese texto entra en el cuerpo del email y no se
/// interpreta — es diagnóstico, no un código que nadie deba parsear.
///
/// Descartado partir <see cref="NotificationKind"/> en dos valores según el
/// camino: obligaría a deducir el motivo de un texto libre, que es justo lo que
/// ese campo prohíbe.
/// </summary>
public sealed class OrderCancelledNotificationConsumer(
    NotificationsDbContext db,
    ILogger<OrderCancelledNotificationConsumer> logger) : IConsumer<OrderCancelled>
{
    private const string ConsumerName = nameof(OrderCancelledNotificationConsumer);

    public async Task Consume(ConsumeContext<OrderCancelled> context)
    {
        var message = context.Message;
        var cancellationToken = context.CancellationToken;

        // Idempotencia de transporte, por MessageId del sobre (3.6). Ver el
        // comentario largo de OrderConfirmedNotificationConsumer.
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

        // Idempotencia de negocio, por (OrderId, Kind). Sin ella un OrderCancelled
        // con MessageId nuevo reventaría el INSERT contra la clave primaria.
        var alreadyNotified = await db.Notifications
            .AsNoTracking()
            .AnyAsync(
                notification => notification.OrderId == message.OrderId
                    && notification.Kind == NotificationKind.Cancellation,
                cancellationToken);

        if (alreadyNotified)
        {
            logger.LogInformation(
                "El pedido {OrderId} ya tenía su aviso de cancelación; no se manda un segundo email.",
                message.OrderId);

            MarkProcessed(messageId);
            await db.SaveChangesAsync(cancellationToken);

            return;
        }

        var notification = Notification.Cancellation(
            message.OrderId,
            message.CustomerEmail,
            message.Reason);

        db.Notifications.Add(notification);

        MarkProcessed(messageId);

        await db.SaveChangesAsync(cancellationToken);

        // El Reason se registra aparte del cuerpo aunque vaya dentro de él: en la
        // consola es lo único que distingue una cancelación por falta de stock de
        // una por pago rechazado, y buscarlo dentro de un email de varias líneas es
        // peor que tenerlo en su propia propiedad estructurada.
        logger.LogInformation(
            "Email enviado a {Recipient} | Asunto: {Subject} | Motivo: {Reason}{NewLine}{Body}",
            notification.Recipient,
            notification.Subject,
            message.Reason,
            Environment.NewLine,
            notification.Body);
    }

    private void MarkProcessed(Guid messageId) =>
        db.ProcessedMessages.Add(
            new ProcessedMessage(messageId, ConsumerName, typeof(OrderCancelled).FullName!));
}
