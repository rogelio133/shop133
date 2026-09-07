using Catalog.API;
using Catalog.API.Consumers;
using Catalog.Infrastructure.Entities;
using Catalog.Infrastructure.Persistence;

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

namespace Catalog.Tests.Infrastructure;

/// <summary>
/// El host de los tests del consumer de 4.8: <c>OrderCreatedPricingConsumer</c>
/// montado sobre el transporte en memoria de MassTransit y una base de datos SQL
/// Server recién creada dentro del contenedor de
/// <see cref="SqlServerContainerFixture"/>.
///
/// **No es un <c>WebApplicationFactory</c>, aunque Catalog.API sí tenga API y sí
/// tenga el <c>public partial class Program { }</c> desde 1.7.** El patrón que se
/// copia es el de Inventory.Tests y Payments.Tests, no el de
/// <see cref="CatalogApiFactory"/>, y la razón es que lo que se prueba aquí es un
/// <c>IConsumer&lt;OrderCreated&gt;</c>: levantar el servidor web entero no
/// habilita ni una aserción y obliga a desmontar el bus real para volver a montar
/// el harness. Con un <c>ServiceCollection</c> pelado, el harness se monta ya.
///
/// *Descartado* reutilizar <see cref="CatalogApiFactory"/>, que desde 4.8 ya trae
/// un harness dentro y por tanto **podría** servir. Se descarta por lo que
/// arrastra: esa fábrica existe para probar endpoints, y sus 19 tests dependen de
/// que la base tenga el seed intacto y de que nadie toque una fila sembrada. Los
/// tests de este consumer necesitan lo contrario — cambiar precios y retrasar
/// fechas—, así que compartir la fábrica sería mezclar dos disciplinas de datos
/// opuestas en la misma clase de host. La única cosa que se comparte es la
/// colección, y eso a propósito (ver abajo).
///
/// **El precio de no usar WebApplicationFactory, dicho en voz alta:** nada aquí
/// comprueba que <c>Program.cs</c> registre de verdad el consumer con
/// <c>AddConsumer&lt;OrderCreatedPricingConsumer&gt;()</c>, ni —lo que en 4.8 es
/// peor— que el nombre de la cola que sale del formatter sea el que se cree. Si
/// alguien renombrara la clase a <c>OrderCreatedConsumer</c>, estos tests seguirían
/// verdes y en producción Catalog e Inventory se convertirían en consumidores
/// competidores de <c>order-created</c>. Ese hueco es de 8.2, que prueba la
/// topología real contra un RabbitMQ de verdad, y se verifica a mano mirando el
/// broker.
///
/// **Una instancia por test**: xUnit construye la clase de test una vez por método
/// y este host es un campo de instancia, así que cada test estrena su
/// <c>CatalogConsumerTests_NNN</c>. El prefijo es distinto del de
/// <see cref="CatalogApiFactory"/> a propósito — su contador es un <c>static</c>
/// por clase, así que con el mismo prefijo las dos podrían acuñar el mismo nombre
/// de base.
/// </summary>
public sealed class CatalogConsumerHost : IAsyncLifetime
{
    /// <summary>
    /// La ventana de checkout que se fija en este host, **en código y no leyendo el
    /// appsettings.json del servicio**.
    ///
    /// Es el criterio de <c>PaymentsConsumerHost</c> con su
    /// <c>DeclineAmountAbove</c>: un test cuyo resultado cambia al editar un archivo
    /// de configuración del servicio miente sobre lo que prueba. Los tests que
    /// afirman algo sobre la ventana usan esta constante para calcular sus fechas,
    /// así que cambiar el valor por defecto de producción no los rompe ni —peor—
    /// los hace pasar por otro motivo.
    /// </summary>
    public const int SnapshotWindowMinutes = 30;

    private static int databaseCounter;

    private readonly SqlServerContainerFixture container;

    private ServiceProvider provider = null!;

    public CatalogConsumerHost(SqlServerContainerFixture container)
    {
        this.container = container;

        DatabaseName = $"CatalogConsumerTests_{Interlocked.Increment(ref databaseCounter):D3}";
    }

    public string DatabaseName { get; }

    /// <summary>
    /// El harness ya arrancado. Publicar por <c>harness.Bus</c> y afirmar sobre
    /// <c>harness.Published</c>.
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
        services.AddDbContext<CatalogDbContext>(options =>
            options.UseSqlServer(container.ConnectionStringFor(DatabaseName)));

