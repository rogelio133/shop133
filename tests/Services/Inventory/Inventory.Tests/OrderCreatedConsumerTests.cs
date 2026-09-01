using Inventory.API.Consumers;
using Inventory.Tests.Infrastructure;

using MassTransit;
using MassTransit.Testing;

using Shop133.Contracts;
using Shop133.Contracts.Events;
using Shop133.TestUtilities;

using Xunit;

namespace Inventory.Tests;

/// <summary>
/// <see cref="OrderCreatedConsumer"/> — el consumer de 3.4, con la guarda de
/// idempotencia que le añadió 3.6.
///
/// Todo lo que hay aquí se verificó a mano en su día contra un RabbitMQ y una
/// base reales (secciones de Verificación de docs/fase_3_4.md y docs/fase_3_6.md).
/// Esto es lo mismo, automatizado: colas espía sustituidas por
/// <c>harness.Published</c>, y los <c>SELECT</c> por los helpers del host.
///
/// **Los asserts miran los eventos publicados, no solo la base.** Es la
/// conclusión que dejó medida docs/fase_3_6.md: cuando un duplicado se descarta,
/// el estado de la base queda **idéntico** a si se hubiera reprocesado, así que
/// la única diferencia observable es cuántos eventos salieron. Un test que solo
/// consultara <c>StockItems</c> pasaría en verde con la idempotencia rota.
///
/// Las cantidades del seed de 3.4 que se usan aquí: producto 1 → 42 unidades,
/// producto 2 → 65, producto 9 → 12.
/// </summary>
[Collection(InventoryConsumerCollection.Name)]
[Trait("Category", "Docker")]
public sealed class OrderCreatedConsumerTests(SqlServerContainerFixture container) : IAsyncLifetime
{
    private const string CustomerEmail = "cliente@shop133.test";
    private const int MugId = 1;
    private const int MugOnHand = 42;
    private const int KeyringId = 2;
    private const int ScarceProductId = 9;
    private const int ScarceOnHand = 12;
    private const int UnknownProductId = 999_999;

    private readonly InventoryConsumerHost host = new(container);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => host.InitializeAsync();

    public ValueTask DisposeAsync() => host.DisposeAsync();

    // ── Camino feliz ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Consume_OrderWithAvailableStock_PublishesStockReservedAndReservesTheQuantities()
    {
        var orderId = Guid.NewGuid();

        await PublishAsync(NewOrder(orderId, 100m, Line(MugId, 3), Line(KeyringId, 2)));
        await SettleAsync();

        var reserved = Assert.Single(Published<StockReserved>());
        Assert.Equal(orderId, reserved.OrderId);

        // Reservar mueve QuantityReserved y NO toca QuantityOnHand. Es la decisión
        // de 3.4: una sola columna decrementada haría indistinguible "vendido" de
        // "apartado para un pedido que todavía puede caerse", que es justo la
        // distinción que la compensación de 4.4 existe para deshacer.
        Assert.Equal(3, await host.QuantityReservedAsync(MugId, CancellationToken));
        Assert.Equal(2, await host.QuantityReservedAsync(KeyringId, CancellationToken));
        Assert.Equal(MugOnHand, await host.QuantityOnHandAsync(MugId, CancellationToken));

        var reservation = await host.ReservationAsync(orderId, CancellationToken);

        Assert.NotNull(reservation);
        Assert.Equal(
            [(MugId, 3), (KeyringId, 2)],
            reservation.Lines.OrderBy(line => line.ProductId).ToList());
    }

    /// <summary>
    /// El olvido más caro de este consumer, con test propio a propósito.
    ///
    /// <c>StockReserved.Amount</c> se reenvía tal cual desde
    /// <c>OrderCreated.Total</c>. A Inventory no le sirve de nada —guarda
    /// cantidades, no importes— y lo acarrea porque Payments no puede
    /// preguntárselo a nadie (decisión 1 de docs/fase_3_2.md). Si se pierde esa
    /// línea, <c>Amount</c> sale 0, **el pedido se cobra 0 y nada falla**: ni una
    /// excepción, ni un log raro, ni ninguno de los otros ocho tests.
    /// </summary>
    [Fact]
    public async Task Consume_OrderWithATotal_ForwardsItAsTheStockReservedAmount()
    {
        var orderId = Guid.NewGuid();

        await PublishAsync(NewOrder(orderId, 974.50m, Line(MugId, 2, 487.25m)));
        await SettleAsync();

        var reserved = Assert.Single(Published<StockReserved>());

        Assert.Equal(974.50m, reserved.Amount);
    }

