using Catalog.API.Consumers;
using Catalog.Tests.Infrastructure;

using MassTransit;
using MassTransit.Testing;

using Shop133.Contracts;
using Shop133.Contracts.Events;
using Shop133.TestUtilities;

using Xunit;

namespace Catalog.Tests;

/// <summary>
/// <see cref="OrderCreatedPricingConsumer"/> — el consumer de 4.8, con el que
/// Catalog deja de ser el único servicio sin mensajería y el importe del pedido
/// deja de estar sin dueño.
///
/// **Los asserts miran los eventos publicados, no la base.** Aquí eso no es una
/// preferencia: este consumer **no escribe nada de negocio**, solo la marca de
/// idempotencia. La base es idéntica se acepte o se rechace un pedido, así que la
/// única cosa observable es qué evento salió — y en los tests de idempotencia,
/// cuántos. Es la conclusión de docs/fase_3_6.md llevada al extremo.
///
/// Del seed de 1.4 se usa el producto 1 (<c>TAZA-001</c>) **solo para leer**. Los
/// tests que cambian precios crean su propio producto <c>TEST-8xx</c>: es la
/// disciplina que 1.7 impuso a esta suite, y aunque cada test tenga su propia base
/// desde entonces, mezclar los dos usos haría los asserts ilegibles.
/// </summary>
[Collection(CatalogApiCollection.Name)]
[Trait("Category", "Docker")]
public sealed class OrderCreatedPricingConsumerTests(SqlServerContainerFixture container) : IAsyncLifetime
{
    private const string CustomerEmail = "cliente@shop133.test";

    /// <summary>El primer producto del seed de 1.4: <c>TAZA-001</c>. Solo se lee.</summary>
    private const int SeededProductId = 1;

    private const int UnknownProductId = 999_999;

    private readonly CatalogConsumerHost host = new(container);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => host.InitializeAsync();

    public ValueTask DisposeAsync() => host.DisposeAsync();

    // ── Camino feliz ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Consume_OrderWithTheCurrentPrices_PublishesOrderPricingValidated()
    {
        var orderId = Guid.NewGuid();
        var price = await host.PriceAsync(SeededProductId, CancellationToken);

        await PublishAsync(NewOrder(orderId, price * 2, Line(SeededProductId, 2, price)));
        await SettleAsync();

        var validated = Assert.Single(Published<OrderPricingValidated>());
        Assert.Equal(orderId, validated.OrderId);

        Assert.Empty(Published<OrderPricingRejected>());
    }

    // ── Rechazos ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Catalog es el dueño de la existencia de un producto. Inventory descubre este
    /// mismo caso desde 3.4, pero contra <c>StockItems</c> y **en paralelo** —
    /// consume el mismo fanout—, así que son dos preguntas distintas contestadas por
    /// sus dos dueños: un producto puede tener fila en InventoryDb y no existir aquí.
    /// </summary>
    [Fact]
    public async Task Consume_UnknownProduct_PublishesOrderPricingRejected()
    {
        var orderId = Guid.NewGuid();

        await PublishAsync(NewOrder(orderId, 50m, Line(UnknownProductId, 1, 50m)));
        await SettleAsync();

        var rejected = Assert.Single(Published<OrderPricingRejected>());
        Assert.Equal(orderId, rejected.OrderId);
        Assert.Contains(UnknownProductId.ToString(), rejected.Reason);

        Assert.Empty(Published<OrderPricingValidated>());
    }

    /// <summary>
    /// **El test por el que existe 4.8.**
    ///
    /// Es el escenario exacto que la corrección 2b de docs/fase_3_3.md dejó medido y
    /// sin dueño: desde que el cuerpo del <c>POST /orders</c> trae el precio, un
    /// pedido de un producto **que sí existe** a <c>0.01</c> devolvía <c>201</c>,
    /// pasaba la reserva de Inventory (que guarda cantidades, no importes), pasaba el
    /// umbral de Payments (0.01 no supera 1000) y **se cobraba un céntimo**. Ningún
    /// punto del roadmap lo notaba.
    ///
    /// El <c>Reason</c> nombra las dos cifras: sin la del catálogo, "el precio no es
    /// válido" no le sirve ni a quien lea el log ni a quien reciba el email de 4.6.
    /// Se afirma que las **contiene**, nunca su redacción.
    /// </summary>
    [Fact]
    public async Task Consume_UnitPriceThatWasNeverThePrice_PublishesOrderPricingRejected()
    {
        var orderId = Guid.NewGuid();
        var price = await host.PriceAsync(SeededProductId, CancellationToken);
        var sku = await host.SkuAsync(SeededProductId, CancellationToken);

        await PublishAsync(NewOrder(orderId, 0.01m, Line(SeededProductId, 1, 0.01m)));
        await SettleAsync();

        var rejected = Assert.Single(Published<OrderPricingRejected>());
        Assert.Contains("0.01", rejected.Reason);
        Assert.Contains(price.ToString("0.00"), rejected.Reason);
        Assert.Contains(sku, rejected.Reason);

        Assert.Empty(Published<OrderPricingValidated>());
    }

