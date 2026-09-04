using MassTransit;
using MassTransit.Testing;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Orders.Domain.Sagas;
using Orders.Infrastructure.Persistence;

using Shop133.TestUtilities;

using Xunit;

namespace Orders.Tests.Infrastructure;

/// <summary>
/// La misma máquina de estados que <see cref="OrderSagaHost"/>, pero con el **repositorio EF
/// de 4.5** contra un SQL Server real: la instancia deja de vivir en memoria y se escribe en
/// <c>OrdersDb.OrderStates</c>.
///
/// **Existe porque de 4.5 no había ni un test.** Sus nueve verificaciones fueron a mano contra
/// el compose real, y la suite pasaba idéntica con el código de 4.4 — así lo dejó anotado la
/// sección Pendiente de docs/fase_4_5.md. Lo que aquí se comprueba es lo que
/// <see cref="OrderSagaHost"/> no puede ver: que hay una fila, qué lleva dentro, que su
/// <c>rowversion</c> avanza, y que **la saga sobrevive a que el proceso se caiga**.
///
/// ── Qué se registra y qué NO, deliberadamente ──
///
/// Se copia del <c>Program.cs</c> de Orders.API exactamente el <c>AddSagaStateMachine</c> con
/// su <c>EntityFrameworkRepository</c>: <c>ExistingDbContext&lt;OrdersDbContext&gt;()</c>,
/// <c>UseSqlServer()</c> y <c>ConcurrencyMode.Optimistic</c>.
///
/// **No se registra el outbox** (<c>AddEntityFrameworkOutbox</c> / <c>UseBusOutbox</c> /
/// <c>UseEntityFrameworkOutbox</c>). Con él, un <c>Publish</c> deja de entregar y escribe una
/// fila que un servicio de sondeo vacía más tarde, así que <c>InactivityTask</c> dejaría de
/// medir el final del trabajo y estos tests pasarían a depender de un intervalo de polling.
/// El outbox es de **8.2**, junto con la topología real y el choque de concurrencia.
///
/// *Descartado* meterlo igual y esperar por sondeo: cambiaría una suite determinista por una
/// con temporizadores, para probar algo que no es la saga sino su composición.
///
/// **Tampoco se registran los dos consumers de Orders.API** (<c>OrderConfirmedConsumer</c> /
/// <c>OrderCancelledConsumer</c>): moverían <c>Order.Status</c> en la tabla <c>Orders</c>, y
/// aquí no hay ningún pedido dado de alta — lo que se prueba es la fila de la *saga*, que es
/// otra tabla y otra pregunta.
///
/// **Una base por test**, como los otros tres hosts. Aquí importa más que en ninguno: el test
/// del reinicio tira el proveedor y levanta otro **contra la misma base**, que es justamente
/// lo que <c>InMemoryRepository()</c> no puede hacer.
/// </summary>
public sealed class OrderSagaDbHost : IAsyncLifetime
{
    private static int databaseCounter;

    private readonly SqlServerContainerFixture container;

    private ServiceProvider provider = null!;

    public OrderSagaDbHost(SqlServerContainerFixture container)
    {
        this.container = container;

        DatabaseName = $"OrdersSagaTests_{Interlocked.Increment(ref databaseCounter):D3}";
    }

    public string DatabaseName { get; }

    public ITestHarness Harness { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await container.CreateDatabaseAsync(DatabaseName);

        // La migración crea OrderStates (4.5) y las tres tablas del outbox. Que las del
        // outbox queden vacías es correcto: este host no lo registra.
        await using (var migrator = BuildProvider())
        {
            using var scope = migrator.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();

            await db.Database.MigrateAsync();
        }

        await StartBusAsync();
    }

    /// <summary>
    /// Tira el bus y levanta uno nuevo **contra la misma base**, sin borrar nada.
    ///
    /// Es lo que modela un reinicio de Orders.API, y el único sitio donde se ve la diferencia
    /// entre los dos repositorios: con <c>InMemoryRepository()</c> —el código entre 4.1 y
    /// 4.4— aquí se perderían todas las instancias, que es lo que midió la verificación 7 de
    /// docs/fase_4_1.md.
    /// </summary>
    public async Task RestartBusAsync()
    {
        await provider.DisposeAsync();

        await StartBusAsync();
    }

    private async Task StartBusAsync()
    {
        provider = BuildProvider();

        Harness = provider.GetRequiredService<ITestHarness>();

        await Harness.Start();
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();

        services.AddLogging();

        // ── Y acto seguido se sustituye la fábrica por una que no se puede destruir ──
        //
        // No es tuning: sin esta línea, <see cref="RestartBusAsync"/> revienta con
        // `ObjectDisposedException: 'LoggerFactory'` al arrancar el segundo bus. La causa es
        // que **LogContext de MassTransit es estático**: el primer bus deja ahí su
        // ILoggerFactory al arrancar, y cuando se destruye el proveedor esa fábrica muere sin
        // que nadie limpie el estático. El segundo bus, al construirse, lo reutiliza —
        // BaseHostConfiguration.set_LogContext -> BusLogContext.CreateLogContext -> boom.
        //
        // NullLoggerFactory.Instance es un singleton compartido cuyo Dispose no hace nada, así
        // que ningún proveedor puede dejarlo inservible para el siguiente. El precio es que
        // los cinco LogInformation de la saga no salen por consola en esta suite; no se
        // afirma nada sobre ellos, y OrderStateMachineTests sí conserva el logging de verdad.
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);