        // El consumer pide IOptions<PricingValidationOptions>, así que sin esta
        // línea no se resuelve. Se fija el valor en código — ver la constante.
        services.Configure<PricingValidationOptions>(
            options => options.PricingSnapshotWindowMinutes = SnapshotWindowMinutes);

        services.AddMassTransitTestHarness(configure =>
        {
            configure.AddConsumer<OrderCreatedPricingConsumer>();

            // El mismo formatter que Program.cs. Aquí es honestamente **cosmético**,
            // y conviene decirlo porque en Inventory.Tests no lo era: allí sostiene
            // el acuerdo entre el "queue:release-stock" que escribe la
            // OrderStateMachine y el nombre del endpoint del consumer. Este consumer
            // recibe un evento PUBLICADO, así que nadie nombra su cola: ningún test
            // de aquí puede notar si el nombre cambia. Se pone para que el host se
            // parezca al Program.cs que imita, no porque pruebe nada.
            //
            // Esa es precisamente la razón por la que la colisión de nombres de
            // 4.8 se verifica contra el broker y no con un test.
            configure.SetKebabCaseEndpointNameFormatter();

            // ── ConcurrentMessageLimit = 1, y no es cosmético ──
            //
            // Por defecto el transporte en memoria entrega en paralelo, y eso hace
            // no deterministas los tests que publican dos veces el mismo mensaje: si
            // los dos entran a la vez, ninguno ve todavía la marca del otro, los dos
            // intentan el INSERT en ProcessedMessages y uno revienta por clave
            // duplicada. Medido en 3.7 con Inventory, donde el mismo test daba 2
            // eventos en una ejecución y 1 en la siguiente.
            //
            // Se fija a 1 porque lo que estos tests modelan es una **reentrega**, que
            // es secuencial por definición. Cubrir la carrera es otro test y necesita
            // antes que alguien decida qué debe pasar; fijarlo aquí no tapa el
            // agujero, lo deja donde estaba: documentado y sin dueño.
            configure.UsingInMemory((context, cfg) =>
            {
                cfg.ConcurrentMessageLimit = 1;
                cfg.ConfigureEndpoints(context);
            });
        });

        // validateScopes: true a propósito. El consumer recibe un DbContext scoped,
        // y si algún día alguien lo inyectara en un singleton, esto lo convierte en
        // un fallo ruidoso en vez de en un DbContext compartido entre mensajes.
        provider = services.BuildServiceProvider(validateScopes: true);

        // MigrateAsync() ES el seed: las 5 categorías y los 50 productos viven
        // dentro de la migración SeedSouvenirCatalog (1.4). No hay que sembrar nada
        // a mano — y desde 4.8 aplica además las dos migraciones nuevas, así que las
        // 50 filas llegan con PreviousPrice y PriceChangedAt a NULL, o sea "este
        // producto nunca ha cambiado de precio".
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

