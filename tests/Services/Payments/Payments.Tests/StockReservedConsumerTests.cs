using MassTransit;
using MassTransit.Testing;

using Payments.API.Consumers;
using Payments.Infrastructure.Entities;
using Payments.Tests.Infrastructure;

using Shop133.Contracts.Events;
using Shop133.TestUtilities;

using Xunit;

namespace Payments.Tests;

/// <summary>
/// <see cref="StockReservedConsumer"/> — el consumer de 3.5, con la guarda de
/// idempotencia que le añadió 3.6. Cierra la cadena de coreografía de la Fase 3.
///
/// Se verificó a mano en su día contra un broker y una base reales (Verificación
/// de docs/fase_3_5.md y docs/fase_3_6.md); esto es lo mismo, automatizado.
///
/// **El rechazo es determinista y por importe**, y ésa fue una decisión tomada en
/// 3.5 *pensando en esta clase*: con un porcentaje de fallo aleatorio, forzar el
/// camino del rechazo llegaría por suerte y habría que inyectar el <c>Random</c>
/// detrás de una interfaz o aceptar tests intermitentes. Con un umbral, forzarlo
/// es "pide más caro" — y aquí el umbral lo fija el host, no el appsettings.json.
/// </summary>
[Collection(PaymentsConsumerCollection.Name)]
[Trait("Category", "Docker")]
public sealed class StockReservedConsumerTests(SqlServerContainerFixture container) : IAsyncLifetime
{
    private const decimal Threshold = PaymentsConsumerHost.DeclineAmountAbove;

    private readonly PaymentsConsumerHost host = new(container);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => host.InitializeAsync();

    public ValueTask DisposeAsync() => host.DisposeAsync();

    // ── Cobro aceptado ───────────────────────────────────────────────────────

    [Fact]
    public async Task Consume_AmountBelowTheThreshold_PublishesPaymentCompletedWithATransactionId()
    {
        var orderId = Guid.NewGuid();

        await PublishAsync(NewReservation(orderId, 249.00m));
        await SettleAsync();

        var completed = Assert.Single(Published<PaymentCompleted>());
        Assert.Equal(orderId, completed.OrderId);
        Assert.Equal(249.00m, completed.Amount);
        Assert.StartsWith("SIM-", completed.TransactionId);

        Assert.Empty(Published<PaymentFailed>());

        var payment = await host.PaymentAsync(orderId, CancellationToken);

        Assert.NotNull(payment);
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        // El TransactionId publicado y el guardado son el mismo: es el
        // identificador con el que 4.4 tendría que pedir la devolución.
        Assert.Equal(completed.TransactionId, payment.TransactionId);
        Assert.Null(payment.FailureReason);
    }

    /// <summary>
    /// La frontera del umbral, que ningún documento había ejercido: el corte es
    /// <c>Amount &gt; threshold</c>, estrictamente mayor, así que un pedido de
    /// exactamente el límite **se cobra**.
    /// </summary>
    [Fact]
    public async Task Consume_AmountExactlyAtTheThreshold_PublishesPaymentCompleted()
    {
        var orderId = Guid.NewGuid();

        await PublishAsync(NewReservation(orderId, Threshold));
        await SettleAsync();

        Assert.Single(Published<PaymentCompleted>());
        Assert.Empty(Published<PaymentFailed>());
    }

    // ── Cobro rechazado ──────────────────────────────────────────────────────

