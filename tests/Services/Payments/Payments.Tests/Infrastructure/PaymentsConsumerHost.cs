using MassTransit;
// ITestHarness vive en MassTransit.Testing, mientras que
// AddMassTransitTestHarness está en MassTransit a secas. Hacen falta los dos.
using MassTransit.Testing;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Payments.API;
using Payments.API.Consumers;
using Payments.Infrastructure.Entities;
using Payments.Infrastructure.Persistence;

using Shop133.TestUtilities;

using Xunit;

namespace Payments.Tests.Infrastructure;

/// <summary>
/// El host de estos tests: el consumer de Payments montado sobre el transporte en
/// memoria de MassTransit y una base de datos SQL Server recién creada dentro del
/// contenedor de <see cref="SqlServerContainerFixture"/>.
///
/// Es el gemelo de <c>InventoryConsumerHost</c> y comparte sus decisiones: no es
/// un <c>WebApplicationFactory</c> porque Payments.API tampoco tiene superficie
/// HTTP, y el harness es lo que hace observable cuántos eventos salieron — la
/// única diferencia entre un duplicado descartado y uno reprocesado, según midió
/// docs/fase_3_6.md. **El mismo precio, dicho en voz alta:** nada aquí comprueba
/// que el <c>Program.cs</c> del servicio registre el consumer; ese hueco es de 8.2.
///
/// Lo que sí es propio de Payments es el umbral: ver <see cref="DeclineAmountAbove"/>.
/// </summary>
public sealed class PaymentsConsumerHost : IAsyncLifetime
{
    /// <summary>
    /// El umbral de rechazo con el que corren estos tests, fijado **por código y
    /// no leyendo el appsettings.json** del servicio.
    ///
    /// Coincide a propósito con el valor por defecto de
    /// <see cref="PaymentSimulationOptions.DeclineAmountAbove"/>, pero se declara
    /// aquí para que el test diga en su propia cara de qué depende: un test cuyo
    /// resultado cambia al editar un fichero de configuración del servicio es un
    /// test que miente sobre lo que prueba.
    /// </summary>
    public const decimal DeclineAmountAbove = 1000m;

    private static int databaseCounter;

    private readonly SqlServerContainerFixture container;

    private ServiceProvider provider = null!;

    public PaymentsConsumerHost(SqlServerContainerFixture container)
    {
        this.container = container;

        DatabaseName = $"PaymentsTests_{Interlocked.Increment(ref databaseCounter):D3}";
    }

    public string DatabaseName { get; }

    public ITestHarness Harness { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await container.CreateDatabaseAsync(DatabaseName);

        var services = new ServiceCollection();

        services.AddLogging();

        services.AddDbContext<PaymentsDbContext>(options =>
            options.UseSqlServer(container.ConnectionStringFor(DatabaseName)));

        // El consumer recibe IOptions<PaymentSimulationOptions>, así que hay que
        // registrarlo: sin esto resolvería el valor por defecto y el umbral de los
        // tests dependería de que nadie tocara el default.
        services.Configure<PaymentSimulationOptions>(
            options => options.DeclineAmountAbove = DeclineAmountAbove);

        services.AddMassTransitTestHarness(configure =>
        {
            configure.AddConsumer<StockReservedConsumer>();

            // ConcurrentMessageLimit = 1 por lo mismo que en Inventory.Tests: por
            // defecto el transporte en memoria entrega en paralelo y los tests que
            // publican dos veces el mismo pedido salían no deterministas. Lo que
            // aquí se modela es una reentrega, que es secuencial por definición.
            // El detalle completo está en InventoryConsumerHost.
            configure.UsingInMemory((context, cfg) =>
            {
                cfg.ConcurrentMessageLimit = 1;
                cfg.ConfigureEndpoints(context);
            });
        });

        provider = services.BuildServiceProvider(validateScopes: true);

        // PaymentsDb no tiene seed: las dos migraciones (3.5 y 3.6) solo crean
        // Payments y ProcessedMessages, las dos vacías. Al revés que Inventory,
        // aquí cada test parte de la nada, que es justo lo que hace legible
        // "no existe cobro de este pedido".
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

            await db.Database.MigrateAsync();
        }

        Harness = provider.GetRequiredService<ITestHarness>();

        await Harness.Start();
    }

    /// <summary>
    /// El cobro de un pedido, aplanado para que los asserts no sostengan
    /// entidades fuera de su scope. <c>null</c> = no hay fila.
    /// </summary>
    public async Task<PaymentSnapshot?> PaymentAsync(Guid orderId, CancellationToken cancellationToken)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        var payment = await db.Payments
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.OrderId == orderId, cancellationToken);

        return payment is null
            ? null
            : new PaymentSnapshot(
                payment.OrderId,
                payment.Amount,
                payment.Status,
                payment.TransactionId,
                payment.FailureReason);
    }

    public async Task<int> CountPaymentsAsync(CancellationToken cancellationToken)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        return await db.Payments.CountAsync(cancellationToken);
    }

    public async Task<int> CountProcessedAsync(CancellationToken cancellationToken)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        return await db.ProcessedMessages.CountAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await provider.DisposeAsync();

        await container.DropDatabaseAsync(DatabaseName);
    }
}

/// <summary>Un cobro leído de la base.</summary>
public sealed record PaymentSnapshot(
    Guid OrderId,
    decimal Amount,
    PaymentStatus Status,
    string? TransactionId,
    string? FailureReason);
