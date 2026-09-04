using MassTransit;

using Microsoft.EntityFrameworkCore;

using Notifications.Infrastructure.Entities;
using Notifications.Infrastructure.Persistence;

using Shop133.Contracts.Events;

namespace Notifications.API.Consumers;

/// <summary>
/// El consumer que avisa al cliente de que su pedido salió bien. Escucha el
/// <c>OrderConfirmed</c> que la saga publica desde 4.2 y deja una fila en
/// <c>NotificationsDb.Notifications</c> con el email "enviado".
///
/// Es el primer código que Notifications.API ejecuta en todo el proyecto: hasta
/// 4.6 este servicio era la plantilla <c>webapi</c> intacta, y los <c>///</c> de
/// los dos contratos llevaban prometiendo desde 0.3 que alguien los escuchaba.
///
/// ── Por qué NO se llama OrderConfirmedConsumer ──
///
/// Porque **Orders.API ya tiene una clase con ese nombre** desde 4.3, y el
/// <c>SetKebabCaseEndpointNameFormatter()</c> deriva el nombre de la cola del tipo
/// menos el sufijo <c>Consumer</c>. Dos servicios con el mismo nombre de clase
/// producen la **misma cola**, <c>order-confirmed</c>, y entonces los dos dejan de
/// ser dos suscriptores del fanout para convertirse en **consumidores competidores
/// de una sola cola**: cada evento llega a uno de los dos, al azar. La mitad de los
/// pedidos se quedaría sin mover su <c>Order.Status</c> y la otra mitad sin aviso,
/// sin un solo error en ningún log.
///
/// Con el nombre de aquí la cola es <c>order-confirmed-notification</c>, ligada al
/// mismo exchange <c>Shop133.Contracts.Events:OrderConfirmed</c> que la de Orders.
/// Eso es lo que hace la decisión 2 de 4.1 —la saga observa la coreografía—
/// visible por primera vez con dos servicios distintos sobre el mismo evento.
///
/// **Nada en el repo detecta esa colisión**: los tests de arquitectura leen
/// <c>.csproj</c> y rutas de archivo, no nombres de cola de un broker. Se verifica
/// contra RabbitMQ, mirando que <c>order-confirmed</c> siga teniendo un solo
/// consumidor.
///
/// ── Por qué el "envío" es un log y no un IEmailSender ──
///
/// Porque no hay una segunda implementación y no la habrá en este roadmap. Una
/// interfaz con un único implementador es la abstracción que 4.2 y 4.3 rechazaron
/// dos veces para <c>IOrderWriter</c>, y el precedente directo es Payments, que
/// simula el cobro dentro de su consumer sin inventar un <c>IPaymentGateway</c>.
/// El día que haya un SMTP de verdad, ese día aparece la interfaz con sus dos
/// implementaciones delante.
/// </summary>
public sealed class OrderConfirmedNotificationConsumer(
    NotificationsDbContext db,
    ILogger<OrderConfirmedNotificationConsumer> logger) : IConsumer<OrderConfirmed>
{
    /// <summary>
    /// La mitad de la clave con la que este consumer marca lo que ya procesó.
    /// <c>nameof</c> y no una cadena suelta: renombrar la clase mueve la constante
    /// con ella. Lo que **no** hace es migrar las filas ya escritas con el nombre
    /// viejo, que pasarían a verse como no procesadas — un renombrado de consumer
    /// es un cambio de esquema disfrazado, y aquí además un cambio de nombre de
    /// cola (ver el comentario de la clase).
    /// </summary>
    private const string ConsumerName = nameof(OrderConfirmedNotificationConsumer);

    public async Task Consume(ConsumeContext<OrderConfirmed> context)
    {
        var message = context.Message;
        var cancellationToken = context.CancellationToken;

        // ── Idempotencia de transporte, por MessageId del sobre (3.6) ──
        //
        // Es la regla 6 de CLAUDE.md al pie de la letra: RabbitMQ garantiza *al
        // menos* una entrega, así que se guarda el MessageId procesado y se
        // descarta el repetido. El identificador sale del SOBRE de MassTransit,
        // nunca de un campo del contrato — comprometido en 0.3, 2.1 y 3.2.
        //
        // Sin MessageId no se puede deduplicar, y un consumer que no puede cumplir
        // la regla 6 no debe seguir: revienta y el mensaje acaba en
        // order-confirmed-notification_error, donde se ve.
        var messageId = context.MessageId
            ?? throw new InvalidOperationException(
                $"El mensaje OrderConfirmed del pedido {message.OrderId} llegó sin MessageId en el " +
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

        // ── Idempotencia de negocio, por (OrderId, Kind) ──
        //
        // No es la de arriba y sigue haciendo falta: la de transporte reconoce la
        // misma ENTREGA, ésta reconoce el mismo PEDIDO. Un OrderConfirmed reacuñado
        // con un MessageId nuevo —o republicado a mano— pasa por la de arriba sin
        // enterarse, y sin esta comprobación reventaría el INSERT contra la clave
        // primaria y dejaría un pedido perfectamente terminado en la cola de error.
        //
        // Se sale en silencio, sin reenviar nada, al revés que el duplicado de
        // negocio de Payments (3.5): allí había un desenlace guardado que la saga
        // podía estar esperando; aquí Notifications **no publica nada**, así que no
        // hay nada que reenviar. Es el final de la coreografía.
        var alreadyNotified = await db.Notifications
            .AsNoTracking()
            .AnyAsync(
                notification => notification.OrderId == message.OrderId
                    && notification.Kind == NotificationKind.Confirmation,
                cancellationToken);

        if (alreadyNotified)
        {
            logger.LogInformation(
                "El pedido {OrderId} ya tenía su aviso de confirmación; no se manda un segundo email.",
                message.OrderId);

            // Este camino no manda nada, pero sí procesa el mensaje: se marca para
            // que una reentrega de ESTA entrega ni siquiera llegue a consultar la
            // tabla de negocio.
            MarkProcessed(messageId);
            await db.SaveChangesAsync(cancellationToken);

            return;
        }

        var notification = Notification.Confirmation(message.OrderId, message.CustomerEmail);

        db.Notifications.Add(notification);

        // La marca de 3.6 entra en el MISMO SaveChanges que la notificación, y ésa
        // es toda la razón por la que la guarda vive aquí dentro y no en un filtro
        // de MassTransit envolviendo al consumer. Un filtro confirmaría la marca en
        // una transacción aparte, y entre las dos cabe un estado fatal: mensaje
        // marcado como procesado y email sin mandar, que la reentrega ya no repara
        // porque se lo salta. Así no cabe — o entran las dos cosas o no entra
        // ninguna.
        MarkProcessed(messageId);

        await db.SaveChangesAsync(cancellationToken);

        // ── El "envío" ──
        //
        // Va DESPUÉS del commit y a propósito: un log es la única parte de esto que
        // no se puede deshacer. Si fuera antes y el SaveChanges fallara, la consola
        // diría que el email salió y la tabla diría que no.
        //
        // Aquí no hay agujero de doble escritura como el que 3.3/3.5 anotaron y 4.5
        // cerró con el outbox: lo de después del commit no es un Publish a otro
        // sistema, es una línea de log. Si el proceso muere justo aquí, la fila
        // consta y lo único perdido es su rastro en la consola.
        logger.LogInformation(
            "Email enviado a {Recipient} | Asunto: {Subject}{NewLine}{Body}",
            notification.Recipient,
            notification.Subject,
            Environment.NewLine,
            notification.Body);
    }

    /// <summary>
    /// Deja constancia de que este consumer procesó este mensaje.
    ///
    /// Solo hace <c>Add</c>: **no guarda**. Es deliberado y es lo que permite que
    /// en el camino normal la marca viaje en el mismo <c>SaveChangesAsync</c> que
    /// la notificación. Quien llama decide cuándo se confirma; en la rama del
    /// duplicado de negocio, con un SaveChanges propio inmediatamente después.
    /// </summary>
    private void MarkProcessed(Guid messageId) =>
        db.ProcessedMessages.Add(
            new ProcessedMessage(messageId, ConsumerName, typeof(OrderConfirmed).FullName!));
}