            await db.Database.MigrateAsync();
        }

        Harness = provider.GetRequiredService<ITestHarness>();

        await Harness.Start();
    }

    /// <summary>
    /// El precio actual de un producto, leído de la base.
    ///
    /// <c>AsNoTracking</c> no sobra: el consumer trabaja en su propio scope, así que
    /// este scope tiene su propio ChangeTracker.
    /// </summary>
    public async Task<decimal> PriceAsync(int productId, CancellationToken cancellationToken)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var product = await db.Products
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == productId, cancellationToken);

        return product.Price;
    }

    /// <summary>El Sku de un producto — los asserts sobre el Reason lo buscan dentro.</summary>
    public async Task<string> SkuAsync(int productId, CancellationToken cancellationToken)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var product = await db.Products
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == productId, cancellationToken);

        return product.Sku;
    }

    /// <summary>
    /// Da de alta un producto y devuelve el Id que le puso SQL Server.
    ///
    /// **Pasa por el constructor real de <see cref="Product"/>**, con el criterio de
    /// <c>SeedReservationAsync</c> en Inventory.Tests: sembrar con las mismas
    /// entidades y los mismos métodos que usa el código bajo prueba, sin pasar por
    /// el bus. Y no se devuelve un Id inventado — 1.1 midió que
    /// <c>IDENTITY</c> no empieza en 1 y que un INSERT abortado quema su número, así
    /// que el Id hay que leerlo después de guardar.
    ///
    /// Los tests que escriben crean su propio producto en vez de tocar uno del seed:
    /// es la disciplina que <see cref="CatalogApiFactory"/> impuso en 1.7, y aquí
    /// sigue valiendo aunque cada test tenga su propia base — un test que cambia el
    /// precio de TAZA-001 sería ilegible al lado de otro que lo lee.
    /// </summary>
    public async Task<int> SeedProductAsync(
        string sku,
        decimal price,
        CancellationToken cancellationToken)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        // CategoryId 1 es "Tazas", sembrada por la migración de 1.4. La categoría es
        // obligatoria y a estos tests les da igual cuál sea.
        var product = new Product(
            sku,
            $"Producto de prueba {sku}",
            "Creado por Catalog.Tests para no tocar ninguna fila del seed.",
            price,
            stock: 100,
            categoryId: 1);

        db.Products.Add(product);

        await db.SaveChangesAsync(cancellationToken);

        return product.Id;
    }

    /// <summary>
    /// Le cambia el precio a un producto **llamando al <c>Update</c> real**, que es
    /// lo único que escribe <c>PreviousPrice</c> y <c>PriceChangedAt</c>.
    ///
    /// Importa que sea por ahí y no con un UPDATE a pelo: así la contabilidad del
    /// precio anterior la escribe el código bajo prueba, y un test que afirma "el
    /// precio anterior sigue siendo auténtico" está afirmando de verdad que
    /// <c>Product.Update</c> hizo su trabajo.
    /// </summary>
    public async Task ChangePriceAsync(int productId, decimal newPrice, CancellationToken cancellationToken)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var product = await db.Products
            .SingleAsync(candidate => candidate.Id == productId, cancellationToken);

        product.Update(
            product.Sku,
            product.Name,
            product.Description,
            newPrice,
            product.Stock,
            product.CategoryId,
            product.ImageUrl);

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Retrasa la fecha del último cambio de precio, para que la ventana esté
    /// vencida.
    ///
    /// **Es el único ayudante que rodea a la entidad, y necesita justificarse.**
    /// <see cref="Product.IsAuthenticPrice"/> lee <c>DateTimeOffset.UtcNow</c>
    /// directamente, y este repositorio ya descartó por escrito un
    /// <c>TimeProvider</c> inyectado (ver el <c>///</c> del constructor de
    /// <c>ProcessedMessage</c>). Así que "el precio cambió hace 31 minutos" es un
    /// estado que la base de datos puede tener y la entidad no puede expresar: no
    /// hay forma de fingir que el reloj avanzó, solo de mover la fecha hacia atrás.
    ///
    /// Va con <c>ExecuteUpdateAsync</c> y no cargando la entidad porque no hay
    /// ningún método que asigne <c>PriceChangedAt</c> —el setter es privado y solo
    /// <c>Update</c> lo toca—, y añadirle uno para los tests sería meter en la
    /// entidad una puerta que producción no necesita.
    ///
    /// *Descartado* encoger la ventana a <c>TimeSpan.Zero</c> en la configuración
    /// del host: confundiría *la ventana venció* con *la rama del precio anterior
    /// está apagada*, así que un test verde no distinguiría las dos causas. Y una
    /// ventana de 1 ms más un <c>Task.Delay</c> es intermitente por construcción.
    /// </summary>
    public async Task BackdatePriceChangeAsync(
        int productId,
        TimeSpan age,
        CancellationToken cancellationToken)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var backdated = DateTimeOffset.UtcNow - age;

        var rows = await db.Products
            .Where(product => product.Id == productId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(product => product.PriceChangedAt, backdated),
                cancellationToken);

        // Si no actualizó nada, el test que llamó a esto estaría afirmando sobre una
        // ventana que nunca se movió — y pasaría o fallaría por el motivo
        // equivocado. Mejor reventar aquí.
        Assert.Equal(1, rows);
    }

    public async Task<int> CountProcessedAsync(CancellationToken cancellationToken)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        return await db.ProcessedMessages.CountAsync(cancellationToken);
    }

    /// <summary>
    /// El bus se para primero y la base se borra después: al cerrarse el proveedor
    /// de servicios se devuelven al pool las conexiones que abrió EF, y el DROP
    /// encuentra la base libre.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await provider.DisposeAsync();

        await container.DropDatabaseAsync(DatabaseName);
    }
}
