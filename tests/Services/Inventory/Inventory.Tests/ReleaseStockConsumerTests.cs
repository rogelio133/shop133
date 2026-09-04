using Inventory.API.Consumers;
using Inventory.Tests.Infrastructure;

using MassTransit;

using Shop133.Contracts.Commands;
using Shop133.Contracts.Events;
using Shop133.TestUtilities;

using Xunit;

namespace Inventory.Tests;

/// <summary>
/// <see cref="ReleaseStockConsumer"/> — la compensación de 4.4.
///
/// **Es el escenario 3 de los cuatro obligatorios del roadmap visto desde
/// Inventory**: stock reservado, pago rechazado, unidades devueltas. La otra mitad
/// —que la saga publique exactamente un ReleaseStock y que el pedido acabe en
/// Cancelled— es 4.7, con el harness contra la OrderStateMachine; aquí se prueba el
/// otro extremo del cable.
///
/// El comando se manda con <c>Send</c> a **queue:release-stock**, la misma URI
/// literal que escribe la <c>OrderStateMachine</c>. De paso, eso comprueba el único
/// acuerdo del proyecto que no vigila el compilador: que el nombre de esta cola y el
/// destino que escribe la saga sean el mismo. Nada más lo tocaría — un desacuerdo no
/// produce ningún error, solo comandos apilándose en una cola que nadie lee.
/// Lo que sigue sin comprobarse es que <c>Program.cs</c> registre los consumers;
/// hueco de 8.2, heredado de 3.7.
///
/// **La reserva se siembra por base de datos, no publicando un OrderCreated**, y esa
/// decisión tiene un motivo medido que está escrito en
/// <c>InventoryConsumerHost.SeedReservationAsync</c>: dos etapas de bus por test
/// rompen el <c>InactivityTask</c>, que es de un solo uso.
///
/// Cantidades del seed de 3.4 que se usan aquí: producto 1 → 42 unidades,
/// producto 2 → 65.
/// </summary>
[Collection(InventoryConsumerCollection.Name)]
[Trait("Category", "Docker")]
public sealed class ReleaseStockConsumerTests(SqlServerContainerFixture container) : IAsyncLifetime
{
    private const int MugId = 1;
    private const int MugOnHand = 42;
    private const int KeyringId = 2;

    /// <summary>
    /// La dirección literal que escribe <c>OrderStateMachine</c>. Se repite aquí a
    /// propósito en vez de importarla: la constante de la saga es privada y, aunque
    /// no lo fuera, un test que la reutilizara pasaría igual si las dos estuvieran
    /// mal. Lo que hay que comprobar es que dos sitios independientes coinciden.
    /// </summary>
    private static readonly Uri ReleaseStockEndpoint = new("queue:release-stock");

    private readonly InventoryConsumerHost host = new(container);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => host.InitializeAsync();

    public ValueTask DisposeAsync() => host.DisposeAsync();

    // ── El camino de la compensación ─────────────────────────────────────────

    [Fact]
    public async Task Consume_ReservedOrder_ReturnsTheUnitsAndPublishesStockReleased()
    {
        var orderId = Guid.NewGuid();

        await ReserveAsync(orderId, (MugId, 3), (KeyringId, 2));
        await SendReleaseAsync(orderId);
        await SettleAsync();

        var released = Assert.Single(Published<StockReleased>());
        Assert.Equal(orderId, released.OrderId);

        // Lo que este punto existe para conseguir: las unidades vuelven, y sin que
        // nadie intervenga a mano.
        Assert.Equal(0, await host.QuantityReservedAsync(MugId, CancellationToken));
        Assert.Equal(0, await host.QuantityReservedAsync(KeyringId, CancellationToken));

        // Y las físicas no se han movido en ningún momento — ni al reservar ni al
        // soltar. Es lo que hace que devolver unidades no sea indistinguible de
        // inventarlas, y el motivo de que 3.4 no usara una sola columna.
        Assert.Equal(MugOnHand, await host.QuantityOnHandAsync(MugId, CancellationToken));

        Assert.NotNull(await host.ReleasedAtAsync(orderId, CancellationToken));
    }

