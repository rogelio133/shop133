using MassTransit;

using Microsoft.EntityFrameworkCore;

using Inventory.Infrastructure.Entities;
using Inventory.Infrastructure.Persistence;

using Shop133.Contracts.Events;

namespace Inventory.API.Consumers;

/// <summary>
/// El primer consumer del proyecto, y con él la primera cola y el primer binding
/// del sistema. Hasta 3.3 el <c>OrderCreated</c> que publicaba Orders.API caía en
/// un exchange fanout sin colas ligadas, o sea al vacío; a partir de aquí, hay
/// alguien al otro lado.
///
/// Reserva el stock del pedido contra <c>InventoryDb</c> y responde con
/// <c>StockReserved</c> o <c>StockRejected</c>. En la Fase 3 esto es
/// **coreografía**: nadie se lo ha mandado, se ha enterado de un hecho. El
/// comando <c>ReserveStock</c> existe en Shop133.Contracts desde 0.3 pero no
/// tiene consumidor hasta que la saga de la Fase 4 lo envíe.
///
/// **La cola se llama <c>order-created</c>** — el nombre lo decide el
/// <c>SetKebabCaseEndpointNameFormatter()</c> que 3.1 dejó puesto con cero
/// consumers precisamente para no tener que cambiarlo hoy y dejar colas
/// huérfanas en el broker.
///
/// Vive en <c>Consumers/</c> y no en <c>Controllers/</c>: un consumer no es un
/// controller (convención de CLAUDE.md), y desde 3.4 lo comprueba el test
/// <c>ConsumerFiles_LiveOnlyIn_ServiceApiConsumersFolder</c>.
///
/// **La lógica está aquí y no en un servicio de Inventory.Infrastructure**, con
/// el mismo criterio con el que <c>ProductsController</c> inyecta
/// <c>CatalogDbContext</c> desde 1.3: las invariantes que importan viven en la
/// entidad (<see cref="StockItem.Reserve"/> no deja bajar de cero), así que un
/// <c>StockReservationService</c> sería un passthrough con una interfaz delante.
/// Y este método *es* el paso de la saga que el proyecto existe para hacer
/// legible; esconderlo detrás de una capa sería enterrar la lección.
/// </summary>
public sealed class OrderCreatedConsumer(
    InventoryDbContext db,
    ILogger<OrderCreatedConsumer> logger) : IConsumer<OrderCreated>
{
    /// <summary>
    /// La mitad de la clave con la que este consumer marca lo que ya procesó.
    /// <c>nameof</c> y no una cadena suelta: renombrar la clase mueve la
    /// constante con ella. Lo que **no** hace es migrar las filas ya escritas con
    /// el nombre viejo, que pasarían a verse como no procesadas — un renombrado
    /// de consumer es un cambio de esquema disfrazado.
    /// </summary>
    private const string ConsumerName = nameof(OrderCreatedConsumer);

    public async Task Consume(ConsumeContext<OrderCreated> context)
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
        // Va delante de la guarda de negocio de más abajo, y las dos se quedan:
        // ésta reconoce la misma ENTREGA, aquélla reconoce el mismo PEDIDO. Un
        // OrderCreated reacuñado con MessageId nuevo para un pedido ya reservado
        // pasa por aquí sin enterarse y lo para la de abajo.
        //
        // Sin MessageId no se puede deduplicar, y un consumer que no puede
        // cumplir la regla 6 no debe seguir: revienta y el mensaje acaba en
        // order-created_error, donde se ve. MassTransit siempre lo rellena, así
        // que esta rama solo la pisa un mensaje escrito a mano — y la receta de
        // reposteo de CLAUDE.md lleva message_id justamente por esto.
        var messageId = context.MessageId
            ?? throw new InvalidOperationException(
                $"El mensaje OrderCreated del pedido {message.OrderId} llegó sin MessageId en el sobre, " +
                "así que no se puede deducir si es un duplicado. Todo mensaje publicado por MassTransit " +
                "lo lleva; si esto se ve, el mensaje se inyectó a mano sin la propiedad message_id.");

        var alreadyProcessed = await db.ProcessedMessages
            .AsNoTracking()
            .AnyAsync(
                processed => processed.MessageId == messageId && processed.ConsumerName == ConsumerName,
                cancellationToken);

        if (alreadyProcessed)
        {
            // Se sale en silencio, sin volver a publicar. Es "skip repeats"
            // literal, y tiene un precio que conviene no disimular: retira el
            // reenvío curativo que la guarda de abajo daba gratis. Si el proceso
            // murió entre el COMMIT y el Publish, la reentrega ya no republica el
            // desenlace y ese mensaje se pierde para siempre.
            //
            // No es un descuido de este punto: es el agujero de la doble
            // escritura, que no tiene arreglo con dos sistemas y sin transacción
            // distribuida, y que cierra el outbox transaccional de 4.5. Un inbox
            // sin outbox se comporta exactamente así.
            logger.LogInformation(
                "El mensaje {MessageId} ya lo procesó {ConsumerName} (pedido {OrderId}); se descarta.",
                messageId,
                ConsumerName,
                message.OrderId);

            return;
        }

        // ── Idempotencia de negocio, por OrderId ──
        //
        // No es la de arriba y sigue haciendo falta: la PK de StockReservations
        // es el OrderId, así que un OrderCreated del mismo pedido con MessageId
        // distinto —la guarda de arriba no lo ve— reventaría el INSERT y, tras
        // los reintentos, acabaría en la cola order-created_error. Un pedido
        // correcto en la cola de errores no es la lección de esta fase.
        //
        // Aquí sí se vuelve a publicar StockReserved en vez de salir en silencio,
        // y la diferencia con la rama de arriba es el motivo: un MessageId nuevo
        // es alguien que ha vuelto a preguntar, no la misma entrega repetida.
        var existing = await db.StockReservations
            .AsNoTracking()
            .FirstOrDefaultAsync(reservation => reservation.OrderId == message.OrderId, cancellationToken);

        if (existing is not null)
        {
            logger.LogInformation(
                "El pedido {OrderId} ya tenía stock reservado (reserva del {CreatedAt}); " +
                "no se reserva de nuevo y se reenvía StockReserved.",
                message.OrderId,
                existing.CreatedAt);

            // Este camino tampoco reserva nada, pero sí procesa el mensaje: se
            // marca antes de publicar para que una reentrega de ESTA entrega ni
            // siquiera llegue a consultar la reserva.
            MarkProcessed(messageId);
            await db.SaveChangesAsync(cancellationToken);

            await PublishReserved(context, message);
            return;
        }

        // Las líneas no traen ProductId repetido: lo garantiza la invariante del
        // constructor de Order, que existe exactamente para que Inventory no
        // tenga que adivinar si dos entradas del mismo producto son una suma o
        // un duplicado (2.1, decisión reafirmada en 3.3). Aquí se confía en ella
        // y no se vuelve a agrupar — hacerlo taparía el día que deje de cumplirse.
        var requestedIds = message.Lines.Select(line => line.ProductId).ToList();

        var stockItems = await db.StockItems
            .Where(item => requestedIds.Contains(item.ProductId))
            .ToDictionaryAsync(item => item.ProductId, cancellationToken);

        // ── Todo o nada ──
        //
        // Lo dice por escrito el /// de StockRejected: "la reserva es atómica, o
        // entra entera o no entra nada. No hay nada que compensar". Por eso se
        // comprueban TODAS las líneas antes de tocar una sola: reservar sobre la
        // marcha y abortar a mitad dejaría unidades comprometidas por un pedido
        // que se va a cancelar — stock filtrado, que es lo que la regla 7 existe
        // para impedir.
        var problems = new List<string>();

        foreach (var line in message.Lines)
        {
            if (!stockItems.TryGetValue(line.ProductId, out var item))
            {
                // Un producto sin fila en StockItems es, para una reserva, un
                // producto que no existe. Esta rama es **la mitad de existencia**
                // del hueco que abrió la decisión 2 de docs/fase_3_3.md al dejar
                // que el cliente mande la foto del pedido: Orders ya no comprueba
                // que el producto exista, y quien lo descubre es este if.
                //
                // Nótese lo que cambió con la coreografía: el pedido no se
                // *rechaza* con un 404, se **cancela** con un evento. El cliente
                // se entera después, no en la respuesta HTTP.
                problems.Add($"el producto {line.ProductId} no existe en el inventario");
                continue;
            }

            if (!item.CanReserve(line.Quantity))
            {
                problems.Add(
                    $"el producto {line.ProductId} tiene {item.QuantityAvailable} " +
                    $"unidad(es) disponible(s) y se piden {line.Quantity}");
            }
        }

        if (problems.Count > 0)
        {
            var reason = string.Join("; ", problems);

            // Reason es texto de diagnóstico y material para el email de 4.6, no
            // un código que nadie deba parsear para decidir (StockRejected lo
            // dice explícitamente). Por eso se juntan todos los problemas: quien
            // lea el mensaje quiere saber qué falló, no el primero que falló.
            logger.LogInformation(
                "Stock rechazado para el pedido {OrderId}: {Reason}.",
                message.OrderId,
                reason);

            // ── El camino que 3.4 dejó sin cubrir ──
            //
            // Hasta 3.6 esta rama no escribía nada: se publicaba StockRejected y
            // se salía sin tocar el ChangeTracker. Eso la dejaba fuera de la
            // guarda de negocio de arriba —que solo sabe de reservas existentes—,
            // así que un OrderCreated reentregado de un pedido sin stock volvía a
            // validar y publicaba un SEGUNDO StockRejected. No reventaba nada
            // porque hoy nadie lo consume; con la saga de 4.3 delante, sí.
            //
            // Ahora la marca es la única escritura de esta rama, y por eso lleva
            // su propio SaveChangesAsync: no hay trabajo de negocio al que
            // engancharla. Es exactamente el hueco que la sección Pendiente de
            // docs/fase_3_4.md apuntó para este punto.
            MarkProcessed(messageId);
            await db.SaveChangesAsync(cancellationToken);

            await context.Publish(
                new StockRejected
                {
                    OrderId = message.OrderId,
                    Reason = reason,
                },
                cancellationToken);

            return;
        }

        foreach (var line in message.Lines)
        {
            stockItems[line.ProductId].Reserve(line.Quantity);
        }

        db.StockReservations.Add(
            new StockReservation(
                message.OrderId,
                message.Lines.Select(line => new StockReservationLine(line.ProductId, line.Quantity))));

        // La marca de 3.6 entra en el MISMO SaveChanges que la reserva, y ésa es
        // toda la razón por la que la guarda vive aquí dentro y no en un filtro
        // de MassTransit envolviendo al consumer. Un filtro confirmaría la marca
        // en una transacción aparte, y entre las dos cabe un estado fatal: mensaje
        // marcado como procesado y trabajo sin hacer, que la reentrega ya no
        // repara porque se lo salta. Así no cabe — o entran las dos cosas o no
        // entra ninguna.
        MarkProcessed(messageId);

        // Un solo SaveChanges para el incremento de los StockItem, el alta de la
        // reserva con sus líneas y la marca: EF los mete en la misma transacción,
        // así que no existe un estado en el que el stock esté comprometido y no
        // haya reserva que lo justifique — que es justo lo que 4.4 necesitaría
        // para soltarlo.
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Stock reservado para el pedido {OrderId}: {LineCount} línea(s) por un importe de {Amount}.",
            message.OrderId,
            message.Lines.Count,
            message.Total);

        await PublishReserved(context, message);
    }

    /// <summary>
    /// Deja constancia de que este consumer procesó este mensaje.
    ///
    /// Solo hace <c>Add</c>: **no guarda**. Es deliberado y es lo que permite que
    /// en el camino de éxito la marca viaje en el mismo <c>SaveChangesAsync</c>
    /// que la reserva. Quien llama decide cuándo se confirma; en las dos ramas
    /// que no escriben nada más, con un SaveChanges propio inmediatamente después.
    ///
    /// Está extraído por lo mismo que <c>PublishReserved</c>: se llama desde los
    /// tres caminos de salida, y tres copias de la misma línea son tres sitios
    /// donde olvidarse de uno — que aquí significa reprocesar un duplicado sin
    /// que nada falle.
    /// </summary>
    private void MarkProcessed(Guid messageId) =>
        db.ProcessedMessages.Add(
            new ProcessedMessage(messageId, ConsumerName, typeof(OrderCreated).FullName!));

    /// <summary>
    /// **La línea que no se puede olvidar.**
    ///
    /// <c>Amount</c> sale de <c>OrderCreated.Total</c> tal cual. A Inventory no
    /// le sirve para nada —guarda cantidades, no importes— y lo transporta
    /// porque Payments no puede preguntárselo a nadie: no puede leer OrdersDb
    /// (regla 1) y en la Fase 3 no hay saga a la que consultar. Esa incomodidad
    /// es la lección de la decisión 1 de docs/fase_3_2.md: en coreografía, el
    /// servicio de en medio acaba acarreando datos ajenos.
    ///
    /// Y es un olvido caro **porque no falla**: sin esta asignación, Amount sale
    /// 0, el pedido se cobra 0 y ninguna prueba de humo lo nota.
    ///
    /// Está extraído a un método porque se publica desde dos sitios —el camino
    /// normal y el del duplicado—, y dos copias de la línea que más importa son
    /// dos sitios donde olvidarse.
    /// </summary>
    private static Task PublishReserved(ConsumeContext<OrderCreated> context, OrderCreated message) =>
        context.Publish(
            new StockReserved
            {
                OrderId = message.OrderId,
                Amount = message.Total,
            },
            context.CancellationToken);
}