    /// <summary>
    /// El camino que la Fase 4 necesita poder forzar: el escenario 3 obligatorio
    /// —stock reservado y pago rechazado, o sea la compensación— sale de aquí.
    /// </summary>
    [Fact]
    public async Task Consume_AmountAboveTheThreshold_PublishesPaymentFailedNamingTheLimit()
    {
        var orderId = Guid.NewGuid();

        await PublishAsync(NewReservation(orderId, 1197.00m));
        await SettleAsync();

        var failed = Assert.Single(Published<PaymentFailed>());
        Assert.Equal(orderId, failed.OrderId);
        Assert.Contains("1197.00", failed.Reason);
        Assert.Contains("1000.00", failed.Reason);

        Assert.Empty(Published<PaymentCompleted>());

        var payment = await host.PaymentAsync(orderId, CancellationToken);

        Assert.NotNull(payment);
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        // Un cobro fallido no tiene TransactionId, y eso no lo garantiza un CHECK
        // en la base: lo garantizan las dos factorías estáticas de Payment, que son
        // el único camino para construir uno (decisión de 3.5).
        Assert.Null(payment.TransactionId);
        Assert.NotNull(payment.FailureReason);
    }

    /// <summary>
    /// La segunda guarda de 3.5. Es alcanzable de verdad desde que 3.3 dejó que el
    /// cliente mande el precio en el cuerpo del <c>POST</c>.
    ///
    /// **Y no es el arreglo del agujero de precios**: un producto real pedido a
    /// 0.01 pasa esta guarda, pasa el umbral y se cobra un céntimo. Eso sigue
    /// siendo de 4.8/4.9, como recogió la corrección 2b de docs/fase_3_3.md.
    /// </summary>
    [Fact]
    public async Task Consume_ZeroAmount_PublishesPaymentFailed()
    {
        var orderId = Guid.NewGuid();

        await PublishAsync(NewReservation(orderId, 0m));
        await SettleAsync();

        Assert.Single(Published<PaymentFailed>());
        Assert.Empty(Published<PaymentCompleted>());

        var payment = await host.PaymentAsync(orderId, CancellationToken);

        Assert.NotNull(payment);
        Assert.Equal(PaymentStatus.Failed, payment.Status);
    }

    [Fact]
    public async Task Consume_NegativeAmount_PublishesPaymentFailed()
    {
        var orderId = Guid.NewGuid();

        await PublishAsync(NewReservation(orderId, -50m));
        await SettleAsync();

        Assert.Single(Published<PaymentFailed>());
        Assert.Empty(Published<PaymentCompleted>());
    }

    // ── Idempotencia ─────────────────────────────────────────────────────────

    /// <summary>
    /// La guarda de **negocio** (PK de <c>Payments</c> = <c>OrderId</c>), que es
    /// la que 3.5 tuvo que escribir a mano — Inventory la tenía gratis porque su
    /// PK ya era el OrderId.
    ///
    /// Lo que se afirma aquí es lo caro: **el republicado lleva el
    /// <c>TransactionId</c> guardado, no uno nuevo**. Acuñar otro daría a un mismo
    /// cobro dos identidades distintas, que es el duplicado más caro que este
    /// sistema puede producir.
    /// </summary>
    [Fact]
    public async Task Consume_SameOrderWithANewMessageId_RepublishesTheStoredTransactionId()
    {
        var orderId = Guid.NewGuid();
        var reservation = NewReservation(orderId, 249.00m);

        await PublishAsync(reservation);
        await PublishAsync(reservation);
        await SettleAsync();

        var completed = Published<PaymentCompleted>();

        // Dos eventos: un MessageId nuevo es alguien que ha vuelto a preguntar.
        Assert.Equal(2, completed.Count);

        // Pero un solo cobro, con un solo identificador.
        Assert.Equal(completed[0].TransactionId, completed[1].TransactionId);
        Assert.Equal(1, await host.CountPaymentsAsync(CancellationToken));
    }

