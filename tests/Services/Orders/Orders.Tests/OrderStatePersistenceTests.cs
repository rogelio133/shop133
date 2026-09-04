using MassTransit;

using Orders.Domain.Sagas;
using Orders.Tests.Infrastructure;

using Shop133.Contracts;
using Shop133.Contracts.Events;
using Shop133.TestUtilities;

using Xunit;

namespace Orders.Tests;

/// <summary>
/// La saga **persistida**: lo que entregó 4.5 y que hasta hoy no cubría ni un test.
///
/// La sección Pendiente de docs/fase_4_5.md lo dejó escrito con todas las letras — *"nada de
/// este punto está cubierto por un test […] los 71 tests pasan exactamente igual con el código
/// de 4.4"*—, porque <see cref="OrdersApiFactory"/> borra todo MassTransit y con él el
/// repositorio EF. Estos cuatro tests son la respuesta.
///
/// **Qué se prueba aquí y no en <see cref="OrderStateMachineTests"/>**: que la instancia es una
/// fila de <c>OrdersDb.OrderStates</c> con lo que tiene que llevar dentro, que su
/// <c>rowversion</c> la escribe SQL Server, que un pedido cerrado **conserva su fila** y —el
/// que de verdad justifica 4.5— que la saga **sobrevive a que el proceso se caiga**.
///
/// Las transiciones y los mensajes que salen no se repiten aquí: son idénticos con los dos
/// repositorios, porque 4.5 no cambió ni una línea de la máquina de estados. Duplicarlos
/// costaría SQL Server para no afirmar nada nuevo.
///
/// **Fuera, y con dueño**: el outbox transaccional y el choque real de concurrencia optimista
/// —que necesita dos entregas simultáneas, o sea justo lo contrario del
/// <c>ConcurrentMessageLimit = 1</c> que hace deterministas estas suites— son de **8.2**, que
/// ya los reclama por escrito en el roadmap.
/// </summary>
[Collection(OrdersApiCollection.Name)]
[Trait("Category", "Docker")]
public sealed class OrderStatePersistenceTests(SqlServerContainerFixture container) : IAsyncLifetime
{
    private const string CustomerEmail = "cliente@shop133.test";

    private const decimal MugPrice = 249.00m;

    private readonly OrderSagaDbHost host = new(container);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => host.InitializeAsync();

    public ValueTask DisposeAsync() => host.DisposeAsync();

    /// <summary>
    /// Arrancar la saga escribe una fila, y la fila lleva lo que <c>OrderStateConfiguration</c>
    /// mapea: la clave de correlación **es** el <c>OrderId</c> (decisión 5 de
    /// docs/fase_0_3.md, que descartó meter un <c>CorrelationId</c> en los contratos), el
    /// estado por nombre —<c>string</c> y no <c>int</c>, para que la tabla se lea sin
    /// descifrar nada— y el email copiado en el <c>Initially</c>.
    /// </summary>
    [Fact]
    public async Task SagaStarted_WritesTheInstanceInOrderStates()
    {
        var orderId = Guid.NewGuid();

        await PublishAsync(OrderCreated(orderId));
        await SettleAsync();

        var instance = await host.InstanceAsync(orderId, CancellationToken);

        Assert.NotNull(instance);
        Assert.Equal(orderId, instance.CorrelationId);
        Assert.Equal(nameof(OrderStateMachine.StockPending), instance.CurrentState);
        Assert.Equal(CustomerEmail, instance.CustomerEmail);
        Assert.NotEqual(default, instance.CreatedAt);

        // La rowversion la rellena SQL Server, no el código: IsRowVersion() la mapea a una
        // columna que el motor incrementa en cada UPDATE. Si esto sale vacío, el token de
        // concurrencia optimista de 4.5 no existe y el UseMessageRetry no protege nada.
        Assert.NotEmpty(instance.RowVersion);

        Assert.Equal(1, await host.CountInstancesAsync(CancellationToken));
    }

    /// <summary>
    /// Cada transición avanza la <c>rowversion</c>. Es lo que hace detectable un choque entre
    /// dos entregas del mismo pedido: EF mete el valor leído en el <c>WHERE</c> del
    /// <c>UPDATE</c>, así que si otro mensaje pisó la fila el update afecta a cero filas y
    /// salta <c>DbUpdateConcurrencyException</c>.
    ///
    /// Lo que este test **no** demuestra es que ese choque se resuelva bien: forzarlo necesita
    /// dos entregas a la vez, y es de 8.2. Aquí solo se comprueba que la mitad pasiva de la
    /// protección —la columna— está viva y se mueve.
    /// </summary>
    [Fact]
    public async Task EachTransition_AdvancesTheRowVersion()
    {
        var orderId = Guid.NewGuid();

        await PublishAsync(OrderCreated(orderId));

        var afterStart = await host.WaitForStateAsync(
            orderId, nameof(OrderStateMachine.StockPending), CancellationToken);

        await PublishAsync(new StockReserved { OrderId = orderId, Amount = MugPrice });

        var afterReservation = await host.WaitForStateAsync(
            orderId, nameof(OrderStateMachine.PaymentPending), CancellationToken);

        Assert.NotEqual(afterStart.RowVersion, afterReservation.RowVersion);
    }

