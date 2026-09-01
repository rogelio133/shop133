using MassTransit;
using MassTransit.Testing;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Orders.Infrastructure.Persistence;

using Shop133.TestUtilities;

using Xunit;

namespace Orders.Tests.Infrastructure;

/// <summary>
/// Levanta Orders.API en memoria contra una base de datos recién creada dentro
/// del contenedor de <see cref="SqlServerContainerFixture"/>.
///
/// **Necesita el RabbitMQ del compose levantado desde 3.3**, y eso es nuevo: ver
/// la nota sobre <c>ConnectionStrings:RabbitMq</c> más abajo.
///
/// **Una instancia por test.** xUnit construye la clase de test una vez por
/// método, y estas fábricas son campos de instancia, así que cada test estrena
/// base de datos. Aquí sale barato de verdad: OrdersDb tiene una sola migración
/// y ningún seed, al revés que CatalogDb, cuyo SeedSouvenirCatalog mete 55
/// filas. El efecto secundario es que **ningún test de Orders depende del estado
/// que dejó otro**, y por eso se puede afirmar `Orders.Count == 0` sin más
/// (ver <see cref="CountOrdersAsync"/>), que es la mitad de lo que 2.4 tiene que
/// demostrar.
///
/// *Descartado* Respawn. Sin seed que restaurar no aporta nada: lo que Respawn
/// hace —borrar filas— aquí lo hace gratis un CREATE DATABASE.
/// </summary>
public sealed class OrdersApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private static int databaseCounter;

    private readonly SqlServerContainerFixture container;

    public OrdersApiFactory(SqlServerContainerFixture container)
    {
        this.container = container;

        DatabaseName = $"OrdersTests_{Interlocked.Increment(ref databaseCounter):D3}";
    }

    public string DatabaseName { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Testing" y no el "Development" que WebApplicationFactory pone por
        // defecto: Development carga los User Secrets de Orders.API, que traen la
        // contraseña real de orders_user y el OrdersDb del compose. Si la línea
        // del connection string de abajo se rompiera, la suite escribiría pedidos
        // en la base de desarrollo sin que nada lo delatara.
        builder.UseEnvironment("Testing");

        // Las DOS claves que Program.cs exige —eran tres hasta 3.3—, y las dos por
        // la misma razón: las lee y lanza InvalidOperationException *antes* de
        // app.Build(), así que sustituir servicios en ConfigureTestServices
        // llegaría tarde: el host ni se construiría. Dándoles valor se prueban el
        // AddDbContext y el AddMassTransit reales del servicio, sin reregistrar
        // nada.
        //
        // La regla que esto ilustra, y que 3.1 dejó escrita: cada guarda nueva en
        // un Program.cs es una línea nueva en esta fábrica, y cada guarda que se
        // va se lleva la suya. Aquí acaba de irse Services:CatalogBaseUrl con la
        // llamada síncrona a Catalog. Nada más que esta suite detecta el desajuste.
        builder.UseSetting("ConnectionStrings:OrdersDb", container.ConnectionStringFor(DatabaseName));

        // Añadida en 3.1, cuando Program.cs empezó a exigir el URI del broker.
        // Sin esta línea la suite entera falla con "Falta la configuración
        // 'ConnectionStrings:RabbitMq'".
        //
        // **El valor es falso a propósito desde 3.7.** El bus de RabbitMQ que
        // registra Program.cs se desmonta unas líneas más abajo y se sustituye por
        // el harness en memoria, así que aquí no hay ningún broker al que
        // conectarse: la clave solo tiene que existir para que la guarda pase.
        // Poner un URI verosímil sería peor — si algún día el desmontaje se
        // rompiera, la suite se conectaría al RabbitMQ de desarrollo sin que nada
        // lo delatara. Con este host inventado, se cuelga y se investiga.
        //
        // Nótese el viaje de esta clave: en 3.1 era decorativa (nadie publicaba),
        // en 3.3 pasó a ser una dependencia real (un Publish sobre RabbitMQ espera
        // a que haya conexión en vez de fallar rápido, así que con el broker caído
        // la petición se colgaba), y en 3.7 vuelve a ser decorativa. La regla que
        // ilustra sigue en pie: cada guarda de Program.cs es una línea de esta
        // fábrica.
        builder.UseSetting("ConnectionStrings:RabbitMq", "amqp://el-harness-sustituye-esto:5672");

        // ── El bus de RabbitMQ, fuera; el harness en memoria, dentro (3.7) ──
        //
        // Esto es lo que le quita a la suite la dependencia del broker real que
        // estrenó 3.3, y de paso lo que permite por fin **afirmar en un test que
        // OrderCreated se publicó** — la deuda que docs/fase_3_3.md dejó apuntada
        // aquí en un comentario.
        //
        // Hay que desmontar en vez de sustituir porque no se llega antes:
        // Program.cs lee su guarda y registra AddMassTransit *antes* de
        // app.Build(), y ConfigureTestServices corre después. Así que se quitan los
        // ServiceDescriptor que puso MassTransit y se registra el harness encima.
        //
        // *Descartado* un interruptor de transporte en Program.cs (elegir
        // UsingInMemory o UsingRabbitMq según configuración). Sería más robusto que
        // este filtro, pero mete código de producción que existe solo para los
        // tests y, peor, deja al servicio poder arrancar sin hablar con el broker
        // sin que nada avise. El precio de la alternativa elegida es que el filtro
        // es frágil por naturaleza; lo que lo hace aceptable es que su rotura no es
        // silenciosa: sin bus registrado el host no arranca, y con el de RabbitMQ
        // todavía puesto los tests se cuelgan contra el URI inventado de arriba.
        //
        // *Descartado* Testcontainers.RabbitMq: haría la suite autónoma, sí, pero
        // a cambio de un paquete más y ~10 s de arranque por ensamblado para
        // seguir sin poder afirmar nada sobre el mensaje publicado.
        builder.ConfigureTestServices(services =>
        {
            foreach (var descriptor in services.Where(IsMassTransit).ToList())
            {
                services.Remove(descriptor);
            }

            services.AddMassTransitTestHarness();
        });
    }

    /// <summary>
    /// Un registro puesto por MassTransit, reconocido por el ensamblado en el que
    /// vive su tipo de servicio o su implementación.
    ///
    /// Se filtra por ensamblado y no por una lista de tipos concretos porque
    /// <c>AddMassTransit</c> registra decenas y la lista quedaría desfasada en la
    /// siguiente versión menor. Lo que queda detrás son cosas como
    /// <c>IConfigureOptions&lt;MassTransitHostOptions&gt;</c>, cuyo tipo vive en
    /// Microsoft.Extensions.Options: inofensivas, porque
    /// <c>AddMassTransitTestHarness</c> vuelve a registrar las suyas.
    /// </summary>
    private static bool IsMassTransit(ServiceDescriptor descriptor) =>
        BelongsToMassTransit(descriptor.ServiceType)
        || BelongsToMassTransit(descriptor.ImplementationType)
        || BelongsToMassTransit(descriptor.ImplementationInstance?.GetType());

    private static bool BelongsToMassTransit(Type? type) =>
        type?.Assembly.GetName().Name?.StartsWith("MassTransit", StringComparison.Ordinal) is true;

    /// <summary>
    /// Crea la base y le aplica la migración de 2.2. No hay seed: los pedidos los
    /// crea cada test por HTTP, que es justamente el camino que se está probando.
    ///
    /// El orden importa: la base tiene que existir antes de tocar
    /// <see cref="WebApplicationFactory{TEntryPoint}.Services"/>, porque esa
    /// propiedad es la que construye el host.
    /// </summary>
    /// <summary>
    /// El harness en memoria que sustituye al bus de RabbitMQ, ya arrancado.
    ///
    /// Lo arranca el propio host —el harness se registra como hosted service—, así
    /// que aquí solo hay que resolverlo. Tocar <c>Services</c> es lo que construye
    /// el host, de ahí que se haga después del MigrateAsync.
    /// </summary>
    public ITestHarness Harness => Services.GetRequiredService<ITestHarness>();

    public async ValueTask InitializeAsync()
    {
        await container.CreateDatabaseAsync(DatabaseName);

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();

        await db.Database.MigrateAsync();
    }

    /// <summary>
    /// Cuenta los pedidos leyendo la base directamente, sin pasar por la API.
    ///
    /// Hace falta porque Orders.API no tiene un `GET /orders` que liste: 2.3 solo
    /// expuso el GET por id, y comprobar "no se creó el pedido" preguntando por un
    /// id que nunca se devolvió no demuestra nada. Es también la única forma de
    /// distinguir "no se guardó" de "se guardó y la respuesta falló después".
    /// </summary>
    public async Task<int> CountOrdersAsync(CancellationToken cancellationToken)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();

        return await db.Orders.CountAsync(cancellationToken);
    }

    /// <summary>
    /// El host se va primero y la base después: al cerrarse el proveedor de
    /// servicios se devuelven al pool las conexiones que abrió EF, y el DROP
    /// encuentra la base libre.
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        await container.DropDatabaseAsync(DatabaseName);
    }
}
