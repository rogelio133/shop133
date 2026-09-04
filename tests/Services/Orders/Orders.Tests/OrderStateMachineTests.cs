using MassTransit;

using Orders.Domain.Sagas;
using Orders.Tests.Infrastructure;

using Shop133.Contracts;
using Shop133.Contracts.Commands;
using Shop133.Contracts.Events;

using Xunit;

namespace Orders.Tests;

/// <summary>
/// <see cref="OrderStateMachine"/> — **los cuatro escenarios obligatorios del roadmap**, que
/// son la especificación de este punto: compra exitosa, sin stock, stock reservado con pago
/// rechazado (la compensación) y evento duplicado.
///
/// Hasta hoy la saga se verificó **a mano** contra el compose real en 4.1, 4.2, 4.3, 4.4 y
/// 4.5 — cinco puntos seguidos cuya comprobación no sobrevive a un refactor. Es el hueco más
/// grande que tenía la suite.
///
/// **Primera clase <c>Category=Fast</c> de un servicio**: sin Docker, sin SQL Server y sin
/// collection, porque no toca el <c>SqlServerContainerFixture</c>. Lo que se prueba aquí es un
/// proceso —qué transición dispara cada evento y qué mensaje sale— y eso no necesita tabla.
/// La persistencia de 4.5 se prueba aparte, en <see cref="OrderStatePersistenceTests"/>.
///
/// ── La estrategia de espera, que es el problema real de esta clase ──
///
/// Una saga es multi-etapa por naturaleza: <c>OrderCreated → StockReserved →
/// PaymentCompleted</c>. Y <c>harness.InactivityTask</c> es **una sola tarea** que se completa
/// la primera vez que el bus queda ocioso, así que un segundo <c>await</c> vuelve al instante
/// (trampa 1 de docs/fase_3_7.md, estrellada de verdad en la decisión 8 de
/// docs/fase_4_4.md). La solución de 4.4 —sembrar el estado previo por base de datos— no se
/// puede trasladar aquí: la secuencia *es* lo que se prueba.
///
/// Lo que se hace en su lugar: **publicar todos los eventos seguidos y esperar UNA sola vez al
/// final**. Los seis eventos de la saga entran por el mismo endpoint (<c>order-state</c>) con
/// <c>ConcurrentMessageLimit = 1</c>, así que la cola es FIFO y el orden de publicación es el
/// de consumo. Y el test se autocomprueba: si ese orden se rompiera, un <c>StockReserved</c>
/// sin instancia dispara el <c>OnMissingInstance</c> y un <c>PaymentCompleted</c> en
/// <c>StockPending</c> no está aceptado — o sea que un desorden sale como fallo ruidoso, nunca
/// como verde silencioso. Por eso **todos** los tests afirman que no hay faults.
///
/// *Descartado* esperar con <c>SagaHarness.Exists(orderId, m => m.PaymentPending)</c> entre
/// etapas: ordena bien, pero no des-gasta el <c>InactivityTask</c>, y sin él solo se puede
/// afirmar "al menos uno", nunca "exactamente uno" — que es justo lo que el roadmap exige del
/// escenario 3. Es el mismo descarte que razonó 4.4 con <c>Published.Any&lt;StockReserved&gt;()</c>.
/// </summary>
[Trait("Category", "Fast")]
public sealed class OrderStateMachineTests : IAsyncLifetime
{
    private const string CustomerEmail = "cliente@shop133.test";

    private const int MugId = 1;
    private const string MugSku = "TAZA-001";
    private const string MugName = "Taza Talavera Puebla";
    private const decimal MugPrice = 249.00m;

    private readonly OrderSagaHost host = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => host.InitializeAsync();

    public ValueTask DisposeAsync() => host.DisposeAsync();

    // ── Escenario 1: compra exitosa ──────────────────────────────────────────