    // ── La ventana: las dos caras de la decisión 1 de 4.8 ────────────────────

    /// <summary>
    /// **El caso por el que la validación no es una comparación por igualdad.**
    ///
    /// El cliente vio 100.00, empezó el checkout, alguien cambió el precio a 80.00 y
    /// el pedido llegó con la foto de 100.00. Es un pedido **legítimo**: congelar el
    /// precio que el cliente vio es el comportamiento correcto, y todo el <c>///</c>
    /// de <c>OrderLine</c> existe para decirlo. Comparar contra el precio de hoy —lo
    /// que el roadmap llama incorrecto— lo cancelaría.
    ///
    /// El cambio de precio se hace llamando al <c>Product.Update</c> real, así que
    /// este test también afirma que esa contabilidad se escribió.
    /// </summary>
    [Fact]
    public async Task Consume_UnitPriceMatchingThePreviousPriceInsideTheWindow_PublishesOrderPricingValidated()
    {
        var orderId = Guid.NewGuid();
        var productId = await host.SeedProductAsync("TEST-801", 100m, CancellationToken);

        await host.ChangePriceAsync(productId, 80m, CancellationToken);

        await PublishAsync(NewOrder(orderId, 100m, Line(productId, 1, 100m)));
        await SettleAsync();

        Assert.Single(Published<OrderPricingValidated>());
        Assert.Empty(Published<OrderPricingRejected>());
    }

    /// <summary>
    /// La otra cara, y **lo que prueba que la ventana es una ventana** y no "cualquier
    /// precio viejo vale para siempre". Sin este test, borrar la comprobación de
    /// fecha de <c>Product.IsAuthenticPrice</c> dejaría el anterior en verde.
    ///
    /// La fecha se retrasa en la base porque la entidad lee <c>UtcNow</c> directo y
    /// este repositorio descartó por escrito un <c>TimeProvider</c> inyectado — ver
    /// el <c>///</c> de <c>CatalogConsumerHost.BackdatePriceChangeAsync</c>.
    /// </summary>
    [Fact]
    public async Task Consume_UnitPriceMatchingThePreviousPriceOutsideTheWindow_PublishesOrderPricingRejected()
    {
        var orderId = Guid.NewGuid();
        var productId = await host.SeedProductAsync("TEST-802", 100m, CancellationToken);

        await host.ChangePriceAsync(productId, 80m, CancellationToken);
        await host.BackdatePriceChangeAsync(
            productId,
            TimeSpan.FromMinutes(CatalogConsumerHost.SnapshotWindowMinutes + 1),
            CancellationToken);

        await PublishAsync(NewOrder(orderId, 100m, Line(productId, 1, 100m)));
        await SettleAsync();

        var rejected = Assert.Single(Published<OrderPricingRejected>());
        Assert.Contains("100.00", rejected.Reason);
        Assert.Contains("80.00", rejected.Reason);

        Assert.Empty(Published<OrderPricingValidated>());
    }

    // ── El total ─────────────────────────────────────────────────────────────

