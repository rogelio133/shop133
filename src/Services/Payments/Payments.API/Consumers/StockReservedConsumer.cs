using MassTransit;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Payments.Infrastructure.Entities;
using Payments.Infrastructure.Persistence;

using Shop133.Contracts.Events;

namespace Payments.API.Consumers;

/// <summary>
/// El segundo consumer del proyecto, y el que cierra la cadena de coreografía de
/// la Fase 3: <c>POST /orders</c> → <c>OrderCreated</c> → <c>StockReserved</c> →
/// <c>PaymentCompleted</c>/<c>PaymentFailed</c>, sin que ningún servicio llame a
/// otro por HTTP. Hasta 3.5 el <c>StockReserved</c> que publicaba Inventory caía
/// en un exchange sin colas ligadas, o sea al vacío.
///
/// **La cola se llama <c>stock-reserved</c>** — el nombre lo decide el
/// <c>SetKebabCaseEndpointNameFormatter()</c> que 3.1 dejó puesto con cero
/// consumers precisamente para no tener que cambiarlo hoy y dejar colas
/// huérfanas en el broker.
///
/// Vive en <c>Consumers/</c> y no en <c>Controllers/</c>: un consumer no es un
/// controller (convención de CLAUDE.md), y desde 3.4 lo comprueba el test
/// <c>ConsumerFiles_LiveOnlyIn_ServiceApiConsumersFolder</c>. Aquí importa más
/// que en Inventory, porque Payments.API **no tiene ni un endpoint HTTP**: todo
/// lo que este servicio hace, lo hace desde este archivo.
///
/// **La lógica está aquí y no en un servicio de Payments.Infrastructure**, con el
/// mismo criterio que <c>OrderCreatedConsumer</c> en 3.4 y que
/// <c>ProductsController</c> desde 1.3: las invariantes que importan viven en la
/// entidad (las dos factorías de <see cref="Payment"/> impiden una fila
/// incoherente), así que un <c>PaymentService</c> sería un passthrough con una
/// interfaz delante. Y este método *es* el paso de la saga que el proyecto existe
/// para hacer legible.
///
/// ── De dónde sale el importe ──
///
/// De <c>StockReserved.Amount</c>, y de ningún otro sitio. Payments no puede leer
/// OrdersDb —regla 1, y desde 0.4 lo impide SQL Server: <c>payments_user</c> no
/// tiene permiso— y en la Fase 3 no hay saga a la que preguntar. Este consumer es
/// la comprobación de que la decisión 1 de docs/fase_3_2.md era correcta al
/// añadirle ese campo al evento. Si Inventory se olvidara de rellenarlo, aquí
/// llegaría 0 y el pedido se cobraría a cero sin que nada fallara.
/// </summary>
public sealed class StockReservedConsumer(
    PaymentsDbContext db,
    IOptions<PaymentSimulationOptions> options,
    ILogger<StockReservedConsumer> logger) : IConsumer<StockReserved>
{
    /// <summary>
    /// La mitad de la clave con la que este consumer marca lo que ya procesó.
    /// <c>nameof</c> y no una cadena suelta: renombrar la clase mueve la
    /// constante con ella. Lo que **no** hace es migrar las filas ya escritas con
    /// el nombre viejo, que pasarían a verse como no procesadas — un renombrado
    /// de consumer es un cambio de esquema disfrazado.
    /// </summary>
    private const string ConsumerName = nameof(StockReservedConsumer);

    public async Task Consume(ConsumeContext<StockReserved> context)
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
        // StockReserved reacuñado con MessageId nuevo para un pedido ya cobrado
        // pasa por aquí sin enterarse y lo para la de abajo — que es la que
        // impide el duplicado más caro del sistema.
        //
        // Sin MessageId no se puede deduplicar, y un consumer que no puede
        // cumplir la regla 6 no debe seguir: revienta y el mensaje acaba en
        // stock-reserved_error, donde se ve. Aquí importa más que en Inventory —
        // el trabajo que hay al otro lado es cobrar.
        var messageId = context.MessageId
            ?? throw new InvalidOperationException(
                $"El mensaje StockReserved del pedido {message.OrderId} llegó sin MessageId en el sobre, " +
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
            // desenlace — el cobro está hecho y la saga no se entera nunca.
            //
            // No es un descuido de este punto: es el mismo agujero de la doble
            // escritura anotado más abajo, que cierra el outbox transaccional de
            // 4.5. Un inbox sin outbox se comporta exactamente así.
            logger.LogInformation(
                "El mensaje {MessageId} ya lo procesó {ConsumerName} (pedido {OrderId}); se descarta.",
                messageId,
                ConsumerName,
                message.OrderId);

            return;
        }

        // ── Idempotencia de negocio, por OrderId ──
        //
        // No es la de arriba y sigue haciendo falta, aquí más que en ningún otro
        // sitio del sistema: la PK de Payments es el OrderId, así que un
        // StockReserved del mismo pedido con MessageId distinto —la guarda de
        // arriba no lo ve— cobraría el pedido dos veces sin esta comprobación, o
        // reventaría el INSERT. Es el duplicado más caro que el proyecto puede
        // producir.
        //
        // Aquí sí se reenvía el desenlace guardado en vez de salir en silencio, y
        // la diferencia con la rama de arriba es el motivo: un MessageId nuevo es
        // alguien que ha vuelto a preguntar, no la misma entrega repetida.
        var existing = await db.Payments
            .AsNoTracking()
            .FirstOrDefaultAsync(payment => payment.OrderId == message.OrderId, cancellationToken);

        if (existing is not null)
        {
            logger.LogInformation(
                "El pedido {OrderId} ya se había cobrado el {ProcessedAt} con resultado {Status}; " +
                "no se vuelve a cobrar y se reenvía el desenlace guardado.",
                message.OrderId,
                existing.ProcessedAt,
                existing.Status);

            // Este camino no cobra nada, pero sí procesa el mensaje: se marca
            // antes de publicar para que una reentrega de ESTA entrega ni
            // siquiera llegue a consultar el cobro.
            MarkProcessed(messageId);
            await db.SaveChangesAsync(cancellationToken);

            await Republish(context, existing);
            return;
        }

        // ── La pasarela simulada ──
        //
        // Determinista y en función del importe: ver el /// de
        // PaymentSimulationOptions.DeclineAmountAbove para por qué no es un
        // porcentaje aleatorio. El orden de las dos comprobaciones importa poco,
        // pero la de importe no positivo va primero porque describe mejor lo que
        // pasa: un importe de 0 no es un cobro "demasiado grande", es un cobro
        // que no tiene sentido.
        var threshold = options.Value.DeclineAmountAbove;

        string? declineReason = null;

        if (message.Amount <= 0m)
        {
            // Alcanzable hoy, y no es teoría: la decisión 2 de docs/fase_3_3.md
            // dejó que el cuerpo del POST traiga el precio, así que un cliente
            // puede pedir a 0. Nadie comprueba la autenticidad de esa foto hasta
            // 4.8 — este if no la comprueba tampoco, solo se niega a cobrar el
            // caso más obvio.
            declineReason =
                $"el importe {message.Amount:0.00} no es cobrable; un pedido tiene que valer algo";
        }
        else if (message.Amount > threshold)
        {
            declineReason =
                $"el importe {message.Amount:0.00} supera el límite autorizado de {threshold:0.00}";
        }

        var payment = declineReason is null
            ? Payment.Completed(message.OrderId, message.Amount, NewTransactionId())
            : Payment.Declined(message.OrderId, message.Amount, declineReason);

        db.Payments.Add(payment);

        // La marca de 3.6 entra en el MISMO SaveChanges que el cobro, y ésa es
        // toda la razón por la que la guarda vive aquí dentro y no en un filtro
        // de MassTransit envolviendo al consumer. Un filtro confirmaría la marca
        // en una transacción aparte, y entre las dos cabe un estado fatal: mensaje
        // marcado como procesado y cobro sin hacer, que la reentrega ya no repara
        // porque se lo salta. Así no cabe — o entran las dos cosas o no entra
        // ninguna.
        MarkProcessed(messageId);

        // ── Guardar primero, publicar después ──
        //
        // Mismo orden y mismo motivo que en OrdersController desde 3.3: publicar
        // antes dejaría a la saga confirmando un pedido cuyo cobro no consta en
        // ningún sitio, y sin constancia no hay forma de devolverlo.
        //
        // El precio de este orden es el agujero de la doble escritura: si el
        // proceso muere entre el COMMIT y el Publish, el cobro está hecho y nadie
        // se entera nunca. No tiene arreglo con dos sistemas y sin transacción
        // distribuida; lo cierra el outbox transaccional de 4.5.
        await db.SaveChangesAsync(cancellationToken);

        if (declineReason is null)
        {
            logger.LogInformation(
                "Cobro aceptado para el pedido {OrderId} por {Amount}, transacción {TransactionId}.",
                payment.OrderId,
                payment.Amount,
                payment.TransactionId);
        }
        else
        {
            logger.LogInformation(
                "Cobro rechazado para el pedido {OrderId} por {Amount}: {Reason}.",
                payment.OrderId,
                payment.Amount,
                declineReason);
        }

        await Republish(context, payment);
    }

    /// <summary>
    /// Deja constancia de que este consumer procesó este mensaje.
    ///
    /// Solo hace <c>Add</c>: **no guarda**. Es deliberado y es lo que permite que
    /// en el camino normal la marca viaje en el mismo <c>SaveChangesAsync</c> que
    /// el cobro. Quien llama decide cuándo se confirma; en la rama del duplicado
    /// de negocio, con un SaveChanges propio inmediatamente después.
    ///
    /// Está extraído por lo mismo que <c>Republish</c>: se llama desde los dos
    /// caminos de salida, y dos copias de la misma línea son dos sitios donde
    /// olvidarse de una — que aquí significa reprocesar un duplicado sin que nada
    /// falle.
    /// </summary>
    private void MarkProcessed(Guid messageId) =>
        db.ProcessedMessages.Add(
            new ProcessedMessage(messageId, ConsumerName, typeof(StockReserved).FullName!));

    /// <summary>
    /// Publica el desenlace de un cobro ya persistido.
    ///
    /// Está extraído a un método por lo mismo que <c>PublishReserved</c> en 3.4:
    /// se publica desde dos sitios —el camino normal y el del duplicado— y dos
    /// copias de la línea que más importa son dos sitios donde equivocarse.
    ///
    /// **Y lo que hace que esto tenga que salir de la fila y no de la variable
    /// local**: en el camino del duplicado, el <c>TransactionId</c> que se reenvía
    /// es el que ya estaba guardado. Acuñar uno nuevo daría dos identificadores
    /// para un mismo cobro, que es exactamente lo que la tabla viene a impedir.
    ///
    /// <c>PaymentFailed</c> y <c>PaymentCompleted</c> no tienen consumidor todavía
    /// —la saga llega en 4.2/4.3—, así que en 3.5 se publican a un exchange sin
    /// colas ligadas: no falla ni avisa. En el caso del rechazo eso significa que
    /// **el stock reservado se queda reservado**, y ese hueco es la Fase 4 en una
    /// frase.
    /// </summary>
    private static Task Republish(ConsumeContext<StockReserved> context, Payment payment) =>
        payment.Status == PaymentStatus.Completed
            ? context.Publish(
                new PaymentCompleted
                {
                    OrderId = payment.OrderId,
                    Amount = payment.Amount,

                    // No puede ser null en una fila Completed: lo garantiza la
                    // factoría Payment.Completed, que es el único camino para
                    // crearla. El operador está para el compilador, que no lo
                    // sabe, no para un caso real.
                    TransactionId = payment.TransactionId!,
                },
                context.CancellationToken)
            : context.Publish(
                new PaymentFailed
                {
                    OrderId = payment.OrderId,
                    Reason = payment.FailureReason!,
                },
                context.CancellationToken);

    /// <summary>
    /// El identificador del cobro en la pasarela. Simulado, con prefijo
    /// <c>SIM-</c> visible: en un log tiene que verse de un vistazo que no viene
    /// de ninguna pasarela real. El día que haya una de verdad, el identificador
    /// lo devuelve ella y este método desaparece.
    /// </summary>
    private static string NewTransactionId() =>
        $"SIM-{Guid.NewGuid():N}".ToUpperInvariant();
}