    [Fact]
    public async Task HappyPath_ReachesConfirmedAndPublishesExactlyOneOrderConfirmed()
    {
        var orderId = Guid.NewGuid();

        await PublishAsync(OrderCreated(orderId));
        await PublishAsync(new StockReserved { OrderId = orderId, Amount = MugPrice });
        await PublishAsync(PaymentCompleted(orderId));
        await SettleAsync();

        AssertNoFaults();

        var confirmed = Assert.Single(Published<OrderConfirmed>());
        Assert.Equal(orderId, confirmed.OrderId);

        Assert.Equal(nameof(OrderStateMachine.Confirmed), host.State(orderId));

        // Y por el camino feliz **no se manda nada a Inventory**: no hay nada que compensar
        // cuando todo salió bien. Es la mitad de la regla 7 que se olvida, porque un
        // ReleaseStock de más no rompe ningún test que solo mire el estado final.
        Assert.Empty(Sent<ReleaseStock>());

        // El pedido no se cancela: los dos desenlaces son excluyentes, y es lo que hace que
        // Order.Confirm()/Cancel() puedan lanzar ante una transición imposible (4.3).
        Assert.Empty(Published<OrderCancelled>());
    }

    /// <summary>
    /// El <c>CustomerEmail</c> del <c>OrderConfirmed</c> sale de la **instancia**, no del
    /// mensaje que dispara la transición: <c>PaymentCompleted</c> no lo lleva. Es la decisión
    /// 6 de docs/fase_4_1.md —copiarlo en el <c>Initially</c>— en forma de assert, y sin ella
    /// Notifications.API se quedaría sin destinatario, porque no puede leer <c>OrdersDb</c>
    /// (regla 1).
    /// </summary>
    [Fact]
    public async Task HappyPath_OrderConfirmedCarriesTheEmailCapturedInInitially()
    {
        var orderId = Guid.NewGuid();

        await PublishAsync(OrderCreated(orderId));
        await PublishAsync(new StockReserved { OrderId = orderId, Amount = MugPrice });
        await PublishAsync(PaymentCompleted(orderId));
        await SettleAsync();

        AssertNoFaults();

        var confirmed = Assert.Single(Published<OrderConfirmed>());
        Assert.Equal(CustomerEmail, confirmed.CustomerEmail);

        var instance = host.Instance(orderId);
        Assert.NotNull(instance);
        Assert.Equal(CustomerEmail, instance.CustomerEmail);
    }

    // ── Escenario 2: sin stock disponible ────────────────────────────────────

    /// <summary>
    /// El camino de error **corto**. El <c>Reason</c> que compone Inventory viaja tal cual
    /// dentro del <c>OrderCancelled</c>: la saga no lo reescribe ni lo traduce a un código,
    /// porque quien mejor sabe por qué falló es quien falló.
    /// </summary>
    [Fact]
    public async Task StockRejected_ReachesCancelledAndPublishesOrderCancelledWithTheReason()
    {
        var orderId = Guid.NewGuid();
        const string reason = "el producto 999999 no existe en el inventario";

        await PublishAsync(OrderCreated(orderId));
        await PublishAsync(new StockRejected { OrderId = orderId, Reason = reason });
        await SettleAsync();

        AssertNoFaults();

        var cancelled = Assert.Single(Published<OrderCancelled>());
        Assert.Equal(orderId, cancelled.OrderId);
        Assert.Equal(CustomerEmail, cancelled.CustomerEmail);
        Assert.Equal(reason, cancelled.Reason);

        Assert.Equal(nameof(OrderStateMachine.Cancelled), host.State(orderId));
        Assert.Empty(Published<OrderConfirmed>());
    }

    /// <summary>
    /// **Por este camino no se manda ningún <c>ReleaseStock</c>, y ése es el test que nadie
    /// escribe.** La reserva de Inventory es atómica —verificado en docs/fase_3_4.md—, así que
    /// un rechazo significa que ninguna unidad se movió: soltar stock aquí sería devolver
    /// unidades que nunca se apartaron, o sea **crearlas de la nada**, que es lo que el
    /// <c>///</c> de <c>ReleaseStock</c> avisa que es peor que un duplicado de reserva.
    ///
    /// Es también lo que distingue los dos caminos de error, que comparten estado final: no
    /// se diferencian por dónde acaban sino por **lo que queda por deshacer**.
    /// </summary>
    [Fact]
    public async Task StockRejected_SendsNoReleaseStock()
    {
        var orderId = Guid.NewGuid();

        await PublishAsync(OrderCreated(orderId));
        await PublishAsync(new StockRejected { OrderId = orderId, Reason = "sin unidades" });
        await SettleAsync();

        AssertNoFaults();

        Assert.Single(Published<OrderCancelled>());
        Assert.Empty(Sent<ReleaseStock>());
        Assert.Empty(Consumed<ReleaseStock>());
        Assert.Empty(Published<StockReleased>());
    }