    /// <summary>
    /// La fila de la reserva sigue ahí después de liberarla, con sus líneas. Es la
    /// decisión de marcar en vez de borrar, en forma de assert: sin fila no habría
    /// rastro de que la compensación ocurrió, no se podría distinguir "ya liberada"
    /// de "nunca reservada", y la guarda de negocio de <c>OrderCreatedConsumer</c>
    /// dejaría pasar una reserva nueva del mismo pedido ya cancelado.
    /// </summary>
    [Fact]
    public async Task Consume_ReservedOrder_KeepsTheReservationRowAsEvidence()
    {
        var orderId = Guid.NewGuid();

        await ReserveAsync(orderId, (MugId, 3));
        await SendReleaseAsync(orderId);
        await SettleAsync();

        Assert.Single(Published<StockReleased>());
        Assert.Equal(1, await host.CountReservationsAsync(CancellationToken));

        var reservation = await host.ReservationAsync(orderId, CancellationToken);

        Assert.NotNull(reservation);
        Assert.Equal([(MugId, 3)], reservation.Lines);
    }

    // ── Idempotencia ─────────────────────────────────────────────────────────

    /// <summary>
    /// La guarda de **transporte** (3.6). Misma entrega dos veces ⇒ un solo efecto.
    ///
    /// Es el test más importante de la clase: sin la guarda, el segundo
    /// <c>Release</c> devolvería otras 3 unidades que nadie reservó — **unidades
    /// creadas de la nada**, que es lo que el <c>///</c> de ReleaseStock avisa que es
    /// peor que un duplicado de reserva.
    /// </summary>
    [Fact]
    public async Task Consume_SameMessageIdTwice_ReleasesOnceAndPublishesASingleStockReleased()
    {
        var orderId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        await ReserveAsync(orderId, (MugId, 3));
        await SendReleaseAsync(orderId, messageId);
        await SendReleaseAsync(orderId, messageId);
        await SettleAsync();

        Assert.Single(Published<StockReleased>());

        // **El assert que de verdad prueba la guarda**: sin ella la segunda entrega
        // tampoco publica —muere antes, en el Release() de la reserva ya sellada o en
        // el INSERT duplicado de ProcessedMessages— así que el recuento de arriba
        // sale 1 en los dos casos. Lo que distingue "descartado en silencio" de
        // "explotó" es el fault.
        Assert.Empty(Published<Fault<ReleaseStock>>());

        Assert.Equal(0, await host.QuantityReservedAsync(MugId, CancellationToken));

        // Una sola fila, y con ConsumerName = ReleaseStockConsumer: es la primera
        // vez que Inventory escribe en ProcessedMessages con un nombre distinto del
        // de OrderCreatedConsumer, o sea la primera vez que su PK compuesta hace
        // falta de verdad. Aquí sale 1 porque la reserva se sembró por base de datos.
        Assert.Equal(1, await host.CountProcessedAsync(CancellationToken));
    }

    /// <summary>
    /// La guarda de **negocio** (por <c>ReleasedAt</c>), que no es la de arriba: un
    /// MessageId nuevo es alguien que ha vuelto a preguntar, así que aquí sí se
    /// reenvía <c>StockReleased</c> — quien lo espera es una saga que sin él no sale
    /// de <c>CompensatingStock</c>.
    /// </summary>
    [Fact]
    public async Task Consume_SameOrderWithANewMessageId_RepublishesStockReleasedWithoutReleasingTwice()
    {
        var orderId = Guid.NewGuid();

        await ReserveAsync(orderId, (MugId, 3));
        await SendReleaseAsync(orderId);
        await SendReleaseAsync(orderId);
        await SettleAsync();

        Assert.Equal(2, Published<StockReleased>().Count);
        Assert.Empty(Published<Fault<ReleaseStock>>());

        // Pero suelta una sola vez: 0 reservadas, no -3. Sin la guarda, el segundo
        // Release() de la reserva lanzaría y esto sería un fault.
        Assert.Equal(0, await host.QuantityReservedAsync(MugId, CancellationToken));
        Assert.Equal(MugOnHand, await host.QuantityOnHandAsync(MugId, CancellationToken));
    }