    // ── Rechazos ─────────────────────────────────────────────────────────────

    /// <summary>
    /// La mitad de *existencia* del hueco que abrió la decisión 2 de
    /// docs/fase_3_3.md: desde que el cliente manda la foto del pedido, Orders ya
    /// no comprueba que el producto exista y devuelve <c>201</c>. Quien lo
    /// descubre es este consumer, y el pedido no se *rechaza* con un 404: se
    /// **cancela** con un evento, después de que el cliente se haya ido.
    /// </summary>
    [Fact]
    public async Task Consume_UnknownProduct_PublishesStockRejectedAndReservesNothing()
    {
        var orderId = Guid.NewGuid();

        await PublishAsync(NewOrder(orderId, 50m, Line(UnknownProductId, 1)));
        await SettleAsync();

        var rejected = Assert.Single(Published<StockRejected>());
        Assert.Equal(orderId, rejected.OrderId);
        Assert.Contains(UnknownProductId.ToString(), rejected.Reason);

        Assert.Empty(Published<StockReserved>());
        Assert.Equal(0, await host.CountReservationsAsync(CancellationToken));
    }

    [Fact]
    public async Task Consume_InsufficientStock_PublishesStockRejectedNamingTheAvailableQuantity()
    {
        var orderId = Guid.NewGuid();

        await PublishAsync(NewOrder(orderId, 50m, Line(ScarceProductId, ScarceOnHand + 1)));
        await SettleAsync();

        var rejected = Assert.Single(Published<StockRejected>());

        // El Reason es texto de diagnóstico y material para el email de 4.6, no un
        // código a parsear; se afirma que menciona las dos cifras, no su redacción.
        Assert.Contains(ScarceOnHand.ToString(), rejected.Reason);
        Assert.Contains((ScarceOnHand + 1).ToString(), rejected.Reason);

        Assert.Equal(0, await host.QuantityReservedAsync(ScarceProductId, CancellationToken));
    }

    /// <summary>
    /// **La atomicidad de 3.4**, que hasta ahora solo se había comprobado a mano.
    ///
    /// Una línea servible y otra imposible: no se reserva **nada**. Reservar sobre
    /// la marcha y abortar a mitad dejaría unidades comprometidas por un pedido
    /// que se va a cancelar — stock filtrado, que es lo que la regla 7 de
    /// CLAUDE.md existe para impedir.
    /// </summary>
    [Fact]
    public async Task Consume_OneServableLineAndOneImpossible_ReservesNothing()
    {
        var orderId = Guid.NewGuid();

        await PublishAsync(NewOrder(
            orderId,
            80m,
            Line(KeyringId, 1),
            Line(UnknownProductId, 1)));
        await SettleAsync();

        Assert.Single(Published<StockRejected>());

        // La línea que SÍ se podía servir tampoco se tocó.
        Assert.Equal(0, await host.QuantityReservedAsync(KeyringId, CancellationToken));
        Assert.Equal(0, await host.CountReservationsAsync(CancellationToken));
    }

    // ── Idempotencia ─────────────────────────────────────────────────────────

    /// <summary>
    /// La guarda de **negocio** (PK de <c>StockReservations</c> = <c>OrderId</c>),
    /// que existe desde 3.4 y **no** es la de 3.6. Un <c>MessageId</c> nuevo es
    /// alguien que ha vuelto a preguntar, no la misma entrega repetida: por eso
    /// aquí sí se republica <c>StockReserved</c> en vez de salir en silencio.
    ///
    /// Sin esta guarda el segundo mensaje reventaría el INSERT por clave duplicada
    /// y acabaría un pedido correcto en <c>order-created_error</c>.
    /// </summary>
    [Fact]
    public async Task Consume_SameOrderWithANewMessageId_RepublishesStockReservedWithoutReservingTwice()
    {
        var orderId = Guid.NewGuid();
        var order = NewOrder(orderId, 100m, Line(MugId, 3));

        await PublishAsync(order);
        await PublishAsync(order);
        await SettleAsync();

        // Dos StockReserved: la segunda entrega vuelve a contestar.
        Assert.Equal(2, Published<StockReserved>().Count);

        // Pero reserva una sola vez: 3 unidades, no 6.
        Assert.Equal(3, await host.QuantityReservedAsync(MugId, CancellationToken));
        Assert.Equal(1, await host.CountReservationsAsync(CancellationToken));
    }