    // ── Escenario 3: stock reservado y pago rechazado (la compensación) ──────

    /// <summary>
    /// **El escenario que da nombre a la fase, y la regla 7 de CLAUDE.md en forma
    /// ejecutable**: se publica *exactamente un* <c>ReleaseStock</c> y el estado final es
    /// <c>Cancelled</c>.
    ///
    /// El ida y vuelta entero ocurre dentro de una sola etapa de bus: la saga manda el
    /// comando, el espía lo consume y contesta <c>StockReleased</c>, y solo entonces sale el
    /// <c>OrderCancelled</c>. Por eso el <c>InactivityTask</c> de un solo uso basta.
    /// </summary>
    [Fact]
    public async Task PaymentFailed_SendsExactlyOneReleaseStockAndEndsCancelled()
    {
        var orderId = Guid.NewGuid();

        await PublishAsync(OrderCreated(orderId));
        await PublishAsync(new StockReserved { OrderId = orderId, Amount = MugPrice });
        await PublishAsync(PaymentFailed(orderId));
        await SettleAsync();

        AssertNoFaults();

        // Exactamente uno. Soltar el stock dos veces es peor que no soltarlo, y es el motivo
        // por el que la saga usa Send y no Publish (un fanout admitiría un segundo suscriptor
        // sin tocar una línea de código).
        var release = Assert.Single(Sent<ReleaseStock>());
        Assert.Equal(orderId, release.OrderId);

        // Y llegó a su destino. Esto es lo que ata el `queue:release-stock` que la saga
        // escribe a mano con el nombre del endpoint donde alguien escucha — el único acuerdo
        // del proyecto que no vigila el compilador y cuyo desacuerdo no produce ningún error.
        Assert.Single(Consumed<ReleaseStock>());
        Assert.Single(Published<StockReleased>());

        Assert.Single(Published<OrderCancelled>());
        Assert.Equal(nameof(OrderStateMachine.Cancelled), host.State(orderId));
    }

    /// <summary>
    /// Con Inventory callado, la saga **espera** en <c>CompensatingStock</c> y el pedido
    /// **no** se cancela. Es la frase del <c>///</c> de ese estado convertida en assert: el
    /// proceso no ha terminado mientras el stock siga reservado, y publicar el
    /// <c>OrderCancelled</c> antes de tiempo sería que la saga afirmara algo que no sabe —
    /// el <c>///</c> de <c>OrderCancelled</c> promete desde 0.3 que en este camino "el stock
    /// ya se soltó".
    ///
    /// Es también la única forma de ver el estado intermedio: en el test de arriba la saga
    /// entra y sale de él dentro del mismo <c>await</c>.
    /// </summary>
    [Fact]
    public async Task PaymentFailed_WithoutInventoryAnswer_WaitsInCompensatingStockWithoutCancelling()
    {
        var orderId = Guid.NewGuid();

        host.Spy.Answers = false;

        await PublishAsync(OrderCreated(orderId));
        await PublishAsync(new StockReserved { OrderId = orderId, Amount = MugPrice });
        await PublishAsync(PaymentFailed(orderId));
        await SettleAsync();

        AssertNoFaults();

        Assert.Single(Sent<ReleaseStock>());
        Assert.Empty(Published<StockReleased>());

        Assert.Equal(nameof(OrderStateMachine.CompensatingStock), host.State(orderId));
        Assert.Empty(Published<OrderCancelled>());
        Assert.Empty(Published<OrderConfirmed>());
    }

