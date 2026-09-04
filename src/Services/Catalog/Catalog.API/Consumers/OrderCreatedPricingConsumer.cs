using Catalog.Infrastructure.Entities;
using Catalog.Infrastructure.Persistence;

using MassTransit;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Shop133.Contracts.Events;

namespace Catalog.API.Consumers;

/// <summary>
/// El primer consumer de Catalog.API, y con él el final de la única isla
/// síncrona del sistema: hasta 4.8 este servicio era el único de los cinco sin
/// MassTransit, alcanzable solo por HTTP.
///
/// Valida la **foto de precios** que viaja dentro de <c>OrderCreated</c> contra
/// <c>CatalogDb</c> y contesta <c>OrderPricingValidated</c> u
/// <c>OrderPricingRejected</c>. Como Inventory en 3.4, esto es **coreografía**:
/// nadie se lo ha mandado, se ha enterado de un hecho.
///
/// Hasta que 4.9 le dé a la saga su estado <c>PricingPending</c>, las dos
/// respuestas se publican **al vacío** — exchange sin colas ligadas, igual que
/// les pasó a <c>StockRejected</c> y <c>PaymentFailed</c> entre 3.4 y 4.3.
///
/// ── EL NOMBRE DE ESTA CLASE ES LA PARTE PELIGROSA DEL PUNTO ──
///
/// Se llama <c>OrderCreatedPricingConsumer</c> y no <c>OrderCreatedConsumer</c>,
/// **rompiendo a propósito la convención del proyecto** ("el consumer se llama
/// como el mensaje que consume"). El motivo: el
/// <c>SetKebabCaseEndpointNameFormatter()</c> deriva el nombre de la cola del
/// nombre del tipo menos el sufijo <c>Consumer</c>, e <c>Inventory.API</c> es
/// dueño de <c>OrderCreatedConsumer</c> —y por tanto de la cola
/// <c>order-created</c>— desde 3.4.
///
/// Con clases homónimas, los dos servicios no serían dos suscriptores del
/// exchange fanout: serían **consumidores COMPETIDORES de una sola cola**, y cada
/// <c>OrderCreated</c> llegaría a uno de los dos al azar. La mitad de los pedidos
/// se quedaría sin validar el precio y la otra mitad sin reservar stock, **sin un
/// solo error en ningún log**. Es exactamente la trampa que 4.6 documentó para
/// Notifications, un servicio después — y allí el peligro era renombrar un
/// consumer, aquí es no renombrarlo.
///
/// Ningún test puede ver esto: los de arquitectura leen <c>.csproj</c> y rutas de
/// archivo, nunca la topología de un broker. Se verifica mirando que
/// <c>order-created</c> siga teniendo **un solo** consumidor, no que las colas
/// existan. Ese hueco es de 8.2 por escrito desde 4.6.
///
/// *Descartado* un <c>.Endpoint(e => e.Name = "order-created-pricing")</c>
/// explícito: dejaría dos clases homónimas en el repositorio y un log que solo
/// imprime el nombre corto. *Descartado* un formatter con prefijo de servicio:
/// dejaría a Catalog con una convención de nombres distinta a la de los otros
/// cuatro, y el problema real —que dos clases homónimas colisionan— seguiría ahí
/// para el siguiente.
///
/// ── Qué agujero cierra, medido ──
///
/// La decisión 2 de docs/fase_3_3.md dejó que el cuerpo del <c>POST /orders</c>
/// traiga el precio, y su corrección 2b admitió que de las dos comprobaciones que
/// se daban por mudadas a Inventory solo se mudó la de **existencia**: Inventory
/// guarda cantidades, no importes. Así que un pedido de un producto que existe a
/// <c>0.01</c> atravesaba la saga entera y **se cobraba un céntimo**, sin que
/// ningún punto del roadmap se enterara. Este consumer es el dueño que le faltaba
/// a ese dato.
///
/// **La lógica está aquí y no en un servicio de Catalog.Infrastructure**, con el
/// mismo criterio con el que <c>ProductsController</c> inyecta
/// <c>CatalogDbContext</c> desde 1.3 y con el que Inventory puso la suya en el
/// consumer en 3.4: la invariante que importa vive en la entidad
/// (<see cref="Product.IsAuthenticPrice"/>), así que un
/// <c>PricingValidationService</c> sería un passthrough con una interfaz delante.
/// </summary>
public sealed class OrderCreatedPricingConsumer(
    CatalogDbContext db,
    IOptions<PricingValidationOptions> options,
    ILogger<OrderCreatedPricingConsumer> logger) : IConsumer<OrderCreated>
{
    /// <summary>
    /// La mitad de la clave con la que este consumer marca lo que ya procesó.
    /// <c>nameof</c> y no una cadena suelta: renombrar la clase mueve la
    /// constante con ella. Lo que **no** hace es migrar las filas ya escritas con
    /// el nombre viejo, que pasarían a verse como no procesadas — un renombrado
    /// de consumer es un cambio de esquema disfrazado.
    ///
    /// Y aquí, además, es un cambio de **topología** disfrazado: ver el aviso
    /// sobre el nombre de la clase más arriba.
    /// </summary>
    private const string ConsumerName = nameof(OrderCreatedPricingConsumer);

    public async Task Consume(ConsumeContext<OrderCreated> context)
    {
        var message = context.Message;
        var cancellationToken = context.CancellationToken;

        // ── Idempotencia de transporte, por MessageId del sobre (3.6) ──
        //
        // Es la regla 6 de CLAUDE.md al pie de la letra. El identificador sale del
        // SOBRE de MassTransit, nunca de un campo del contrato — comprometido en
        // 0.3, 2.1 y 3.2.
        //
        // **Aquí es la ÚNICA guarda que hay, y eso es nuevo en el proyecto.** En
        // los otros cuatro servicios convive con una de negocio que reconoce el
        // mismo PEDIDO en vez de la misma ENTREGA, y todas salieron de una fila que
        // el consumer tenía que escribir de todas formas: la PK de
        // StockReservations (3.4), la fila de Payments (3.5), la de Notifications
        // con su clave (OrderId, Kind) (4.6). Este consumer no escribe nada de
        // negocio —validar precios es una lectura pura—, así que no hay artefacto
        // del que sacar la otra mitad. Ver el /// de ProcessedMessage.
        //
        // Sin MessageId no se puede deduplicar, y un consumer que no puede cumplir
        // la regla 6 no debe seguir: revienta y el mensaje acaba en
        // order-created-pricing_error, donde se ve. MassTransit siempre lo rellena,
        // así que esta rama solo la pisa un mensaje escrito a mano — y la receta de
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
            logger.LogInformation(
                "El mensaje {MessageId} ya lo procesó {ConsumerName} (pedido {OrderId}); se descarta.",
                messageId,
                ConsumerName,
                message.OrderId);

            return;
        }

        var window = options.Value.SnapshotWindow;

        // AsNoTracking porque este consumer no muta nada: es la diferencia de
        // fondo con el de Inventory, que carga los StockItem para llamarles
        // Reserve(). Aquí solo se pregunta.
        var requestedIds = message.Lines.Select(line => line.ProductId).ToList();

        var products = await db.Products
            .AsNoTracking()
            .Where(product => requestedIds.Contains(product.Id))
            .ToDictionaryAsync(product => product.Id, cancellationToken);

        var problems = new List<string>();

        foreach (var line in message.Lines)
        {
            if (!products.TryGetValue(line.ProductId, out var product))
            {
                // Catalog es el dueño de la existencia de un producto. Inventory
                // ya descubre este caso desde 3.4 vía StockItems, pero lo hace en
                // PARALELO —consume el mismo fanout, decisión 2 de 4.1— y contra
                // otra tabla de otra base: un producto puede tener fila en
                // InventoryDb y no existir aquí. No es redundancia, son dos
                // preguntas distintas contestadas por sus dos dueños.
                problems.Add($"el producto {line.ProductId} no existe en el catálogo");
                continue;
            }

            if (!product.IsAuthenticPrice(line.UnitPrice, window))
            {
                // **La rama por la que existe 4.8.** Un pedido de este producto a
                // 0.01 muere aquí, donde antes llegaba a Payments y se cobraba.
                //
                // El mensaje nombra las DOS cifras a propósito: la de la foto y la
                // del catálogo. Sin la segunda, "el precio no es válido" no dice
                // nada a quien lo lea en un log ni a quien lo reciba por email.
                problems.Add(
                    $"el producto {line.ProductId} ({product.Sku}) se pidió a {line.UnitPrice:0.00} " +
                    $"y su precio es {product.Price:0.00}");
            }
        }

        // ── El Total, que es el agujero que NADIE estaba mirando ──
        //
        // OrderCreated.Total es lo que Payments cobra: el /// de StockReserved.Amount
        // dice que se reenvía tal cual desde aquí, y 3.5 lo comprueba contra el
        // umbral y lo persiste. Hasta 4.8 nada verificaba que cuadrara con las
        // líneas, así que un cuerpo con líneas por 1000.00 y un Total de 0.01
        // pasaba entero — un agujero mayor que el del precio unitario, porque no
        // hace falta ni mentir sobre un precio.
        //
        // Se comprueba aquí y no en Orders porque es la misma pregunta que el resto
        // del punto ("¿es cierta esta foto?") y porque Catalog es quien tiene los
        // precios delante. Es aritmética pura: no consulta nada.
        //
        // Se calcula sobre lo que trae el mensaje, NO sobre los precios del
        // catálogo. Recalcular con el precio de hoy convertiría este check en la
        // comparación por igualdad que la decisión 1 de 4.8 descartó, por la puerta
        // de atrás: un pedido con la foto legítima del precio anterior fallaría el
        // total aunque acabara de pasar la validación de arriba.
        var expectedTotal = message.Lines.Sum(line => line.Quantity * line.UnitPrice);

        if (message.Total != expectedTotal)
        {
            problems.Add(
                $"el total del pedido es {message.Total:0.00} y la suma de sus líneas da {expectedTotal:0.00}");
        }

        // ── Qué NO se valida, y hay que leerlo ──
        //
        // **ProductSku y ProductName**, los otros dos campos congelados de
        // OrderLine, no se comparan contra el catálogo. Product.Update puede
        // cambiar el Sku desde 1.3 (decisión 9 de docs/fase_1_1.md: el código de
        // negocio se corrige y se renumera) y renombrar el producto es una
        // operación de catálogo normal, así que compararlos daría falsos rechazos
        // exactamente igual que compararía el precio contra el de hoy — el modo de
        // fallo que la ventana existe para evitar. Y no hacen falta: lo que se
        // cobra es el precio, y lo que se reserva sale del ProductId.
        //
        // **Quantity > 0** tampoco se recomprueba. Lo garantiza el constructor de
        // Order en Orders.Domain, y esta validación es sobre la AUTENTICIDAD de la
        // foto, no un segundo validador de formato del mensaje.
        //
        // Añadir cualquiera de las dos es fácil y por eso se dice por escrito que
        // se decidió no hacerlo.

        // La marca es la ÚNICA escritura de este consumer, así que lleva su propio
        // SaveChangesAsync: no hay trabajo de negocio al que engancharla. Es
        // literalmente la rama de rechazo de OrderCreatedConsumer (3.6, que la
        // estrenó por este mismo motivo) convertida en el consumer entero — y por
        // eso aquí hay un solo punto de marcado en vez de los tres de Inventory.
        //
        // MarkProcessed sigue siendo solo Add, sin guardar, aunque nada viaje con
        // ella: iguala las otras cuatro copias, y quien llama sigue decidiendo
        // cuándo se confirma.
        //
        // El agujero que esto reabre, dicho en voz alta: la marca se confirma ANTES
        // del Publish, así que una muerte entre las dos deja el mensaje marcado y
        // la respuesta sin enviar, y la reentrega se lo salta en silencio. 4.5
        // cerró eso con un outbox transaccional **solo en Orders**, y la nota de
        // 3.6 es explícita en que meterlo en servicios que 4.5 no toca gastaría la
        // decisión antes de tiempo. La consecuencia en 4.9 es concreta: la saga se
        // queda esperando en PricingPending SIN plazo, el mismo hueco sin dueño que
        // arrastra CompensatingStock desde 4.4.
        MarkProcessed(messageId);
        await db.SaveChangesAsync(cancellationToken);

        if (problems.Count > 0)
        {
            // Todos los problemas en una sola cadena, como el Reason de
            // StockRejected: quien lea el mensaje quiere saber qué falló, no cuál
            // falló primero. Texto de diagnóstico y material para el email de
            // Notifications, nunca un código que nadie deba parsear.
            var reason = string.Join("; ", problems);

            logger.LogWarning(
                "Foto de precios rechazada para el pedido {OrderId}: {Reason}.",
                message.OrderId,
                reason);

            await context.Publish(
                new OrderPricingRejected
                {
                    OrderId = message.OrderId,
                    Reason = reason,
                },
                cancellationToken);

            return;
        }

        logger.LogInformation(
            "Foto de precios válida para el pedido {OrderId}: {LineCount} línea(s) por un total de {Total}.",
            message.OrderId,
            message.Lines.Count,
            message.Total);

        await context.Publish(
            new OrderPricingValidated { OrderId = message.OrderId },
            cancellationToken);
    }

    /// <summary>
    /// Deja constancia de que este consumer procesó este mensaje.
    ///
    /// Solo hace <c>Add</c>: **no guarda**. Aquí no hay más escrituras con las que
    /// compartir la transacción, al contrario que en Inventory, pero se mantiene la
    /// forma de las otras cuatro copias — quien llama decide cuándo se confirma.
    /// </summary>
    private void MarkProcessed(Guid messageId) =>
        db.ProcessedMessages.Add(
            new ProcessedMessage(messageId, ConsumerName, typeof(OrderCreated).FullName!));
}