    /// <summary>
    /// **El agujero que nadie estaba mirando**, y que no necesita mentir sobre
    /// ningún precio.
    ///
    /// <c>OrderCreated.Total</c> es lo que Payments cobra: Inventory lo reenvía tal
    /// cual en <c>StockReserved.Amount</c> (3.2/3.4) y Payments lo compara contra su
    /// umbral y lo persiste (3.5). Hasta 4.8 nada comprobaba que cuadrara con las
    /// líneas, así que un cuerpo con líneas auténticas y un <c>Total</c> de 0.01
    /// pasaba entero.
    ///
    /// La línea va **al precio real** a propósito: así el total es el único problema
    /// posible, y un fallo de este test no puede confundirse con un fallo de la
    /// validación de precios.
    /// </summary>
    [Fact]
    public async Task Consume_TotalThatDoesNotMatchTheLines_PublishesOrderPricingRejected()
    {
        var orderId = Guid.NewGuid();
        var price = await host.PriceAsync(SeededProductId, CancellationToken);

        await PublishAsync(NewOrder(orderId, 0.01m, Line(SeededProductId, 2, price)));
        await SettleAsync();

        var rejected = Assert.Single(Published<OrderPricingRejected>());
        Assert.Contains("0.01", rejected.Reason);
        Assert.Contains((price * 2).ToString("0.00"), rejected.Reason);

        Assert.Empty(Published<OrderPricingValidated>());
    }

    // ── Idempotencia (regla 6) ───────────────────────────────────────────────

    [Fact]
    public async Task Consume_SameMessageIdTwice_PublishesASingleOrderPricingValidated()
    {
        var orderId = Guid.NewGuid();
        var price = await host.PriceAsync(SeededProductId, CancellationToken);
        var order = NewOrder(orderId, price, Line(SeededProductId, 1, price));
        var messageId = Guid.NewGuid();

        await PublishAsync(order, messageId);
        await PublishAsync(order, messageId);
        await SettleAsync();

        Assert.Single(Published<OrderPricingValidated>());

        // **Sin este assert el test pasa con la guarda borrada.** Es la trampa 3 de
        // docs/fase_3_7.md, confirmada de nuevo en 4.4 y 4.7: sin guarda el duplicado
        // no se descarta, muere en el INSERT por clave duplicada de ProcessedMessages
        // y por tanto tampoco publica. Silencio y explosión se parecen mucho si solo
        // se cuentan los eventos de negocio.
        Assert.Empty(Published<Fault<OrderCreated>>());

        Assert.Equal(1, await host.CountProcessedAsync(CancellationToken));
    }

    /// <summary>
    /// El mismo caso por el camino de rechazo. En Inventory este test cubría una
    /// rama que no escribía nada mientras las otras sí; **aquí no escribe nada
    /// ninguna**, así que los dos caminos comparten el único <c>SaveChangesAsync</c>
    /// del consumer y este test recorre casi las mismas líneas que el de arriba.
    ///
    /// Se mantiene porque es barato y porque es donde se vería un cambio que moviera
    /// el marcado a solo una de las dos ramas — el error que 3.4 cometió y 3.6 tuvo
    /// que arreglar.
    /// </summary>
    [Fact]
    public async Task Consume_SameMessageIdTwice_OnTheRejectionPath_PublishesASingleOrderPricingRejected()
    {
        var order = NewOrder(Guid.NewGuid(), 0.01m, Line(SeededProductId, 1, 0.01m));
        var messageId = Guid.NewGuid();

        await PublishAsync(order, messageId);
        await PublishAsync(order, messageId);
        await SettleAsync();

        Assert.Single(Published<OrderPricingRejected>());
        Assert.Empty(Published<Fault<OrderCreated>>());
        Assert.Equal(1, await host.CountProcessedAsync(CancellationToken));
    }

    /// <summary>
    /// **Este test afirma una AUSENCIA deliberada**, y es la forma ejecutable de la
    /// decisión de idempotencia de 4.8.
    ///
    /// En los otros cuatro servicios, el mismo pedido reacuñado con un
    /// <c>MessageId</c> nuevo pasa por delante de la guarda de transporte y lo para
    /// una guarda de **negocio**: la PK de <c>StockReservations</c> (3.4), la fila de
    /// <c>Payments</c> (3.5), la clave <c>(OrderId, Kind)</c> de
    /// <c>Notifications</c> (4.6). Todas salieron de una fila que el consumer tenía
    /// que escribir de todas formas.
    ///
    /// **Catalog no tiene ninguna**, porque validar precios es una lectura pura y no
    /// deja artefacto. Así que vuelve a leer y vuelve a contestar — dos eventos, dos
    /// filas marcadas. Se acepta porque la respuesta es función pura del mensaje y de
    /// CatalogDb, no un segundo efecto como sería un segundo cobro; e inventar una
    /// tabla <c>PricingValidations</c> sería que CatalogDb guardara datos de pedidos
    /// que no le pertenecen.
    ///
    /// Si algún día aparece esa guarda de negocio, **este test se pone en rojo** y
    /// eso es lo que se quiere: obliga a releer la decisión en vez de descubrir el
    /// cambio de comportamiento en producción.
    /// </summary>
    [Fact]
    public async Task Consume_SameOrderWithANewMessageId_AnswersAgain_BecauseThereIsNoBusinessGuard()
    {
        var orderId = Guid.NewGuid();
        var price = await host.PriceAsync(SeededProductId, CancellationToken);
        var order = NewOrder(orderId, price, Line(SeededProductId, 1, price));

        await PublishAsync(order);
        await PublishAsync(order);
        await SettleAsync();

        Assert.Equal(2, Published<OrderPricingValidated>().Count);
        Assert.Empty(Published<Fault<OrderCreated>>());
        Assert.Equal(2, await host.CountProcessedAsync(CancellationToken));
    }