    /// <summary>
    /// El motivo de la cancelación sale de <c>OrderState.CancellationReason</c>, guardado una
    /// transición antes: <c>OrderCancelled</c> no se publica al recibir el
    /// <c>PaymentFailed</c> que trae el texto, sino al recibir el <c>StockReleased</c>, que no
    /// lleva ninguno. **Ése es el precio del estado intermedio**, y este test es la única cosa
    /// que justifica que ese campo exista.
    /// </summary>
    [Fact]
    public async Task PaymentFailed_OrderCancelledCarriesTheReasonSavedInTheInstance()
    {
        var orderId = Guid.NewGuid();
        const string reason = "el importe 1197.00 supera el límite autorizado";

        await PublishAsync(OrderCreated(orderId));
        await PublishAsync(new StockReserved { OrderId = orderId, Amount = 1197.00m });
        await PublishAsync(new PaymentFailed { OrderId = orderId, Reason = reason });
        await SettleAsync();

        AssertNoFaults();

        var cancelled = Assert.Single(Published<OrderCancelled>());
        Assert.Equal(reason, cancelled.Reason);
        Assert.Equal(CustomerEmail, cancelled.CustomerEmail);

        var instance = host.Instance(orderId);
        Assert.NotNull(instance);
        Assert.Equal(reason, instance.CancellationReason);
    }

    // ── Escenario 4: evento duplicado ────────────────────────────────────────

    /// <summary>
    /// La idempotencia de la saga (regla 6 de CLAUDE.md), que **no es la tabla
    /// <c>ProcessedMessages</c> de 3.6**: aquí la guarda son los <c>Ignore(...)</c> repartidos
    /// por los <c>During</c>, y reconocen el mismo *pedido*, no la misma *entrega*.
    ///
    /// Por eso los duplicados van con <c>MessageId</c> **distintos**: un id repetido sería
    /// una reentrega, que aquí no la para nadie —no hay inbox en el harness— y además
    /// colapsaría las dos entradas de <c>harness.Consumed</c> en una (trampa 2 de 3.7),
    /// dejando el test sin poder demostrar que la saga llegó a ver los dos mensajes.
    ///
    /// **El assert que de verdad prueba la guarda es <c>AssertNoFaults()</c>** (trampa 3 de
    /// 3.7): sin los <c>Ignore</c>, MassTransit lanza
    /// <c>NotAcceptedStateMachineException</c> ante un evento no aceptado en el estado
    /// actual, así que el duplicado no se ignora — revienta, y el recuento de
    /// <c>OrderConfirmed</c> sigue saliendo 1 en los dos casos. Contar eventos de negocio no
    /// distingue *se descartó* de *explotó*.
    ///
    /// Un duplicado en cada mitad del camino: <c>OrderCreated</c> repetido cae en
    /// <c>StockPending</c> (la guarda que estrenó 4.1) y <c>PaymentCompleted</c> repetido cae
    /// en <c>Confirmed</c>, o sea en un estado **terminal** — el <c>During(Confirmed, ...)</c>
    /// que parece código muerto y es el más fácil de borrar por error.
    /// </summary>
    [Fact]
    public async Task DuplicateEvents_ProduceASingleOrderConfirmedAndNoFaults()
    {
        var orderId = Guid.NewGuid();

        await PublishAsync(OrderCreated(orderId));
        await PublishAsync(OrderCreated(orderId));
        await PublishAsync(new StockReserved { OrderId = orderId, Amount = MugPrice });
        await PublishAsync(PaymentCompleted(orderId));
        await PublishAsync(PaymentCompleted(orderId));
        await SettleAsync();

        AssertNoFaults();

        // Las cinco entregas llegaron: sin esto el test podría pasar porque los duplicados se
        // perdieron por el camino, que es aprobar por el motivo equivocado.
        Assert.Equal(2, Consumed<OrderCreated>().Count);
        Assert.Equal(2, Consumed<PaymentCompleted>().Count);

        // Un solo efecto. Dos OrderConfirmed significarían dos emails y dos Order.Confirm(),
        // el segundo de los cuales lanza (4.3).
        Assert.Single(Published<OrderConfirmed>());
        Assert.Equal(nameof(OrderStateMachine.Confirmed), host.State(orderId));
    }

    // ── OnMissingInstance ────────────────────────────────────────────────────

