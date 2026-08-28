using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Orders.Infrastructure.Persistence;

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
        // **Ojo: en 3.1 esto NO era una dependencia real y desde 3.3 SÍ lo es.**
        // Entonces bastaba con que la clave existiera —nadie publicaba, y un bus
        // sin broker se limita a avisar y reintentar en segundo plano (ver
        // docs/fase_3_1.md), así que los tests pasaban con RabbitMQ parado—.
        // Ahora POST /orders publica OrderCreated de verdad, y un Publish sobre el
        // transporte de RabbitMQ **espera a que haya conexión en vez de fallar
        // rápido**: con el broker caído la petición no da error, se queda colgada
        // hasta que el test expire. `docker compose up -d` es prerrequisito.
        //
        // *Descartado* traer aquí el harness en memoria de MassTransit
        // (MassTransit.TestFramework), que quitaría esa dependencia y además
        // permitiría afirmar que el evento se publicó. Es el punto 3.7, y hacerlo
        // aquí obligaría a desmontar el bus que Program.cs ya registró — bastante
        // más que una línea. Mientras tanto lo que esta suite demuestra del
        // Publish es lo que un broker real puede demostrar: que no lanza y que no
        // bloquea. Que el mensaje sale se comprueba en el broker, a mano
        // (Verificación de docs/fase_3_3.md).
        //
        // *Descartado* también Testcontainers.RabbitMq, que haría la suite
        // autónoma como ya lo es con SQL Server. Es un paquete más y ~10 s de
        // arranque por ensamblado para una dependencia que 3.7 va a eliminar.
        builder.UseSetting("ConnectionStrings:RabbitMq", "amqp://guest:guest@localhost:5672");
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
