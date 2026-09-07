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
///
/// ── Qué añade 4.9, y por qué se tocaron los nueve tests que ya había ──
///
/// La saga gana <c>PricingPending</c> **delante** de <c>StockPending</c>, así que un
/// <c>StockReserved</c> ya no lleva a <c>PaymentPending</c> y los nueve tests anteriores se
/// pusieron rojos de golpe. La adaptación es insertarles el <c>OrderPricingValidated</c> donde
/// toca; que rompieran es la señal de que cubrían de verdad las transiciones.
///
/// Lo que 4.9 añade encima es la primera **rama paralela** de la saga: Catalog e Inventory
/// consumen el mismo <c>OrderCreated</c> del mismo fanout, así que sus dos respuestas llegan en
/// un orden que nadie garantiza. Esta clase recorre los dos órdenes a mano —publicando los
/// eventos en la secuencia que se quiere probar, que el <c>ConcurrentMessageLimit = 1</c>
/// convierte en determinista— porque una carrera que solo se prueba en el orden probable no
/// está probada.
///
/// **Y aquí es donde se desmiente el título de 4.9 en el roadmap** ("sin nada que compensar"):
/// ver <see cref="PricingRejected_WithStockAlreadyReserved_SendsExactlyOneReleaseStock"/>.
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
        await PublishAsync(PricingValidated(orderId));
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
        await PublishAsync(PricingValidated(orderId));
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
    /// El camino de error **corto**, desde <c>StockPending</c> — o sea con el precio ya
    /// validado. El <c>Reason</c> que compone Inventory viaja tal cual dentro del
    /// <c>OrderCancelled</c>: la saga no lo reescribe ni lo traduce a un código, porque quien
    /// mejor sabe por qué falló es quien falló.
    ///
    /// Desde 4.9 hay un segundo camino corto —el mismo rechazo llegando **antes** de que
    /// Catalog conteste— y tiene su propio test, porque sale de un estado distinto y publica
    /// sin esperar a nadie: ver
    /// <see cref="StockRejected_WithPricingStillPending_CancelsAndIgnoresTheLatePricingAnswer"/>.
    /// </summary>
    [Fact]
    public async Task StockRejected_ReachesCancelledAndPublishesOrderCancelledWithTheReason()
    {
        var orderId = Guid.NewGuid();
        const string reason = "el producto 999999 no existe en el inventario";

        await PublishAsync(OrderCreated(orderId));
        await PublishAsync(PricingValidated(orderId));
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
        await PublishAsync(PricingValidated(orderId));
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
        await PublishAsync(PricingValidated(orderId));
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
        await PublishAsync(PricingValidated(orderId));
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
        await PublishAsync(PricingValidated(orderId));
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
    /// <c>PricingPending</c> (la guarda que estrenó 4.1, que 4.9 muda a ese estado con el
    /// <c>Initially</c>) y <c>PaymentCompleted</c> repetido cae en <c>Confirmed</c>, o sea en
    /// un estado **terminal** — el <c>During(Confirmed, ...)</c> que parece código muerto y es
    /// el más fácil de borrar por error.
    /// </summary>
    [Fact]
    public async Task DuplicateEvents_ProduceASingleOrderConfirmedAndNoFaults()
    {
        var orderId = Guid.NewGuid();

        await PublishAsync(OrderCreated(orderId));
        await PublishAsync(OrderCreated(orderId));
        await PublishAsync(PricingValidated(orderId));
        await PublishAsync(new StockReserved { OrderId = orderId, Amount = MugPrice });
        await PublishAsync(PaymentCompleted(orderId));
        await PublishAsync(PaymentCompleted(orderId));
        await SettleAsync();

        AssertNoFaults();

        // Las seis entregas llegaron: sin esto el test podría pasar porque los duplicados se
        // perdieron por el camino, que es aprobar por el motivo equivocado.
        Assert.Equal(2, Consumed<OrderCreated>().Count);
        Assert.Equal(2, Consumed<PaymentCompleted>().Count);

        // Un solo efecto. Dos OrderConfirmed significarían dos emails y dos Order.Confirm(),
        // el segundo de los cuales lanza (4.3).
        Assert.Single(Published<OrderConfirmed>());
        Assert.Equal(nameof(OrderStateMachine.Confirmed), host.State(orderId));
    }

    // ── 4.9: la rama paralela precio/stock ───────────────────────────────────
    //
    // Catalog e Inventory consumen el mismo OrderCreated del mismo exchange fanout, así que
    // sus dos respuestas llegan en un orden que nadie garantiza. Los seis tests que siguen
    // recorren esa unión: los dos órdenes del camino feliz, y las tres formas en que un
    // rechazo de precio puede encontrarse la reserva de stock.

    /// <summary>
    /// **La carrera, en el orden que hasta 4.8 no existía**: Inventory contesta antes que
    /// Catalog. El pedido llega igual a <c>Confirmed</c>, pasando por
    /// <c>PricingPendingStockReserved</c> en vez de por <c>StockPending</c>.
    ///
    /// Es el test que justifica que el join se modele con estados: los dos órdenes de llegada
    /// convergen en <c>PaymentPending</c>, y ninguno de los dos "salta" la validación de
    /// precio — que es la tentación obvia cuando el stock ya está apartado y lo único que
    /// falta es Catalog.
    /// </summary>
    [Fact]
    public async Task HappyPath_WithStockArrivingBeforePricing_ReachesConfirmedAllTheSame()
    {
        var orderId = Guid.NewGuid();

        await PublishAsync(OrderCreated(orderId));
        await PublishAsync(new StockReserved { OrderId = orderId, Amount = MugPrice });
        await PublishAsync(PricingValidated(orderId));
        await PublishAsync(PaymentCompleted(orderId));
        await SettleAsync();

        AssertNoFaults();

        Assert.Single(Published<OrderConfirmed>());
        Assert.Equal(nameof(OrderStateMachine.Confirmed), host.State(orderId));

        Assert.Empty(Sent<ReleaseStock>());
        Assert.Empty(Published<OrderCancelled>());
    }

    /// <summary>
    /// **El test que desmiente el título de 4.9 en el roadmap.**
    ///
    /// Ese título dice "<c>OrderPricingRejected → Cancelled</c> **sin nada que compensar**".
    /// Aquí el stock ya está reservado cuando llega el rechazo de precio, así que sí hay algo
    /// que compensar: la saga manda *exactamente un* <c>ReleaseStock</c> y solo cancela cuando
    /// Inventory contesta. Es la misma maquinaria de 4.4 alcanzada por un camino nuevo.
    ///
    /// El <c>///</c> de <c>OrderPricingRejected</c> avisó de esto en 4.8 —deliberadamente no
    /// prometió lo que sí promete <c>StockRejected</c>— y encargó releerlo con la máquina de
    /// estados delante. Esto es ese releído, en forma ejecutable.
    /// </summary>
    [Fact]
    public async Task PricingRejected_WithStockAlreadyReserved_SendsExactlyOneReleaseStock()
    {
        var orderId = Guid.NewGuid();
        const string reason = "el producto 1 (TAZA-001) se pidió a 0.01 y su precio es 249.00";

        await PublishAsync(OrderCreated(orderId));
        await PublishAsync(new StockReserved { OrderId = orderId, Amount = MugPrice });
        await PublishAsync(new OrderPricingRejected { OrderId = orderId, Reason = reason });
        await SettleAsync();

        AssertNoFaults();

        var release = Assert.Single(Sent<ReleaseStock>());
        Assert.Equal(orderId, release.OrderId);
        Assert.Single(Consumed<ReleaseStock>());
        Assert.Single(Published<StockReleased>());

        // El motivo sale de la instancia, guardado una transición antes: StockReleased no lleva
        // texto. Es el mismo mecanismo que el camino de PaymentFailed, y desde 4.9 son tres los
        // caminos que escriben OrderState.CancellationReason.
        var cancelled = Assert.Single(Published<OrderCancelled>());
        Assert.Equal(reason, cancelled.Reason);
        Assert.Equal(CustomerEmail, cancelled.CustomerEmail);

        Assert.Equal(nameof(OrderStateMachine.Cancelled), host.State(orderId));
        Assert.Empty(Published<OrderConfirmed>());
    }

    /// <summary>
    /// El rechazo de precio llega **antes** de que Inventory conteste, así que todavía no se
    /// sabe si hay algo que soltar. La saga no cancela: espera en
    /// <c>CancellingStockPending</c>, y cuando llega el <c>StockReserved</c> compensa.
    ///
    /// **Sin ese estado intermedio esto sería la regla 7 rota en silencio**: cancelar al
    /// recibir el rechazo dejaría el <c>StockReserved</c> posterior cayendo en
    /// <c>Cancelled</c>, donde se ignora, con las unidades apartadas para siempre y el pedido
    /// perfectamente cerrado. Ningún assert de estado final lo detectaría — por eso el que
    /// importa aquí es el recuento de <c>ReleaseStock</c>.
    /// </summary>
    [Fact]
    public async Task PricingRejected_BeforeInventoryAnswers_CompensatesWhenTheReservationArrives()
    {
        var orderId = Guid.NewGuid();
        const string reason = "el total 0.01 no cuadra con la suma de las líneas (249.00)";

        await PublishAsync(OrderCreated(orderId));
        await PublishAsync(new OrderPricingRejected { OrderId = orderId, Reason = reason });
        await PublishAsync(new StockReserved { OrderId = orderId, Amount = MugPrice });
        await SettleAsync();

        AssertNoFaults();

        Assert.Single(Sent<ReleaseStock>());
        Assert.Single(Published<StockReleased>());

        var cancelled = Assert.Single(Published<OrderCancelled>());
        Assert.Equal(reason, cancelled.Reason);

        Assert.Equal(nameof(OrderStateMachine.Cancelled), host.State(orderId));
    }

    /// <summary>
    /// La otra salida de <c>CancellingStockPending</c>: Inventory **tampoco** pudo reservar.
    /// No hay nada que devolver, así que el pedido se cierra sin compensación — el único
    /// camino en el que el "sin nada que compensar" del título de 4.9 acierta.
    ///
    /// Los **dos** motivos son ciertos a la vez (precio falso y sin stock) y se publica el de
    /// precio, que es el que la saga tenía guardado y el que la llevó a dar el pedido por
    /// perdido. No se concatenan: el <c>///</c> de <c>OrderCancelled</c> promete un texto para
    /// que el cliente entienda qué pasó, no un inventario de todo lo que falló.
    /// </summary>
    [Fact]
    public async Task PricingRejected_BeforeInventoryAnswers_WhenStockIsAlsoRejected_DoesNotCompensate()
    {
        var orderId = Guid.NewGuid();
        const string pricingReason = "el producto 1 (TAZA-001) se pidió a 0.01 y su precio es 249.00";

        await PublishAsync(OrderCreated(orderId));
        await PublishAsync(new OrderPricingRejected { OrderId = orderId, Reason = pricingReason });
        await PublishAsync(new StockRejected { OrderId = orderId, Reason = "sin unidades" });
        await SettleAsync();

        AssertNoFaults();

        // Nada que soltar: Inventory no apartó ninguna unidad. Soltar aquí sería devolver
        // unidades que nunca se movieron, o sea crearlas de la nada.
        Assert.Empty(Sent<ReleaseStock>());
        Assert.Empty(Published<StockReleased>());

        var cancelled = Assert.Single(Published<OrderCancelled>());
        Assert.Equal(pricingReason, cancelled.Reason);

        Assert.Equal(nameof(OrderStateMachine.Cancelled), host.State(orderId));
    }

    /// <summary>
    /// El segundo camino corto que estrena 4.9: Inventory rechaza **con el precio todavía sin
    /// validar**, y la saga cierra el pedido sin esperar a Catalog.
    ///
    /// Es seguro porque el pedido está perdido pase lo que pase con el precio y porque no hay
    /// nada apartado (la reserva de Inventory es atómica). Y descartar la respuesta de Catalog
    /// no deja rastro: validar precios **no escribe nada de negocio** — es la propiedad de
    /// <c>OrderCreatedPricingConsumer</c> que en 4.8 hizo que su idempotencia saliera distinta
    /// de la de los otros cuatro servicios.
    ///
    /// La segunda mitad del test es la que se olvida: el <c>OrderPricingValidated</c> que llega
    /// tarde cae en <c>Cancelled</c> y tiene que ignorarse. **Sin esa guarda, todo pedido sin
    /// stock dejaría un mensaje en <c>order-state_error</c>** — no es el caso raro, es el
    /// normal.
    /// </summary>
    [Fact]
    public async Task StockRejected_WithPricingStillPending_CancelsAndIgnoresTheLatePricingAnswer()
    {
        var orderId = Guid.NewGuid();
        const string reason = "el producto 999999 no existe en el inventario";

        await PublishAsync(OrderCreated(orderId));
        await PublishAsync(new StockRejected { OrderId = orderId, Reason = reason });
        await PublishAsync(PricingValidated(orderId));
        await SettleAsync();

        AssertNoFaults();

        var cancelled = Assert.Single(Published<OrderCancelled>());
        Assert.Equal(reason, cancelled.Reason);

        Assert.Equal(nameof(OrderStateMachine.Cancelled), host.State(orderId));

        // La respuesta tardía llegó de verdad y se ignoró, que es lo que AssertNoFaults()
        // demuestra. Sin esta línea el test podría pasar porque el mensaje se perdió.
        Assert.Single(Consumed<OrderPricingValidated>());
    }

    /// <summary>
    /// **La inversión que la planificación de 4.9 dio por rara y resultó ser la mitad de los
    /// pedidos.** La rama de Inventory no acaba en el <c>StockReserved</c>: Payments consume
    /// ese mismo evento del fanout y cobra sin esperar a nadie, así que la rama entera puede
    /// completarse antes de que Catalog conteste.
    ///
    /// Medido contra el compose real antes de escribir este test: de siete pedidos legítimos,
    /// **cuatro** los ganó Inventory. Sin <c>PricingPendingPaymentCompleted</c> esos pedidos
    /// dependían de que el <c>UseMessageRetry</c> reordenara la entrega dentro de su ventana de
    /// ~500 ms, o sea de que Catalog no se retrasara — que es justo lo que no se puede suponer
    /// de un servicio que puede caerse.
    /// </summary>
    [Fact]
    public async Task HappyPath_WithTheWholeStockAndPaymentBranchBeforePricing_ReachesConfirmed()
    {
        var orderId = Guid.NewGuid();

        await PublishAsync(OrderCreated(orderId));
        await PublishAsync(new StockReserved { OrderId = orderId, Amount = MugPrice });
        await PublishAsync(PaymentCompleted(orderId));
        await PublishAsync(PricingValidated(orderId));
        await SettleAsync();

        AssertNoFaults();

        var confirmed = Assert.Single(Published<OrderConfirmed>());
        Assert.Equal(CustomerEmail, confirmed.CustomerEmail);

        Assert.Equal(nameof(OrderStateMachine.Confirmed), host.State(orderId));
        Assert.Empty(Sent<ReleaseStock>());
        Assert.Empty(Published<OrderCancelled>());
    }

    /// <summary>
    /// **El caso peor del proyecto**: el precio era falso y el cobro ya se había aceptado.
    ///
    /// La saga suelta el stock y cancela, que es todo lo que puede hacer — **el cobro no se
    /// devuelve, porque no existe ningún contrato de reembolso** en <c>Shop133.Contracts</c>.
    /// El <c>TransactionId</c> que <c>PaymentCompleted</c> lleva desde 0.3 "para poder emitir
    /// el reembolso" sigue sin nadie que lo use, y este test es donde eso se ve. Queda como
    /// hueco anotado, no como algo que 4.9 arregle inventando un decimotercer contrato.
    ///
    /// Lo que el test sí exige es que la mitad que la saga **puede** cumplir se cumpla: una
    /// sola liberación de stock y el pedido cerrado.
    /// </summary>
    [Fact]
    public async Task PricingRejected_AfterThePaymentWasAccepted_ReleasesStockAndCancels()
    {
        var orderId = Guid.NewGuid();
        const string reason = "el producto 1 (TAZA-001) se pidió a 0.01 y su precio es 249.00";

        await PublishAsync(OrderCreated(orderId));
        await PublishAsync(new StockReserved { OrderId = orderId, Amount = MugPrice });
        await PublishAsync(PaymentCompleted(orderId));
        await PublishAsync(new OrderPricingRejected { OrderId = orderId, Reason = reason });
        await SettleAsync();

        AssertNoFaults();

        Assert.Single(Sent<ReleaseStock>());
        Assert.Single(Published<StockReleased>());

        var cancelled = Assert.Single(Published<OrderCancelled>());
        Assert.Equal(reason, cancelled.Reason);

        Assert.Equal(nameof(OrderStateMachine.Cancelled), host.State(orderId));

        // El pedido NO se confirma, aunque el cobro esté hecho: es lo que distingue "cobrado"
        // de "vendido".
        Assert.Empty(Published<OrderConfirmed>());
    }

    /// <summary>
    /// El cobro se rechaza con el precio todavía sin validar. Condena el pedido diga lo que
    /// diga Catalog y hay stock apartado, así que se entra en la compensación de 4.4 **sin
    /// esperar** — y la respuesta de Catalog, que llega después, se ignora en
    /// <c>CompensatingStock</c>.
    ///
    /// Es uno de los dos cruces del join que deliberadamente no tienen estado propio: cuando
    /// el desenlace ya no depende de Catalog, esperarle sería inventar una espera.
    /// </summary>
    [Fact]
    public async Task PaymentFailed_WithPricingStillPending_CompensatesWithoutWaitingForCatalog()
    {
        var orderId = Guid.NewGuid();
        const string reason = "el importe 1197.00 supera el límite autorizado";

        await PublishAsync(OrderCreated(orderId));
        await PublishAsync(new StockReserved { OrderId = orderId, Amount = 1197.00m });
        await PublishAsync(new PaymentFailed { OrderId = orderId, Reason = reason });
        await PublishAsync(PricingValidated(orderId));
        await SettleAsync();

        AssertNoFaults();

        Assert.Single(Sent<ReleaseStock>());
        Assert.Single(Published<StockReleased>());

        var cancelled = Assert.Single(Published<OrderCancelled>());
        Assert.Equal(reason, cancelled.Reason);

        Assert.Equal(nameof(OrderStateMachine.Cancelled), host.State(orderId));

        // La respuesta tardía de Catalog llegó y se ignoró — lo demuestra AssertNoFaults().
        Assert.Single(Consumed<OrderPricingValidated>());
    }

    /// <summary>
    /// La idempotencia de los dos eventos que 4.9 estrena, con la misma mecánica que el
    /// escenario 4: duplicados con <c>MessageId</c> distintos —reconocen el mismo *pedido*, no
    /// la misma *entrega*— y **el assert que de verdad prueba la guarda es
    /// <c>AssertNoFaults()</c>**, no el recuento (trampa 3 de docs/fase_3_7.md, ya confirmada
    /// cuatro veces en este repositorio).
    ///
    /// Los duplicados caen en dos sitios distintos a propósito: el primero en
    /// <c>StockPending</c>, o sea el estado al que el propio evento acaba de llevar a la saga,
    /// y el segundo en <c>Confirmed</c>, un estado **terminal** — el <c>During</c> que parece
    /// código muerto.
    /// </summary>
    [Fact]
    public async Task DuplicatePricingValidated_ProducesASingleEffectAndNoFaults()
    {
        var orderId = Guid.NewGuid();

        await PublishAsync(OrderCreated(orderId));
        await PublishAsync(PricingValidated(orderId));
        await PublishAsync(PricingValidated(orderId));
        await PublishAsync(new StockReserved { OrderId = orderId, Amount = MugPrice });
        await PublishAsync(PaymentCompleted(orderId));
        await PublishAsync(PricingValidated(orderId));
        await SettleAsync();

        AssertNoFaults();

        Assert.Equal(3, Consumed<OrderPricingValidated>().Count);

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

    /// <summary>
    /// La respuesta de Catalog que 4.9 mete delante de todo. Se le da nombre de ayuda —en vez
    /// de escribir el <c>new</c> a pelo como con <c>StockReserved</c>— porque aparece en trece
    /// de los quince tests de la clase y una línea corta ahí se lee mejor.
    /// </summary>
    private static OrderPricingValidated PricingValidated(Guid orderId) => new()
    {
        OrderId = orderId,
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
    /// Ningún mensaje acabó en la cola de error, para los **ocho** eventos que consume la saga
    /// desde 4.9.
    ///
    /// No es decoración: es lo que distingue "el duplicado se descartó" de "el duplicado
    /// reventó" y "el orden se respetó" de "el orden se rompió". Sin esta comprobación, varios
    /// de los tests de esta clase pasarían con las guardas borradas.
    ///
    /// Las dos líneas que añade 4.9 no son ceremonia: con la rama paralela, la mitad de los
    /// tests nuevos publican los eventos en un orden que solo es aceptable si las guardas están
    /// donde tienen que estar, y un fault es la única señal de que no lo estaban.
    /// </summary>
    private void AssertNoFaults()
    {
        Assert.Empty(Published<Fault<OrderCreated>>());
        Assert.Empty(Published<Fault<OrderPricingValidated>>());
        Assert.Empty(Published<Fault<OrderPricingRejected>>());
        Assert.Empty(Published<Fault<StockReserved>>());
        Assert.Empty(Published<Fault<StockRejected>>());
        Assert.Empty(Published<Fault<PaymentCompleted>>());
        Assert.Empty(Published<Fault<PaymentFailed>>());
        Assert.Empty(Published<Fault<StockReleased>>());
    }
}