    /// <summary>
    /// Un evento correlacionado con un pedido que **nunca existió** va a la cola de error.
    ///
    /// Merece test propio porque **el comportamiento por defecto de MassTransit 8 es
    /// descartarlo en silencio** —sin excepción, sin cola de error y sin una línea de log—, y
    /// eso se midió en la verificación 7 de docs/fase_4_2.md creyendo lo contrario. Las dos
    /// líneas de <c>OnMissingInstance(m => m.Fault())</c> que lleva cada evento existen solo
    /// para evitarlo, y nada más en el repositorio las tocaría: borrarlas no rompe ninguna
    /// compilación y hace desaparecer mensajes sin rastro.
    ///
    /// Desde 4.5 la línea cambió de significado y por eso sigue: con la saga persistida, un
    /// reinicio ya no pierde instancias, así que esto ya no señala un accidente de
    /// infraestructura sino una incoherencia real.
    /// </summary>
    [Fact]
    public async Task EventForAnOrderThatNeverExisted_Faults()
    {
        var orphanId = Guid.NewGuid();

        await PublishAsync(new StockReserved { OrderId = orphanId, Amount = MugPrice });
        await SettleAsync();

        Assert.Single(Published<Fault<StockReserved>>());

        Assert.Null(host.State(orphanId));
        Assert.Empty(Published<OrderConfirmed>());
        Assert.Empty(Published<OrderCancelled>());
    }

    // ── Ayudas ───────────────────────────────────────────────────────────────

    private static OrderCreated OrderCreated(Guid orderId) => new()
    {
        OrderId = orderId,
        CustomerEmail = CustomerEmail,
        Lines =
        [
            new OrderLine
            {
                ProductId = MugId,
                ProductSku = MugSku,
                ProductName = MugName,
                Quantity = 1,
                UnitPrice = MugPrice,
            },
        ],
        Total = MugPrice,
    };

    private static PaymentCompleted PaymentCompleted(Guid orderId) => new()
    {
        OrderId = orderId,
        Amount = MugPrice,
        TransactionId = $"SIM-{orderId:N}",
    };

    private static PaymentFailed PaymentFailed(Guid orderId) => new()
    {
        OrderId = orderId,
        Reason = "el importe supera el límite autorizado",
    };

    /// <summary>
    /// Publica con un <c>MessageId</c> nuevo en cada llamada, que es lo que hace que dos
    /// entregas del mismo evento sean dos entradas de <c>harness.Consumed</c> y no una.
    /// </summary>
    private Task PublishAsync<T>(T message)
        where T : class =>
        host.Harness.Bus.Publish(message, context => context.MessageId = Guid.NewGuid(), CancellationToken);

    /// <summary>
    /// Una sola vez por test, **después de todas las publicaciones**: <c>InactivityTask</c> es
    /// una única tarea que se completa la primera vez que el bus queda inactivo, así que un
    /// segundo await no espera nada. Ver el <c>///</c> de la clase.
    /// </summary>
    private Task SettleAsync() => host.Harness.InactivityTask;

    private List<T> Published<T>()
        where T : class =>
        host.Harness.Published.Select<T>().Select(message => message.Context.Message).ToList();

    private List<T> Sent<T>()
        where T : class =>
        host.Harness.Sent.Select<T>().Select(message => message.Context.Message).ToList();

    private List<T> Consumed<T>()
        where T : class =>
        host.Harness.Consumed.Select<T>().Select(message => message.Context.Message).ToList();

    /// <summary>
    /// Ningún mensaje acabó en la cola de error, para los seis eventos que consume la saga.
    ///
    /// No es decoración: es lo que distingue "el duplicado se descartó" de "el duplicado
    /// reventó" y "el orden se respetó" de "el orden se rompió". Sin esta comprobación, varios
    /// de los tests de esta clase pasarían con las guardas borradas.
    /// </summary>
    private void AssertNoFaults()
    {
        Assert.Empty(Published<Fault<OrderCreated>>());
        Assert.Empty(Published<Fault<StockReserved>>());
        Assert.Empty(Published<Fault<StockRejected>>());
        Assert.Empty(Published<Fault<PaymentCompleted>>());
        Assert.Empty(Published<Fault<PaymentFailed>>());
        Assert.Empty(Published<Fault<StockReleased>>());
    }
}
