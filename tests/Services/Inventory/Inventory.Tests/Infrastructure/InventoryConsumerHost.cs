using Inventory.API.Consumers;
using Inventory.Infrastructure.Entities;
using Inventory.Infrastructure.Persistence;

using MassTransit;
// ITestHarness vive en MassTransit.Testing, mientras que
// AddMassTransitTestHarness está en MassTransit a secas. Los dos using hacen
// falta y el compilador solo se queja del segundo. Mismo tropiezo que 3.2 anotó
// con SystemTextJsonMessageSerializer, que está en MassTransit.Serialization.
using MassTransit.Testing;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Shop133.TestUtilities;

using Xunit;

namespace Inventory.Tests.Infrastructure;

/// <summary>
/// El host de estos tests: el consumer de Inventory montado sobre el transporte
/// en memoria de MassTransit y una base de datos SQL Server recién creada dentro
/// del contenedor de <see cref="SqlServerContainerFixture"/>.
///
/// **No es un <c>WebApplicationFactory</c>, y ésa es la decisión de forma de
/// 3.7.** Las otras dos suites levantan su servicio entero porque prueban
/// endpoints HTTP; Inventory.API no tiene ni uno. Lo que se prueba aquí es un
/// <c>IConsumer&lt;OrderCreated&gt;</c>, así que se le construye el contenedor de
/// dependencias que necesita y nada más: el DbContext real y el bus en memoria.
///
/// *Descartado* <c>WebApplicationFactory&lt;Program&gt;</c> sobre Inventory.API.
/// Habría que añadirle un <c>public partial class Program { }</c> —que hoy no
/// tiene, y que docs/fase_3_4.md dejó como pregunta abierta para este punto—, el
/// paquete Microsoft.AspNetCore.Mvc.Testing, y desmontar el bus de RabbitMQ que
/// su Program.cs registra, todo para arrancar un servidor web al que no se le va
/// a pedir una sola petición. **El precio de no hacerlo, dicho en voz alta:**
/// nada aquí comprueba que <c>Program.cs</c> registre de verdad el consumer con
/// <c>AddConsumer&lt;OrderCreatedConsumer&gt;()</c>; si alguien borrara esa
/// línea, estos tests seguirían en verde y el servicio dejaría de consumir. Ese
/// hueco es de 8.2, que prueba la topología real contra un RabbitMQ de verdad.
///
/// *Descartado* también un transporte en memoria "a mano" (instanciar el consumer
/// y llamarle a <c>Consume</c> con un doble de <c>ConsumeContext</c>). El harness
/// es lo que hace observable lo único que distingue un duplicado descartado de
/// uno reprocesado: **cuántos eventos salieron**. El estado de la base es
/// idéntico en los dos casos — lo midió docs/fase_3_6.md — así que un test que
/// solo mire la base no puede afirmar la idempotencia.
///
/// **Una instancia por test**, como en Orders.Tests: xUnit construye la clase de
/// test una vez por método y este host es un campo de instancia, así que cada
/// test estrena su <c>InventoryTests_NNN</c>. Sale caro (~2-3 s por test) y a
/// cambio ningún test depende del stock que dejó otro, que es justo lo que hace
/// legibles los asserts sobre <c>QuantityReserved</c>.
/// </summary>
public sealed class InventoryConsumerHost : IAsyncLifetime
{
    private static int databaseCounter;

    private readonly SqlServerContainerFixture container;

    private ServiceProvider provider = null!;

    public InventoryConsumerHost(SqlServerContainerFixture container)
    {
        this.container = container;

        DatabaseName = $"InventoryTests_{Interlocked.Increment(ref databaseCounter):D3}";
    }

    public string DatabaseName { get; }

