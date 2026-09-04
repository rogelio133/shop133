using MassTransit;

using Microsoft.EntityFrameworkCore;

using Orders.Domain.Entities;
using Orders.Infrastructure.Entities;
using Orders.Infrastructure.Persistence;

using Shop133.Contracts.Events;

namespace Orders.API.Consumers;

/// <summary>
/// El primer consumer de Orders.API, y el que cierra la inconsistencia que 4.2
/// dejó medida: la saga llegaba a <c>Confirmed</c> mientras
/// <c>GET /orders/{id}</c> seguía contestando <c>"Pending"</c>, para siempre.
///
/// ── Por qué esto es un consumer y no una línea dentro de la saga ──
///
/// Porque la <c>OrderStateMachine</c> vive en Orders.Domain y **no puede tocar
/// <c>OrdersDbContext</c>**: la flecha de dependencias va .API →
/// .Infrastructure → .Domain (regla 5 de CLAUDE.md), y el dominio no ve la
/// persistencia. Así que entre "la saga terminó" y "la fila cambió" hay
/// obligatoriamente un mensaje y una cola.
///
/// *Descartado* un puerto (<c>IOrderWriter</c> declarado en Orders.Domain e
/// implementado en Orders.Infrastructure): respetaría la regla igual y ahorraría
/// este archivo, pero cambiaría una interfaz con un método y una implementación
/// por el mecanismo que el proyecto ya usa en todas partes, y que además hace
/// visible la ventana de inconsistencia en vez de esconderla detrás de una
/// llamada que parece síncrona. Este servicio es de mensajería; que el pedido se
/// entere por un mensaje es la respuesta coherente.
///
/// **Servicio propio ni servicio en Orders.Infrastructure**: misma decisión que
/// <c>OrderCreatedConsumer</c> (3.4) y <c>StockReservedConsumer</c> (3.5). Las
/// invariantes viven en la entidad —<c>Order.Confirm()</c> es quien se niega a
/// mover un estado final—, así que un <c>OrderService</c> sería un passthrough
/// con una interfaz delante.
///
/// La cola se llama <c>order-confirmed</c>: la nombra el
/// <c>SetKebabCaseEndpointNameFormatter()</c> que 3.1 dejó puesto con cero
/// consumers precisamente para no tener que cambiarlo hoy.
///
/// **Este consumer no publica nada.** Es el final de la cadena, no un eslabón —
/// a diferencia de Inventory y Payments, que contestan al evento que reciben. Lo
/// que sigue después de <c>OrderConfirmed</c> es Notifications.API (4.6), que
/// escucha el mismo evento por su cuenta: dos consumidores del mismo fanout, no
/// un relevo.
/// </summary>
public sealed class OrderConfirmedConsumer(
    OrdersDbContext db,
    ILogger<OrderConfirmedConsumer> logger) : IConsumer<OrderConfirmed>
{
    /// <summary>
    /// La mitad de la clave con la que este consumer marca lo que ya procesó.
    /// <c>nameof</c> y no una cadena suelta: renombrar la clase mueve la
    /// constante con ella. Lo que **no** hace es migrar las filas ya escritas con
    /// el nombre viejo, que pasarían a verse como no procesadas — un renombrado
    /// de consumer es un cambio de esquema disfrazado.
    /// </summary>
    private const string ConsumerName = nameof(OrderConfirmedConsumer);

    public async Task Consume(ConsumeContext<OrderConfirmed> context)
    {
        var message = context.Message;
        var cancellationToken = context.CancellationToken;

        // ── Idempotencia de transporte, por MessageId del sobre (3.6) ──
        //
        // RabbitMQ garantiza *al menos* una entrega, así que se guarda el
        // MessageId procesado y se descarta el repetido. El identificador sale del
        // SOBRE de MassTransit, nunca de un campo del contrato — comprometido en
        // 0.3, 2.1 y 3.2.
        //
        // Sin MessageId no se puede deduplicar, y un consumer que no puede cumplir
        // la regla 6 no debe seguir: revienta y el mensaje acaba en
        // order-confirmed_error, donde se ve.
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

        var order = await db.Orders
            .FirstOrDefaultAsync(candidate => candidate.Id == message.OrderId, cancellationToken)

            // Se revienta a propósito en vez de salir en silencio. Un desenlace de
            // un pedido que no está en OrdersDb es una incoherencia de verdad:
            // este servicio es el dueño de esa tabla y el evento lo publicó su
            // propia saga. Hoy es alcanzable a mano (un OrderConfirmed reacuñado
            // con un OrderId inventado, como en las pruebas de 3.6) y, hasta 4.5,
            // también reiniciando el servicio a mitad de saga. En la cola de error
            // se ve y se cuenta; absorbido, no.
            ?? throw new InvalidOperationException(
                $"Llegó OrderConfirmed del pedido {message.OrderId}, que no existe en OrdersDb.");

        // ── Idempotencia de negocio, por estado del pedido ──
        //
        // No es la de arriba y hace falta igual: un OrderConfirmed reacuñado con
        // MessageId nuevo —o el mismo pedido resuelto dos veces por la saga— pasa
        // por la guarda de transporte sin enterarse y llegaría a Order.Confirm(),
        // que lanza sobre un estado final. Aquí se reconoce el mismo PEDIDO;
        // arriba, la misma ENTREGA.
        //
        // A diferencia de Payments (3.5), este camino **no reenvía nada**: no hay
        // desenlace que republicar porque este consumer no publica: quien anuncia
        // el final es la saga.
        if (order.Status == OrderStatus.Confirmed)
        {
            logger.LogInformation(
                "El pedido {OrderId} ya estaba en Confirmed; no se vuelve a mover.",
                order.Id);

            // Este camino no mueve el pedido, pero sí procesa el mensaje: se marca
            // igual, para que una reentrega de ESTA entrega ni siquiera llegue a
            // consultar el pedido.
            MarkProcessed(messageId);
            await db.SaveChangesAsync(cancellationToken);

            return;
        }

        // Si el pedido está en Cancelled, esto lanza y el mensaje acaba en la cola
        // de error. Es lo correcto: significaría que la saga confirmó un pedido que
        // OrdersDb da por cancelado, y eso no es un duplicado, es una contradicción.
        order.Confirm();

        // La marca de 3.6 entra en el MISMO SaveChanges que el cambio de estado, y
        // ésa es toda la razón por la que la guarda vive aquí dentro y no en un
        // filtro de MassTransit envolviendo al consumer. Un filtro confirmaría la
        // marca en una transacción aparte, y entre las dos cabe un estado fatal:
        // mensaje marcado como procesado y pedido sin confirmar, que la reentrega
        // ya no repara porque se lo salta. Así no cabe — o entran las dos cosas o
        // no entra ninguna.
        MarkProcessed(messageId);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Pedido {OrderId} confirmado en OrdersDb; su estado pasa de Pending a Confirmed.",
            order.Id);
    }

    /// <summary>
    /// Deja constancia de que este consumer procesó este mensaje.
    ///
    /// Solo hace <c>Add</c>: **no guarda**. Es deliberado y es lo que permite que
    /// la marca viaje en el mismo <c>SaveChangesAsync</c> que el cambio de estado.
    /// Quien llama decide cuándo se confirma.
    /// </summary>
    private void MarkProcessed(Guid messageId) =>
        db.ProcessedMessages.Add(
            new ProcessedMessage(messageId, ConsumerName, typeof(OrderConfirmed).FullName!));
}