    /// <summary>
    /// La guarda de **transporte** de 3.6: misma entrega dos veces ⇒ un solo
    /// efecto y un solo evento.
    /// </summary>
    [Fact]
    public async Task Consume_SameMessageIdTwice_PublishesASinglePaymentCompleted()
    {
        var orderId = Guid.NewGuid();
        var reservation = NewReservation(orderId, 249.00m);
        var messageId = Guid.NewGuid();

        await PublishAsync(reservation, messageId);
        await PublishAsync(reservation, messageId);
        await SettleAsync();

        Assert.Single(Published<PaymentCompleted>());

        // **El assert que de verdad prueba 3.6.** Sin él, este test pasa también
        // con la guarda de transporte borrada: el duplicado moriría en el INSERT
        // de ProcessedMessages por clave duplicada, no publicaría, y el recuento
        // saldría igual. Lo que distingue "descartado en silencio" de "explotó" es
        // que lo segundo publica un Fault y manda el mensaje a stock-reserved_error.
        // Comprobado rompiendo la guarda a propósito.
        Assert.Empty(Published<Fault<StockReserved>>());

        Assert.Equal(1, await host.CountPaymentsAsync(CancellationToken));
        Assert.Equal(1, await host.CountProcessedAsync(CancellationToken));
    }

    [Fact]
    public async Task Consume_SameMessageIdTwice_OnTheDeclinePath_PublishesASinglePaymentFailed()
    {
        var orderId = Guid.NewGuid();
        var reservation = NewReservation(orderId, 1197.00m);
        var messageId = Guid.NewGuid();

        await PublishAsync(reservation, messageId);
        await PublishAsync(reservation, messageId);
        await SettleAsync();

        Assert.Single(Published<PaymentFailed>());
        Assert.Empty(Published<Fault<StockReserved>>());

        Assert.Equal(1, await host.CountPaymentsAsync(CancellationToken));
    }

    /// <summary>
    /// Sin <c>MessageId</c> no se puede deducir si el mensaje es un duplicado, y
    /// un cobro que no puede deduplicarse es el peor sitio para adivinar: el
    /// consumer revienta y el mensaje acaba en <c>stock-reserved_error</c>.
    /// </summary>
    [Fact]
    public async Task Consume_MessageWithoutMessageId_FaultsTheConsumer()
    {
        var orderId = Guid.NewGuid();

        await host.Harness.Bus.Publish(
            NewReservation(orderId, 249.00m),
            context => context.MessageId = null,
            CancellationToken);
        await SettleAsync();

        var fault = Assert.Single(Published<Fault<StockReserved>>());
        Assert.Contains(
            fault.Exceptions,
            exception => exception.ExceptionType == typeof(InvalidOperationException).FullName);

        Assert.Empty(Published<PaymentCompleted>());
        Assert.Equal(0, await host.CountPaymentsAsync(CancellationToken));
    }

    // ── Ayudas ───────────────────────────────────────────────────────────────

    private Task PublishAsync(StockReserved reservation, Guid? messageId = null) =>
        host.Harness.Bus.Publish(
            reservation,
            context => context.MessageId = messageId ?? Guid.NewGuid(),
            CancellationToken);

    /// <summary>
    /// Espera a que el bus se quede sin trabajo. **Una vez por test, después de
    /// todas las publicaciones** — <c>InactivityTask</c> es una sola tarea que se
    /// completa la primera vez que el bus queda inactivo, así que llamarla dentro
    /// de un helper de publicación haría que la segunda espera no esperase nada.
    /// El detalle completo, con los dos intentos fallidos, está en el
    /// <c>SettleAsync</c> de Inventory.Tests.
    /// </summary>
    private Task SettleAsync() => host.Harness.InactivityTask;

    private List<T> Published<T>()
        where T : class =>
        host.Harness.Published.Select<T>().Select(message => message.Context.Message).ToList();

    /// <summary>
    /// <c>StockReserved</c> lleva <c>Amount</c> desde 3.2, y ése es todo el
    /// contenido de aquella revisión: Payments no puede leer OrdersDb (regla 1) y
    /// en la Fase 3 no hay saga a la que preguntar, así que el importe tiene que
    /// viajar con el evento aunque a Inventory no le sirva de nada.
    /// </summary>
    private static StockReserved NewReservation(Guid orderId, decimal amount) =>
        new()
        {
            OrderId = orderId,
            Amount = amount,
        };
}