    /// <summary>
    /// Un consumer que no puede deducir si el mensaje es un duplicado no puede
    /// cumplir la regla 6, así que revienta en vez de procesar a ciegas: el mensaje
    /// acaba en <c>order-created-pricing_error</c>, donde se ve.
    ///
    /// MassTransit siempre rellena el <c>MessageId</c>, así que esta rama solo la
    /// pisa un mensaje inyectado a mano — la razón de que la receta de reposteo de
    /// CLAUDE.md exija <c>message_id</c> en <c>properties</c>.
    /// </summary>
    [Fact]
    public async Task Consume_MessageWithoutMessageId_FaultsTheConsumer()
    {
        var price = await host.PriceAsync(SeededProductId, CancellationToken);
        var order = NewOrder(Guid.NewGuid(), price, Line(SeededProductId, 1, price));

        await host.Harness.Bus.Publish(
            order,
            context => context.MessageId = null,
            CancellationToken);
        await SettleAsync();

        var fault = Assert.Single(Published<Fault<OrderCreated>>());
        Assert.Contains(
            fault.Exceptions,
            exception => exception.ExceptionType == typeof(InvalidOperationException).FullName);

        // Ni una cosa ni la otra: el throw va ANTES de cualquier validación, así que
        // el consumer no llega a decidir nada sobre la foto de precios.
        Assert.Empty(Published<OrderPricingValidated>());
        Assert.Empty(Published<OrderPricingRejected>());
        Assert.Equal(0, await host.CountProcessedAsync(CancellationToken));
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
    /// <c>InactivityTask</c> es **una sola tarea que se completa la primera vez** que
    /// el bus queda inactivo; a partir de ahí cualquier <c>await</c> posterior vuelve
    /// al instante. Ha mordido tres veces en este repositorio (3.7, 4.4, 4.7), así
    /// que conviene ver por qué aquí es seguro: **todos los mensajes de un test van
    /// al mismo endpoint** (<c>order-created-pricing</c>) con
    /// <c>ConcurrentMessageLimit = 1</c>, o sea en FIFO, y **el sembrado de precios
    /// no pasa por el bus** — lo hacen los helpers del host. No hay ni un test con
    /// dos etapas de bus, que es lo que gasta la tarea antes de tiempo.
    ///
    /// *Descartado* contar mensajes en <c>harness.Consumed</c>: está indexado por
    /// <c>MessageId</c>, así que dos entregas del mismo id colapsan en una entrada y
    /// un mensaje sin id no se registra — justo los tres casos que esta clase existe
    /// para probar.
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
    /// El sku y el nombre de la línea son ruido para este consumer — 4.8 decidió
    /// **no** compararlos contra el catálogo, porque <c>Product.Update</c> puede
    /// cambiar el Sku desde 1.3 y renombrar un producto es una operación normal, así
    /// que compararlos daría falsos rechazos igual que compararía el precio contra el
    /// de hoy. Aquí se rellenan con valores que deliberadamente **no** coinciden con
    /// los del seed, y ningún test se entera: eso es la decisión, hecha observable.
    /// </summary>
    private static OrderLine Line(int productId, int quantity, decimal unitPrice) =>
        new()
        {
            ProductId = productId,
            ProductSku = $"NOMATCH-{productId:D3}",
            ProductName = $"Nombre que no coincide {productId}",
            Quantity = quantity,
            UnitPrice = unitPrice,
        };
}
