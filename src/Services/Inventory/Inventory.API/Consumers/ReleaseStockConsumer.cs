using MassTransit;

using Microsoft.EntityFrameworkCore;

using Inventory.Infrastructure.Entities;
using Inventory.Infrastructure.Persistence;

using Shop133.Contracts.Commands;
using Shop133.Contracts.Events;

namespace Inventory.API.Consumers;

/// <summary>
/// La compensación: devuelve las unidades que este servicio había comprometido
/// para un pedido cuyo cobro se rechazó, y contesta <c>StockReleased</c>.
///
/// **Es el punto por el que existe el proyecto** — la línea del checklist del
/// roadmap que dice "puedes forzar un fallo de pago y ver la compensación liberar
/// el stock sin intervención manual". Hasta 4.4, un pedido cancelado por pago
/// rechazado se quedaba con sus unidades reservadas para siempre: medido en la
/// verificación de docs/fase_3_5.md, y la regla 7 de CLAUDE.md sin cumplir.
///
/// **Es el segundo consumer de Inventory, y el primero que consume un COMANDO.**
/// Los otros cuatro del proyecto reaccionan a hechos consumados; a éste se le
/// manda hacer algo, y quien se lo manda es la <c>OrderStateMachine</c> con un
/// <c>Send</c> a <c>queue:release-stock</c>. Esa es la única dirección escrita a
/// mano del proyecto, y el nombre de esta cola sale de aplicar
/// <c>SetKebabCaseEndpointNameFormatter()</c> a esta clase: **renombrarla rompe el
/// envío en silencio**, porque MassTransit crearía la cola nueva y los comandos se
/// apilarían en la vieja sin que nada fallase.
///
/// Al ser el segundo consumer del servicio, es también el primero que le da a la
/// tabla <c>ProcessedMessages</c> dos <c>ConsumerName</c> distintos — la clave
/// primaria compuesta que 3.6 puso "por si acaso" deja aquí de ser hipotética.
///
/// **Y la idempotencia importa más aquí que en ningún otro consumer.** Lo avisa el
/// <c>///</c> del propio comando: soltar dos veces *crea unidades de la nada*, que
/// es peor que un duplicado de reserva —ése solo bloquea de más—. Por eso hay tres
/// defensas superpuestas y no una: la guarda de transporte por MessageId, la de
/// negocio por <c>ReleasedAt</c>, y el <c>throw</c> de
/// <see cref="StockItem.Release"/> si alguien pidiera soltar más de lo reservado.
/// </summary>
public sealed class ReleaseStockConsumer(
    InventoryDbContext db,
    ILogger<ReleaseStockConsumer> logger) : IConsumer<ReleaseStock>
{
    /// <summary>
    /// La otra mitad de la clave de <c>ProcessedMessages</c>. Misma constante y
    /// mismo <c>nameof</c> que en <see cref="OrderCreatedConsumer"/>, y con la
    /// misma advertencia: renombrar la clase es un cambio de esquema disfrazado,
    /// porque las filas escritas con el nombre viejo dejan de encontrarse y todo
    /// mensaje ya procesado vuelve a parecer nuevo.
    /// </summary>
    private const string ConsumerName = nameof(ReleaseStockConsumer);

    public async Task Consume(ConsumeContext<ReleaseStock> context)
    {
        var message = context.Message;
        var cancellationToken = context.CancellationToken;

        // ── Idempotencia de transporte, por MessageId del sobre (3.6) ──
        //
        // Copia literal de la de OrderCreatedConsumer, y la tercera del proyecto
        // (la cuarta y la quinta están en Orders desde 4.3). Sigue sin extraerse: los
        // tres .Infrastructure tienen cero ProjectReference entre sí y no existe un
        // proyecto de infraestructura común, así que compartirla exigiría crearlo.
        //
        // Sin MessageId no se puede deduplicar, y aquí eso sería especialmente
        // grave, así que se revienta: el mensaje va a release-stock_error, que es
        // visible, en vez de soltar stock sin poder saber si ya se soltó.
        var messageId = context.MessageId
            ?? throw new InvalidOperationException(
                $"El comando ReleaseStock del pedido {message.OrderId} llegó sin MessageId en el sobre, " +
                "así que no se puede deducir si es un duplicado. Todo mensaje publicado por MassTransit " +
                "lo lleva; si esto se ve, el mensaje se inyectó a mano sin la propiedad message_id.");

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

        // ── De dónde salen las unidades a devolver ──
        //
        // Del pedido, no del mensaje: ReleaseStock solo lleva el OrderId desde 4.4,
        // porque la PK de esta tabla *es* el OrderId. Un SELECT por clave primaria
        // trae la reserva **con sus líneas y sin Include**, que son un tipo owned
        // desde 3.4.
        //
        // CON tracking, al revés que las lecturas de las guardas: hay que mutarla.
        var reservation = await db.StockReservations
            .FirstOrDefaultAsync(candidate => candidate.OrderId == message.OrderId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Llegó ReleaseStock del pedido {message.OrderId}, que no tiene reserva en InventoryDb. " +
                "La saga solo envía este comando desde PaymentPending, al que únicamente se llega por el " +
                "StockReserved que publicó este mismo servicio, así que la fila tendría que existir.");

        // Se revienta en vez de salir en silencio, con el mismo criterio con el que
        // los consumers de Orders revientan ante un pedido que no está en OrdersDb
        // (4.3): Inventory es el dueño de esta tabla y fue él quien publicó el
        // StockReserved que puso en marcha todo esto, así que no encontrar la fila
        // es una incoherencia real, no un caso de negocio.
        //
        // El precio hay que decirlo: el mensaje se queda en release-stock_error y
        // **la saga se queda esperando en CompensatingStock**, sin plazo que la
        // saque. Es el desenlace honesto de una incoherencia — visible en una cola
        // de error, en vez de tapado con un StockReleased que mentiría diciendo que
        // se soltó algo que nunca se reservó.

        // ── Idempotencia de negocio, por ReleasedAt ──
        //
        // La hermana de la de OrderCreatedConsumer, que va por la existencia de la
        // fila. Aquí no puede ir por ahí —la fila existe siempre— así que va por el
        // sello. Y es lo que hace que **borrar la reserva al liberarla** fuera mala
        // idea: sin fila no habría diferencia entre "ya se liberó" y "nunca se
        // reservó", que son el caso normal y la incoherencia de arriba.
        //
        // Se vuelve a publicar StockReleased en vez de salir en silencio, por el
        // mismo motivo que la rama equivalente del otro consumer: un MessageId nuevo
        // es alguien que ha vuelto a preguntar, y lo que se perdió pudo ser la
        // respuesta. Aquí importa más que allí, porque quien espera esa respuesta es
        // una saga que sin ella no sale de CompensatingStock.
        if (reservation.ReleasedAt is not null)
        {
            logger.LogInformation(
                "El stock del pedido {OrderId} ya se había liberado el {ReleasedAt}; " +
                "no se devuelve nada y se reenvía StockReleased.",
                message.OrderId,
                reservation.ReleasedAt);

            MarkProcessed(messageId);
            await db.SaveChangesAsync(cancellationToken);

            await PublishReleased(context, message);
            return;
        }

        var reservedIds = reservation.Lines.Select(line => line.ProductId).ToList();

        var stockItems = await db.StockItems
            .Where(item => reservedIds.Contains(item.ProductId))
            .ToDictionaryAsync(item => item.ProductId, cancellationToken);

        // No hay pasada de validación "todo o nada" como en la reserva, y no es un
        // olvido: al reservar podía faltar stock, que es un caso de negocio con su
        // StockRejected. Aquí no hay ningún caso de negocio posible — las unidades
        // que se devuelven son exactamente las que este servicio comprometió, así
        // que cualquier cosa que impida devolverlas es una incoherencia entre dos
        // tablas del mismo servicio. Por eso todo lo que puede fallar aquí lanza.
        foreach (var line in reservation.Lines)
        {
            if (!stockItems.TryGetValue(line.ProductId, out var item))
            {
                throw new InvalidOperationException(
                    $"La reserva del pedido {message.OrderId} tiene una línea del producto {line.ProductId}, " +
                    "que no existe en StockItems. Nada borra filas de esa tabla, así que la reserva y el " +
                    "inventario están incoherentes.");
            }

            // Y aquí la tercera defensa: Release lanza si se pidieran más unidades
            // de las reservadas, porque soltar de más las crea de la nada.
            item.Release(line.Quantity);
        }

        // El sello de la reserva y la devolución de las unidades tienen que ser el
        // mismo hecho. Release() lanza si ya estaba sellada — la guarda de arriba es
        // quien debe haber parado el duplicado, y que esto salte significa que no lo
        // hizo.
        reservation.Release();

        // La marca de 3.6 entra en el MISMO SaveChanges que la devolución, por lo
        // mismo que en el otro consumer: un filtro de MassTransit confirmaría la
        // marca en otra transacción, y entre las dos cabe el estado fatal de
        // "marcado como procesado y sin hacer" — que la reentrega ya no repara,
        // porque se lo salta.
        MarkProcessed(messageId);

        // Un solo SaveChanges para los QuantityReserved, el ReleasedAt y la marca:
        // no existe un estado intermedio en el que las unidades estén devueltas y la
        // reserva siga viva, que es el que permitiría soltarlas dos veces.
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Stock liberado para el pedido {OrderId}: {LineCount} línea(s) devuelta(s) al inventario.",
            message.OrderId,
            reservation.Lines.Count);

        await PublishReleased(context, message);
    }

    private void MarkProcessed(Guid messageId) =>
        db.ProcessedMessages.Add(
            new ProcessedMessage(messageId, ConsumerName, typeof(ReleaseStock).FullName!));

    /// <summary>
    /// La respuesta, y va con <c>Publish</c> aunque lo que la provocó viniera con
    /// <c>Send</c>: <c>StockReleased</c> es un evento —un hecho consumado— y quien
    /// lo correlaciona es la cola <c>order-state</c> de la saga, ligada al exchange
    /// por convención. Contestar por el <c>ResponseAddress</c> del sobre habría
    /// hecho falta con un request/response, que no es la forma de este flujo.
    ///
    /// Extraído por lo mismo que <c>PublishReleased</c>'s hermano en el otro
    /// consumer: se publica desde dos caminos, el normal y el del duplicado, y
    /// olvidarse en el segundo dejaría a la saga esperando para siempre.
    /// </summary>
    private static Task PublishReleased(ConsumeContext<ReleaseStock> context, ReleaseStock message) =>
        context.Publish(
            new StockReleased { OrderId = message.OrderId },
            context.CancellationToken);
}
