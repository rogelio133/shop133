using Catalog.Infrastructure.Persistence;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
    }

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