    /// <summary>
    /// **El test que justifica 4.5 entero.**
    ///
    /// Se arranca la saga, se tira el proveedor —el bus, el DbContext y el repositorio se van
    /// con él— y se levanta uno nuevo **contra la misma base**. El pedido termina. Con el
    /// <c>InMemoryRepository()</c> que Orders.API tuvo entre 4.1 y 4.4 esto es rojo: la
    /// verificación 7 de docs/fase_4_1.md midió que un reinicio borraba todas las instancias,
    /// así que un pedido esperando su cobro se quedaba sin nadie que lo moviera y, desde 4.4,
    /// con la consecuencia repartida en dos bases de datos.
    ///
    /// Nótese que **la instancia sigue siendo la misma fila**, no una nueva: la saga no vuelve
    /// a arrancar, se lee. Por eso el estado tras el reinicio ya es <c>StockPending</c> y no
    /// vuelve a pasar por el <c>Initially</c> — que es lo que el bus nuevo no podría saber si
    /// la instancia no estuviera escrita.
    /// </summary>
    [Fact]
    public async Task AfterRestartingTheBus_TheSagaResumesFromTheStoredRow()
    {
        var orderId = Guid.NewGuid();

        await PublishAsync(OrderCreated(orderId));

        var beforeRestart = await host.WaitForStateAsync(
            orderId, nameof(OrderStateMachine.StockPending), CancellationToken);

        await host.RestartBusAsync();

        // El proceso nuevo no ha visto nunca el OrderCreated de este pedido.
        await PublishAsync(new StockReserved { OrderId = orderId, Amount = MugPrice });
        await PublishAsync(PaymentCompleted(orderId));
        await SettleAsync();

        Assert.Empty(Published<Fault<StockReserved>>());
        Assert.Empty(Published<Fault<PaymentCompleted>>());

        var confirmed = Assert.Single(Published<OrderConfirmed>());
        Assert.Equal(orderId, confirmed.OrderId);

        // Y el email sale de la fila, no de un mensaje: nada de lo que se publicó después del
        // reinicio lo lleva. Es la prueba de que la instancia se leyó y no se reinventó.
        Assert.Equal(CustomerEmail, confirmed.CustomerEmail);

        var instance = await host.InstanceAsync(orderId, CancellationToken);
        Assert.NotNull(instance);
        Assert.Equal(nameof(OrderStateMachine.Confirmed), instance.CurrentState);
        Assert.Equal(beforeRestart.CreatedAt, instance.CreatedAt);

        // Una fila, no dos: la saga continuó, no arrancó de cero.
        Assert.Equal(1, await host.CountInstancesAsync(CancellationToken));
    }

    /// <summary>
    /// Un pedido terminado **conserva su fila**, con el estado final dentro.
    ///
    /// Es la decisión que 4.2 aplazó y 4.5 cerró con la tabla delante: <c>Confirmed</c> y
    /// <c>Cancelled</c> son estados normales y no <c>Finalize()</c>, porque el desenlace de un
    /// pedido tiene que poder consultarse después. Este test es una de las dos razones que se
    /// dieron entonces —"lo que 4.7 necesita para afirmar el estado final"—; la otra es la
    /// página de estado del pedido de 6.5.
    ///
    /// El precio, dicho en voz alta y sin resolver: la tabla crece sin techo y nadie la purga,
    /// igual que <c>ProcessedMessages</c>.
    /// </summary>
    [Fact]
    public async Task TerminalSaga_KeepsItsRow()
    {
        var orderId = Guid.NewGuid();

        await PublishAsync(OrderCreated(orderId));
        await PublishAsync(new StockRejected { OrderId = orderId, Reason = "sin unidades" });
        await SettleAsync();

        Assert.Single(Published<OrderCancelled>());

        var instance = await host.InstanceAsync(orderId, CancellationToken);

        Assert.NotNull(instance);
        Assert.Equal(nameof(OrderStateMachine.Cancelled), instance.CurrentState);
        Assert.Equal(1, await host.CountInstancesAsync(CancellationToken));
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
                ProductId = 1,
                ProductSku = "TAZA-001",
                ProductName = "Taza Talavera Puebla",
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

    private Task PublishAsync<T>(T message)
        where T : class =>
        host.Harness.Bus.Publish(message, context => context.MessageId = Guid.NewGuid(), CancellationToken);

    /// <summary>
    /// **Una sola vez por bus, y por eso los tests de dos tandas no lo usan.**
    /// <c>InactivityTask</c> se completa la primera vez que el bus queda ocioso y nunca más
    /// (trampa 1 de docs/fase_3_7.md), así que aquí solo lo llaman los tests de una sola
    /// tanda; los que necesitan leer la fila entre publicaciones esperan con
    /// <c>host.WaitForStateAsync(...)</c>, que sondea la tabla. Ver su <c>///</c>, donde está
    /// escrito cómo se descubrió — con este mismo error, en rojo.
    ///
    /// El test del reinicio sí lo usa después de dos tandas, y es correcto: entre una y otra
    /// se **destruye el bus y se crea otro**, así que el <c>InactivityTask</c> que se espera
    /// es el del segundo, sin estrenar.
    /// </summary>
    private Task SettleAsync() => host.Harness.InactivityTask;

    private List<T> Published<T>()
        where T : class =>
        host.Harness.Published.Select<T>().Select(message => message.Context.Message).ToList();
}
