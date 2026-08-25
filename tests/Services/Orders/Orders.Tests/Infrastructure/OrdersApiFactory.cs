using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Orders.Infrastructure.Persistence;

using Xunit;

namespace Orders.Tests.Infrastructure;

/// <summary>
/// Levanta Orders.API en memoria contra una base de datos recién creada dentro
/// del contenedor de <see cref="SqlServerContainerFixture"/>, apuntando su
/// cliente de Catalog a un <see cref="CatalogStub"/>.
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
    private readonly string catalogBaseUrl;

    /// <param name="catalogBaseUrl">
    /// La URL del stub de Catalog. Se pasa por constructor y no se configura
    /// después porque Program.cs la lee durante la construcción del host, y para
    /// entonces ya tiene que estar.
    /// </param>
    public OrdersApiFactory(SqlServerContainerFixture container, string catalogBaseUrl)
    {
        this.container = container;
        this.catalogBaseUrl = catalogBaseUrl;

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

        // Las dos claves que Program.cs exige, y las dos por la misma razón: las
        // lee y lanza InvalidOperationException *antes* de app.Build(), así que
        // sustituir servicios en ConfigureTestServices llegaría tarde — el host
        // ni se construiría. Dándoles valor se prueban el AddDbContext y el
        // AddHttpClient reales del servicio, sin reregistrar nada.
        builder.UseSetting("ConnectionStrings:OrdersDb", container.ConnectionStringFor(DatabaseName));

        // PHASE-2 DEBT: cuando 3.3 sustituya la llamada síncrona por el evento
        // OrderCreated, esta clave desaparece de Program.cs y esta línea con ella.
        builder.UseSetting("Services:CatalogBaseUrl", catalogBaseUrl);
    }

    /// <summary>
    /// Crea la base y le aplica la migración de 2.2. No hay seed: los pedidos los
    /// crea cada test por HTTP, que es justamente el camino que se está probando.
    ///
    /// El orden importa: la base tiene que existir antes de tocar
    /// <see cref="WebApplicationFactory{TEntryPoint}.Services"/>, porque esa
    /// propiedad es la que construye el host.
    /// </summary>
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