    /// <summary>
    /// El harness ya arrancado. Publicar por <c>harness.Bus</c> y afirmar sobre
    /// <c>harness.Published</c> / <c>harness.Consumed</c>.
    /// </summary>
    public ITestHarness Harness { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await container.CreateDatabaseAsync(DatabaseName);

        var services = new ServiceCollection();

        services.AddLogging();

        // El DbContext real contra SQL Server real. Se registra aquí a mano en vez
        // de heredarlo del Program.cs del servicio, que es la contrapartida de no
        // usar WebApplicationFactory: esta línea y la de Program.cs pueden
        // divergir, y nada avisaría.
        services.AddDbContext<InventoryDbContext>(options =>
            options.UseSqlServer(container.ConnectionStringFor(DatabaseName)));

        // El transporte en memoria. AddMassTransitTestHarness registra el bus, el
        // ITestHarness y los consumers que se le declaren.
        services.AddMassTransitTestHarness(configure =>
        {
            configure.AddConsumer<OrderCreatedConsumer>();

            // El de 4.4, que a diferencia del otro consume un COMANDO: no se le
            // publica, se le envía a una dirección.
            configure.AddConsumer<ReleaseStockConsumer>();

            // El mismo formatter que Program.cs, y aquí no es cosmético como podría
            // parecer: es lo que hace que el endpoint de ReleaseStockConsumer se
            // llame "release-stock" también dentro del harness, así que los tests
            // pueden mandar el comando a **queue:release-stock**, que es la URI
            // literal que escribe la OrderStateMachine.
            //
            // Sin esta línea el harness usaría su formatter por defecto, los tests
            // tendrían que descubrir la dirección de otra forma, y el acuerdo entre
            // el Send de la saga y el nombre de esta cola —que no lo vigila el
            // compilador y cuyo desacuerdo no produce ningún error— se quedaría sin
            // ninguna prueba que lo tocase.
            configure.SetKebabCaseEndpointNameFormatter();

            // ── ConcurrentMessageLimit = 1, y no es cosmético ──
            //
            // Por defecto el transporte en memoria entrega en paralelo, y eso hace
            // NO DETERMINISTAS los tests que publican dos mensajes del mismo
            // pedido: si los dos entran a la vez, ninguno ve todavía la reserva del
            // otro, los dos intentan el INSERT y uno revienta por clave duplicada.
            // Medido en 3.7, y de forma incómoda — el mismo test daba 2 eventos en
            // una ejecución y 1 en la siguiente.
            //
            // **Eso no es un defecto del test: es exactamente el agujero de
            // concurrencia que docs/fase_3_6.md dejó anotado sin dueño** (dos
            // entregas simultáneas del mismo pedido pasan las dos guardas y chocan
            // en el INSERT, sin que nadie capture el DbUpdateException). El harness
            // lo reprodujo sin querer.
            //
            // Se fija a 1 porque lo que estos tests modelan es una **reentrega**,
            // que es secuencial por definición: el mensaje llega, se procesa, y más
            // tarde vuelve a llegar. Cubrir la carrera es otro test distinto y
            // necesita antes que alguien decida qué debe pasar — hoy no está
            // decidido, así que fijarlo aquí no tapa nada: lo deja donde estaba,
            // documentado y sin dueño.
            configure.UsingInMemory((context, cfg) =>
            {
                cfg.ConcurrentMessageLimit = 1;
                cfg.ConfigureEndpoints(context);
            });
        });

        // validateScopes: true a propósito. El consumer recibe un DbContext
        // scoped, y si algún día alguien lo inyectara en un singleton, esto lo
        // convierte en un fallo ruidoso en vez de en un DbContext compartido entre
        // mensajes.
        provider = services.BuildServiceProvider(validateScopes: true);

        // MigrateAsync() ES el seed: las 50 filas de StockItems viven dentro de la
        // migración SeedStockItems (3.4), igual que el catálogo de 1.4 vive dentro
        // de SeedSouvenirCatalog. No hay que sembrar nada a mano.
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

            await db.Database.MigrateAsync();
        }

        Harness = provider.GetRequiredService<ITestHarness>();