    /// <summary>
    /// La guarda de **transporte** de 3.6, y el test que el roadmap llama "la
    /// única verificación fiable" de ese punto.
    ///
    /// Misma entrega dos veces ⇒ un solo efecto **y un solo evento**. El contraste
    /// con el test de arriba es todo el contenido de 3.6: allí salen dos
    /// <c>StockReserved</c> porque el <c>MessageId</c> es distinto; aquí sale uno,
    /// porque la segunda entrega se descarta en silencio antes de llegar siquiera
    /// a consultar la reserva.
    /// </summary>
    [Fact]
    public async Task Consume_SameMessageIdTwice_PublishesASingleStockReserved()
    {
        var orderId = Guid.NewGuid();
        var order = NewOrder(orderId, 100m, Line(MugId, 3));
        var messageId = Guid.NewGuid();

        await PublishAsync(order, messageId);
        await PublishAsync(order, messageId);
        await SettleAsync();

        Assert.Single(Published<StockReserved>());

        // **El assert que de verdad prueba 3.6**, y sin él este test pasa con la
        // guarda de transporte borrada — comprobado rompiéndola a propósito.
        //
        // El motivo es que hay una segunda red debajo: sin la guarda, la segunda
        // entrega llega a MarkProcessed con un MessageId ya escrito y el INSERT en
        // ProcessedMessages revienta por clave duplicada. El consumer falla, no
        // publica, y el recuento de arriba sale igual. O sea que "un solo evento"
        // no distingue "se descartó limpiamente" de "explotó".
        //
        // Lo que sí las distingue es el fault: descartar en silencio no publica
        // ninguno; explotar publica Fault<OrderCreated> y manda el mensaje a la
        // cola de errores.
        Assert.Empty(Published<Fault<OrderCreated>>());

        Assert.Equal(3, await host.QuantityReservedAsync(MugId, CancellationToken));
        Assert.Equal(1, await host.CountReservationsAsync(CancellationToken));
        Assert.Equal(1, await host.CountProcessedAsync(CancellationToken));
    }

    /// <summary>
    /// **El agujero concreto que 3.6 tapó**, y el que ningún estado de base puede
    /// delatar.
    ///
    /// Hasta 3.6 el camino de rechazo no escribía nada, así que no dejaba rastro
    /// por el que reconocer un duplicado y publicaba un **segundo**
    /// <c>StockRejected</c>. La base queda igual en los dos casos —no hay reserva
    /// ni stock movido ni antes ni después—, de modo que lo único que distingue el
    /// arreglo de su ausencia es este recuento. Por eso 3.6 tuvo que montar una
    /// cola espía para verificarlo a mano.
    /// </summary>
    [Fact]
    public async Task Consume_SameMessageIdTwice_OnTheRejectionPath_PublishesASingleStockRejected()
    {
        var orderId = Guid.NewGuid();
        var order = NewOrder(orderId, 50m, Line(UnknownProductId, 1));
        var messageId = Guid.NewGuid();

        await PublishAsync(order, messageId);
        await PublishAsync(order, messageId);
        await SettleAsync();

        Assert.Single(Published<StockRejected>());

        // Igual que en el test de arriba: sin este assert, el caso pasa también con
        // la guarda de transporte rota, porque entonces el duplicado muere en el
        // INSERT de ProcessedMessages en vez de descartarse. Silencio y explosión
        // se parecen mucho si solo se cuentan los eventos de negocio.
        Assert.Empty(Published<Fault<OrderCreated>>());

        // La marca es la única escritura de esta rama, y por eso lleva su propio
        // SaveChangesAsync en el consumer. Si se olvidara esa línea, la
        // deduplicación fallaría solo aquí — y solo este assert lo vería.
        Assert.Equal(1, await host.CountProcessedAsync(CancellationToken));
    }