    // ── Incoherencias ────────────────────────────────────────────────────────

    /// <summary>
    /// Un pedido sin reserva revienta. La saga solo manda este comando desde
    /// <c>PaymentPending</c>, al que únicamente se llega por el <c>StockReserved</c>
    /// que publicó este mismo servicio, así que la fila tendría que existir.
    ///
    /// Se afirma también que **no sale ningún StockReleased**: contestar que se soltó
    /// algo que nunca se reservó sacaría a la saga de <c>CompensatingStock</c> con
    /// una mentira. Que el pedido se quede esperando y el mensaje quede visible en la
    /// cola de error es el desenlace correcto de una incoherencia.
    /// </summary>
    [Fact]
    public async Task Consume_OrderWithoutReservation_FaultsAndPublishesNoStockReleased()
    {
        await SendReleaseAsync(Guid.NewGuid());
        await SettleAsync();

        var fault = Assert.Single(Published<Fault<ReleaseStock>>());
        Assert.Contains(
            fault.Exceptions,
            exception => exception.ExceptionType == typeof(InvalidOperationException).FullName);

        Assert.Empty(Published<StockReleased>());
    }

    [Fact]
    public async Task Consume_MessageWithoutMessageId_FaultsAndReleasesNothing()
    {
        var orderId = Guid.NewGuid();

        await ReserveAsync(orderId, (MugId, 3));

        var endpoint = await host.Harness.Bus.GetSendEndpoint(ReleaseStockEndpoint);
        await endpoint.Send(
            new ReleaseStock { OrderId = orderId },
            context => context.MessageId = null,
            CancellationToken);

        await SettleAsync();

        Assert.Single(Published<Fault<ReleaseStock>>());
        Assert.Empty(Published<StockReleased>());

        // Lo que importa: no ha soltado nada. Un consumer que no puede deduplicar no
        // suelta stock "por si acaso" — soltar de más crea unidades de la nada.
        Assert.Equal(3, await host.QuantityReservedAsync(MugId, CancellationToken));
        Assert.Null(await host.ReleasedAtAsync(orderId, CancellationToken));
    }

    // ── Ayudas ───────────────────────────────────────────────────────────────

    private Task ReserveAsync(Guid orderId, params (int ProductId, int Quantity)[] lines) =>
        host.SeedReservationAsync(orderId, lines, CancellationToken);

    /// <summary>
    /// Manda el comando a <c>queue:release-stock</c> con <c>Send</c>, igual que la
    /// saga. *Descartado* publicarlo: el transporte lo entregaría igual —MassTransit
    /// liga al consumer por tipo de mensaje sin mirar si es comando o evento— y el
    /// test dejaría de tocar la dirección, que es justo la parte frágil.
    /// </summary>
    private async Task SendReleaseAsync(Guid orderId, Guid? messageId = null)
    {
        var endpoint = await host.Harness.Bus.GetSendEndpoint(ReleaseStockEndpoint);

        await endpoint.Send(
            new ReleaseStock { OrderId = orderId },
            context => context.MessageId = messageId ?? Guid.NewGuid(),
            CancellationToken);
    }

    /// <summary>
    /// Una sola vez por test, después de TODOS los envíos: <c>InactivityTask</c> es
    /// una única tarea que se completa la primera vez que el bus queda inactivo, así
    /// que un segundo await no espera nada. Por eso ningún test de esta clase publica
    /// nada antes de esta línea salvo los ReleaseStock — ver
    /// <c>SeedReservationAsync</c>.
    /// </summary>
    private Task SettleAsync() => host.Harness.InactivityTask;

    private List<T> Published<T>()
        where T : class =>
        host.Harness.Published.Select<T>().Select(message => message.Context.Message).ToList();
}
