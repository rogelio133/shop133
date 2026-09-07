using Catalog.Infrastructure.Persistence;

using MassTransit;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Shop133.TestUtilities;

using Xunit;

namespace Catalog.Tests.Infrastructure;

/// <summary>
/// Levanta Catalog.API en memoria contra una base de datos recién creada dentro
/// del contenedor de <see cref="SqlServerContainerFixture"/>.
///
/// **Una instancia por clase de test.** Ese es el mecanismo de aislamiento del
/// punto 1.7: cada clase estrena base, la migra —y con eso obtiene el seed de
/// 1.4 intacto— y la borra al terminar.
///
/// *Descartado* Respawn con un checkpoint. Respawn borra filas, no las
/// restaura, y las 50 del catálogo viven dentro de la migración
/// SeedSouvenirCatalog: reponerlas exigiría o duplicar el seed en los tests o
/// borrar a mano su fila de __EFMigrationsHistory para que MigrateAsync la
/// vuelva a aplicar. Una base por clase sale gratis y no necesita explicación.
///
/// *Descartado* también una base por *test*. Aísla más, pero el CREATE DATABASE
/// más las tres migraciones cuestan cerca de un segundo cada vez. El precio de
/// la base por clase es una disciplina que los tests de este proyecto sí pueden
/// mantener: ninguno toca una fila del seed, los que escriben crean su propio
/// producto con su propio Sku.
/// </summary>
public sealed class CatalogApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private static int databaseCounter;

    private readonly SqlServerContainerFixture container;
    private readonly string databaseName;

    public CatalogApiFactory(SqlServerContainerFixture container)
    {
        this.container = container;
        databaseName = $"CatalogTests_{Interlocked.Increment(ref databaseCounter):D3}";
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Testing" y no el "Development" que WebApplicationFactory pone por
        // defecto, por dos motivos concretos:
        //  - Development carga los User Secrets de Catalog.API, que traen la
        //    contraseña real de catalog_user y el CatalogDb del compose. Un fallo
        //    en la línea de abajo dejaría los tests corriendo contra la base de
        //    desarrollo sin que nada lo delatara.
        //  - Development activa UseHttpsRedirection() (ver Program.cs), que sobre
        //    el TestServer solo sirve para devolver 307 donde se esperaba un 200.
        // Es además el mismo perfil que el contenedor de 1.6, que arranca en
        // Production: se prueba el arranque sin secretos, no el del IDE.
        builder.UseEnvironment("Testing");

        // Esto es obligatorio, no una comodidad. Program.cs lee la clave y lanza
        // InvalidOperationException *antes* de app.Build(), así que sustituir el
        // DbContext en ConfigureTestServices llegaría tarde: el host ni se
        // construye. Dando el connection string correcto tampoco hace falta
        // reregistrar nada — se prueba el AddDbContext real del servicio.
        builder.UseSetting("ConnectionStrings:CatalogDb", container.ConnectionStringFor(databaseName));

        // Añadida en 4.8, cuando Program.cs empezó a exigir el URI del broker.
        // Sin esta línea la suite entera —los 19 tests de 1.7— falla en el
        // constructor con "Falta la configuración 'ConnectionStrings:RabbitMq'".
        //
        // Es la regla que dejó escrita 3.1 y que ya cobró una vez en Orders: cada
        // guarda nueva en un Program.cs es una línea nueva en la fábrica de su
        // suite, y cada guarda que se va se lleva la suya. Nada más que esta suite
        // detecta el desajuste.
        //
        // **El valor es falso a propósito**, copiando el de OrdersApiFactory. El
        // bus de RabbitMQ que registra Program.cs se desmonta justo debajo, así que
        // aquí no hay ningún broker al que conectarse: la clave solo tiene que
        // existir para que la guarda pase. Un URI verosímil sería peor — si algún
        // día el desmontaje se rompiera, la suite se conectaría al RabbitMQ de
        // desarrollo, declararía los exchanges reales y ligaría una cola
        // order-created-pricing de verdad, sin que nada lo delatara. Con este host
        // inventado, se cuelga y se investiga.
        builder.UseSetting("ConnectionStrings:RabbitMq", "amqp://el-harness-sustituye-esto:5672");

        // ── El bus de RabbitMQ, fuera; el harness en memoria, dentro ──
        //
        // Copiado de OrdersApiFactory, donde 3.7 lo estrenó. Hay que desmontar en
        // vez de sustituir porque no se llega antes: Program.cs lee su guarda y
        // registra AddMassTransit *antes* de app.Build(), y ConfigureTestServices
        // corre después.
        //
        // **Y conviene ser preciso sobre por qué hace falta, porque "si no, los
        // tests fallan" es FALSO.** 3.1 midió que un bus apuntando a un host
        // inexistente no revienta: loguea "warn: Connection Failed" y reintenta con
        // backoff. Con solo la línea de arriba, los 19 tests probablemente pasarían
        // — cada uno con un bucle de reconexión de fondo. Las razones honestas son
        // otras tres: elimina la POSIBILIDAD de tocar un broker real, evita que el
        // consumer de 4.8 quede registrado-pero-inerte dentro del host de test, y
        // mantiene lo que CLAUDE.md afirma de todo el repositorio desde 3.7 —
        // ninguna suite necesita RabbitMQ, comprobable parando el contenedor.
        //
        // **La verdad incómoda**: en Orders este desmontaje se pagó solo, porque
        // permitió por fin afirmar en un test que OrderCreated se publicaba. Aquí
        // no. Catalog.API no publica nada por HTTP —ningún controller toca
        // IPublishEndpoint, y 4.8 no lo cambia—, así que estas líneas no habilitan
        // ni una aserción nueva en las 19 pruebas de endpoint. Es puramente un
        // satisfactor de guardas, y las pruebas del consumer viven en su propio
        // host (CatalogConsumerHost), que no monta el API.
        //
        // *Descartado* un interruptor de transporte en Program.cs: metería código
        // de producción que existe solo para los tests y dejaría al servicio poder
        // arrancar sin hablar con el broker sin que nada avise.
        // *Descartado* Testcontainers.RabbitMq: un paquete más y ~10 s por
        // ensamblado para no poder afirmar nada que el harness no afirme ya.
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
    /// siguiente versión menor.
    ///
    /// **Segunda copia literal, y no se extrae.** La otra está en
    /// <c>OrdersApiFactory</c> desde 3.7; la regla de 2.4 es que dos ocurrencias no
    /// son un patrón, y aquí hay además un obstáculo mecánico: el sitio natural
    /// sería Shop133.TestUtilities, cuyo .csproj declara por escrito que solo entra
    /// "lo que las cuatro suites usan igual" y que tiene **cero ProjectReference** a
    /// propósito. Inventory.Tests y Payments.Tests no desmontan ningún bus —sus
    /// hosts construyen un ServiceCollection pelado—, así que esto lo usan dos de
    /// cuatro; y estos helpers necesitan <c>ServiceDescriptor</c>, que no viene en
    /// el reference pack de Microsoft.NETCore.App, así que extraerlos obligaría a
    /// declarar un paquete nuevo bajo tests/ para compartir algo que la mitad de las
    /// suites no usa. Una tercera copia obliga a releerlo.
    /// </summary>
    private static bool IsMassTransit(ServiceDescriptor descriptor) =>
        BelongsToMassTransit(descriptor.ServiceType)
        || BelongsToMassTransit(descriptor.ImplementationType)
        || BelongsToMassTransit(descriptor.ImplementationInstance?.GetType());

    private static bool BelongsToMassTransit(Type? type) =>
        type?.Assembly.GetName().Name?.StartsWith("MassTransit", StringComparison.Ordinal) is true;

    /// <summary>
    /// Crea la base y le aplica las tres migraciones. La última,
    /// SeedSouvenirCatalog, deja las 5 categorías y los 50 productos: aquí
    /// <c>MigrateAsync()</c> **es** el seed, no hace falta sembrar nada a mano.
    ///
    /// El orden importa: la base tiene que existir antes de tocar
    /// <see cref="WebApplicationFactory{TEntryPoint}.Services"/>, porque esa
    /// propiedad es la que construye el host.
    /// </summary>
    public async ValueTask InitializeAsync()
    {
        await container.CreateDatabaseAsync(databaseName);

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        await db.Database.MigrateAsync();
    }

    /// <summary>
    /// El host se va primero y la base después: al cerrarse el proveedor de
    /// servicios se devuelven al pool las conexiones que abrió EF, y el DROP
    /// encuentra la base libre.
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        await container.DropDatabaseAsync(databaseName);
    }
}