    /// <summary>
    /// Un consumer que no puede deducir si el mensaje es un duplicado no puede
    /// cumplir la regla 6, así que revienta en vez de procesar a ciegas: el
    /// mensaje acaba en <c>order-created_error</c>, donde se ve.
    ///
    /// MassTransit siempre rellena el <c>MessageId</c>, así que esta rama solo la
    /// pisa un mensaje inyectado a mano — la razón de que la receta de reposteo de
    /// CLAUDE.md exija <c>message_id</c> en <c>properties</c>.
    /// </summary>
    [Fact]
    public async Task Consume_MessageWithoutMessageId_FaultsTheConsumer()
    {
        var order = NewOrder(Guid.NewGuid(), 100m, Line(MugId, 1));

        await host.Harness.Bus.Publish(
            order,
            context => context.MessageId = null,
            CancellationToken);
        await SettleAsync();

        var fault = Assert.Single(Published<Fault<OrderCreated>>());
        Assert.Contains(
            fault.Exceptions,
            exception => exception.ExceptionType == typeof(InvalidOperationException).FullName);

        Assert.Empty(Published<StockReserved>());
        Assert.Equal(0, await host.CountReservationsAsync(CancellationToken));
    }

    // ── Ayudas ───────────────────────────────────────────────────────────────

    private Task PublishAsync(OrderCreated order, Guid? messageId = null) =>
        host.Harness.Bus.Publish(
            order,
            context => context.MessageId = messageId ?? Guid.NewGuid(),
            CancellationToken);

    /// <summary>
    /// Espera a que el bus se quede sin trabajo. **Se llama una vez por test,
    /// después de todas las publicaciones**, y ese "una vez" no es estilo: es la
    /// condición de que funcione.
    ///
    /// <c>InactivityTask</c> es **una sola tarea que se completa la primera vez**
    /// que el bus queda inactivo; a partir de ahí cualquier <c>await</c> posterior
    /// vuelve al instante. Esperarla dentro de un helper de publicación —que fue
    /// el primer intento— hacía que en los tests de dos mensajes el segundo
    /// <c>await</c> no esperase nada y se contaran los eventos con el mensaje
    /// todavía en vuelo. Se detectó porque
    /// <c>Consume_SameOrderWithANewMessageId_…</c> falló pidiendo 2 y viendo 1;
    /// lo grave era lo otro, que los dos tests de idempotencia **pasaban por el
    /// motivo equivocado**, contando 1 antes de que llegara el duplicado.
    ///
    /// *Descartado* contar mensajes en <c>harness.Consumed</c>, que fue el segundo
    /// intento y falló por una razón que conviene no volver a descubrir:
    /// **<c>Consumed</c> está indexado por <c>MessageId</c>**. Dos entregas del
    /// mismo id colapsan en una sola entrada y un mensaje sin id no se registra —
    /// justo los dos casos que esta clase existe para probar. Se vio en que la
    /// espera por recuento funcionaba con ids distintos y agotaba los 30 s con ids
    /// iguales.
    ///
    /// Nótese que probar que algo **no** ocurrió obliga a esperar de forma
    /// acotada: no hay señal que anuncie "y ya no va a llegar nada más". Lo que
    /// hace <c>InactivityTask</c> mejor que un <c>Task.Delay</c> es que la espera
    /// la cierra el bus cuando de verdad se queda sin trabajo.
    /// </summary>
    private Task SettleAsync() => host.Harness.InactivityTask;

    private List<T> Published<T>()
        where T : class =>
        host.Harness.Published.Select<T>().Select(message => message.Context.Message).ToList();

    private static OrderCreated NewOrder(Guid orderId, decimal total, params OrderLine[] lines) =>
        new()
        {
            OrderId = orderId,
            CustomerEmail = CustomerEmail,
            Lines = lines,
            Total = total,
        };

    /// <summary>
    /// El sku y el nombre son ruido para este consumer —solo mira
    /// <c>ProductId</c> y <c>Quantity</c>— pero <c>OrderLine</c> los exige con
    /// <c>required</c>, y 3.1 midió que el serializador los valida de verdad: un
    /// mensaje incompleto no llega al consumer con null dentro, falla al
    /// deserializar.
    /// </summary>
    private static OrderLine Line(int productId, int quantity, decimal unitPrice = 10m) =>
        new()
        {
            ProductId = productId,
            ProductSku = $"TEST-{productId:D3}",
            ProductName = $"Producto de prueba {productId}",
            Quantity = quantity,
            UnitPrice = unitPrice,
        };
}