        services.AddSingleton<ReleaseStockSpySwitch>();

        // El mismo DbContext que registra el Program.cs del servicio. Declararlo a mano es la
        // contrapartida de no usar WebApplicationFactory: esta línea y la de Program.cs pueden
        // divergir y nada avisaría. Es el precio que la decisión 3 de docs/fase_3_7.md ya
        // aceptó por escrito para Inventory y Payments.
        services.AddDbContext<OrdersDbContext>(options =>
            options.UseSqlServer(container.ConnectionStringFor(DatabaseName)));

        services.AddMassTransitTestHarness(configure =>
        {
            // Copiado literalmente del Program.cs de Orders.API. ExistingDbContext y no un
            // contexto propio de la saga: es lo que hace que la fila de la instancia comparta
            // transacción con el resto (decisión 1 de docs/fase_4_5.md).
            configure.AddSagaStateMachine<OrderStateMachine, OrderState>()
                .EntityFrameworkRepository(repository =>
                {
                    repository.ExistingDbContext<OrdersDbContext>();
                    repository.UseSqlServer();
                    repository.ConcurrencyMode = ConcurrencyMode.Optimistic;
                });

            configure.AddConsumer<ReleaseStockSpyConsumer>()
                .Endpoint(endpoint => endpoint.Name = "release-stock");

            configure.SetKebabCaseEndpointNameFormatter();

            configure.SetTestTimeouts(testInactivityTimeout: TimeSpan.FromMilliseconds(500));

            configure.UsingInMemory((context, cfg) =>
            {
                cfg.ConcurrentMessageLimit = 1;
                cfg.ConfigureEndpoints(context);
            });
        });

        return services.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>
    /// La instancia leída **de la base**, no del repositorio del harness. Es la diferencia
    /// entera de esta clase: <see cref="OrderSagaHost"/> mira un diccionario en memoria y esto
    /// mira una tabla.
    ///
    /// <c>AsNoTracking</c> porque la saga trabaja en su propio scope: sin él, el ChangeTracker
    /// de este scope podría devolver una entidad cacheada de una lectura anterior del mismo
    /// test — que es exactamente lo que un test sobre <c>RowVersion</c> no puede permitirse.
    /// </summary>
    public async Task<OrderState?> InstanceAsync(Guid orderId, CancellationToken cancellationToken)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();

        return await db.OrderStates
            .AsNoTracking()
            .SingleOrDefaultAsync(saga => saga.CorrelationId == orderId, cancellationToken);
    }

    /// <summary>
    /// Espera a que la fila del pedido llegue a un estado, sondeando **la base**.
    ///
    /// ── Por qué esta suite no puede esperar solo con <c>InactivityTask</c> ──
    ///
    /// Porque es de un solo uso (trampa 1 de docs/fase_3_7.md) y aquí hay tests que publican
    /// en dos tandas para poder leer la fila **entre medias** — que es justo lo que se viene a
    /// comprobar. Se descubrió estrellándose: <c>EachTransition_AdvancesTheRowVersion</c>
    /// falló con `Expected: "PaymentPending" / Actual: "StockPending"` porque el segundo
    /// <c>await</c> volvió al instante, con el <c>StockReserved</c> todavía en vuelo.
    ///
    /// En <see cref="OrderStateMachineTests"/> el remedio fue publicarlo todo de una y esperar
    /// una sola vez; aquí no sirve, porque entonces no habría un "antes" que leer. El remedio
    /// correcto es otro y lo da la propia naturaleza de la suite: **la fuente de verdad es la
    /// tabla**, así que se pregunta a la tabla. No gasta el <c>InactivityTask</c>, no depende
    /// de que el bus quede ocioso, y falla con un mensaje que dice qué estado había.
    ///
    /// *Descartado* <c>SagaHarness.Exists(orderId, m => m.PaymentPending)</c>: mira el
    /// repositorio a través del harness, y lo que esta clase existe para demostrar es que hay
    /// una fila de verdad debajo.
    /// </summary>
    public async Task<OrderState> WaitForStateAsync(
        Guid orderId,
        string state,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        OrderState? instance;

        do
        {
            instance = await InstanceAsync(orderId, cancellationToken);

            if (instance?.CurrentState == state)
            {
                return instance;
            }

            await Task.Delay(25, cancellationToken);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException(
            $"La saga del pedido {orderId} no llegó a '{state}' en 10 s; " +
            $"se quedó en '{instance?.CurrentState ?? "(sin instancia)"}'.");
    }

    public async Task<int> CountInstancesAsync(CancellationToken cancellationToken)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();

        return await db.OrderStates.CountAsync(cancellationToken);
    }

    /// <summary>
    /// El bus se va primero y la base después: al cerrarse el proveedor se devuelven al pool
    /// las conexiones que abrió EF, y el DROP encuentra la base libre.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await provider.DisposeAsync();

        await container.DropDatabaseAsync(DatabaseName);
    }
}