        await Harness.Start();
    }

    /// <summary>
    /// Las unidades reservadas de un producto, leídas de la base.
    ///
    /// <c>AsNoTracking</c> no sobra: el consumer trabaja en su propio scope, así
    /// que este scope tiene su propio ChangeTracker y podría devolver una entidad
    /// cacheada de una lectura anterior del mismo test.
    /// </summary>
    public async Task<int> QuantityReservedAsync(int productId, CancellationToken cancellationToken)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        var item = await db.StockItems
            .AsNoTracking()
            .SingleAsync(stockItem => stockItem.ProductId == productId, cancellationToken);

        return item.QuantityReserved;
    }

    /// <summary>Las unidades físicas de un producto. Reservar no debe moverlas.</summary>
    public async Task<int> QuantityOnHandAsync(int productId, CancellationToken cancellationToken)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        var item = await db.StockItems
            .AsNoTracking()
            .SingleAsync(stockItem => stockItem.ProductId == productId, cancellationToken);

        return item.QuantityOnHand;
    }

    public async Task<int> CountReservationsAsync(CancellationToken cancellationToken)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        return await db.StockReservations.CountAsync(cancellationToken);
    }

    /// <summary>
    /// La reserva de un pedido con sus líneas. Vuelven **sin <c>Include</c>**:
    /// son un tipo owned desde 3.4, así que cargan con su dueño.
    /// </summary>
    public async Task<StockReservationSnapshot?> ReservationAsync(
        Guid orderId,
        CancellationToken cancellationToken)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        var reservation = await db.StockReservations
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.OrderId == orderId, cancellationToken);

        return reservation is null
            ? null
            : new StockReservationSnapshot(
                reservation.OrderId,
                reservation.Lines.Select(line => (line.ProductId, line.Quantity)).ToList());
    }

    /// <summary>
    /// Deja un pedido con stock reservado, **sin pasar por el bus** (4.4).
    ///
    /// ── Por qué no se reserva publicando un OrderCreated, que sería más realista ──
    ///
    /// Porque haría que cada test de la compensación tuviera **dos etapas de bus**,
    /// y con dos etapas <c>InactivityTask</c> deja de servir: es UNA SOLA tarea que
    /// se completa la primera vez que el bus queda inactivo, así que si el bus se
    /// vacía mientras se prepara el segundo envío, el await del final vuelve al
    /// instante y los asserts corren antes de que el consumer haya trabajado.
    ///
    /// **No es teórico: se midió.** La primera versión de estos tests reservaba
    /// publicando OrderCreated y luego enviaba el comando; dos de los cuatro tests
    /// susceptibles fallaron con cero <c>StockReleased</c> mientras los otros dos
    /// pasaban, en la misma ejecución. El hueco lo abre el <c>await
    /// GetSendEndpoint(...)</c> que hay entre las dos etapas. Es el trampa 1 de las
    /// tres que 3.7 dejó anotadas, esta vez estrellándose de verdad.
    ///
    /// Hay además una segunda carrera que esto elimina: los dos consumers tienen
    /// endpoints distintos, y <c>ConcurrentMessageLimit = 1</c> es **por endpoint**,
    /// así que nada garantiza que el OrderCreated se consuma antes que el
    /// ReleaseStock que se manda a continuación.
    ///
    /// *Descartado* esperar con <c>Published.Any&lt;StockReserved&gt;()</c> entre las
    /// dos etapas, que ordenaría bien: no arregla el <c>InactivityTask</c> gastado,
    /// y sin él no se puede afirmar "exactamente uno" — solo "al menos uno".
    ///
    /// **El precio, dicho en voz alta:** la fila que estos tests liberan no la
    /// escribió <c>OrderCreatedConsumer</c>, así que si las dos formas de crearla
    /// divergieran, aquí no se notaría. Lo que lo hace aceptable es que se usan las
    /// **mismas entidades y los mismos métodos** que el consumer —
    /// <c>StockItem.Reserve</c> y el constructor de <c>StockReservation</c>, con sus
    /// invariantes—, y que la forma de la fila la afirma
    /// <c>OrderCreatedConsumerTests</c> por su cuenta.
    /// </summary>
    public async Task SeedReservationAsync(
        Guid orderId,
        IReadOnlyList<(int ProductId, int Quantity)> lines,
        CancellationToken cancellationToken)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        foreach (var (productId, quantity) in lines)
        {
            var item = await db.StockItems
                .SingleAsync(stockItem => stockItem.ProductId == productId, cancellationToken);

            item.Reserve(quantity);
        }

        db.StockReservations.Add(
            new StockReservation(
                orderId,
                lines.Select(line => new StockReservationLine(line.ProductId, line.Quantity))));

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Cuándo se liberó la reserva de un pedido, o <c>null</c> si sigue viva (4.4).
    /// Devuelve <c>null</c> también si la reserva no existe — los tests que
    /// distinguen los dos casos usan además <see cref="ReservationAsync"/>.
    /// </summary>
    public async Task<DateTimeOffset?> ReleasedAtAsync(Guid orderId, CancellationToken cancellationToken)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        var reservation = await db.StockReservations
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.OrderId == orderId, cancellationToken);

        return reservation?.ReleasedAt;
    }

    public async Task<int> CountProcessedAsync(CancellationToken cancellationToken)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        return await db.ProcessedMessages.CountAsync(cancellationToken);
    }

    /// <summary>
    /// El bus se para primero y la base se borra después: al cerrarse el
    /// proveedor de servicios se devuelven al pool las conexiones que abrió EF, y
    /// el DROP encuentra la base libre.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await provider.DisposeAsync();

        await container.DropDatabaseAsync(DatabaseName);
    }
}

/// <summary>
/// Una reserva leída de la base, aplanada. Existe para que los asserts no tengan
/// que sostener entidades vivas fuera de su scope.
/// </summary>
public sealed record StockReservationSnapshot(
    Guid OrderId,
    IReadOnlyList<(int ProductId, int Quantity)> Lines);
