using Microsoft.Data.SqlClient;

using Testcontainers.MsSql;

using Xunit;

namespace Orders.Tests.Infrastructure;

/// <summary>
/// El contenedor de SQL Server que comparte todo el ensamblado de tests.
///
/// Arrancarlo cuesta segundos, así que hay exactamente uno: lo sostiene
/// <see cref="OrdersApiCollection"/> como collection fixture y vive desde el
/// primer test hasta el último. Lo que sí es de usar y tirar es la *base de
/// datos*: cada test crea la suya con <see cref="CreateDatabaseAsync"/> y la
/// borra al terminar (ver <see cref="OrdersApiFactory"/>).
///
/// **Es una copia literal de la de Catalog.Tests**, y eso es una decisión de
/// 2.4, no un olvido. docs/fase_1_7.md dejó abierta la pregunta de si extraerla
/// a un proyecto de utilidades compartido; con dos casos delante la respuesta es
/// que todavía no. *Descartado* `tests/Shop133.TestUtilities`: obligaría a
/// aprobar un proyecto fuera del layout de CLAUDE.md y a fijar una API común con
/// un solo uso real. La evidencia llega en 3.7, cuando Inventory.Tests y
/// Payments.Tests pidan lo mismo: entonces serán cuatro copias y la extracción
/// se decidirá con datos.
///
/// *Descartado* reutilizar el `sqlserver` de docker-compose. Los tests dejarían
/// de ser reproducibles fuera de una máquina con el stack levantado, tendrían
/// que compartir OrdersDb con el trabajo manual del día, y 8.3 no podría
/// correrlos en CI sin montar el compose entero.
/// </summary>
public sealed class SqlServerContainerFixture : IAsyncLifetime
{
    // La misma etiqueta que docker-compose.yml, y no la que Testcontainers trae
    // por defecto: así se reutiliza la imagen que el stack ya descargó en vez de
    // bajarse un CU distinto de 1,5 GB la primera vez que alguien corre la suite.
    //
    // La imagen va en el constructor y no en un .WithImage() encadenado: desde
    // Testcontainers 4.14 el constructor sin parámetros está marcado [Obsolete],
    // y este repositorio compila con 0 warnings.
    private readonly MsSqlContainer container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    /// <summary>
    /// Sin puerto fijo a propósito: el 1433 del host ya lo ocupa el `sqlserver`
    /// de docker-compose (ver docker-compose.override.yml), así que fijarlo haría
    /// que la suite fallara justo en la máquina que tiene el proyecto corriendo.
    /// El puerto aleatorio es el comportamiento por defecto de Testcontainers.
    /// </summary>
    public async ValueTask InitializeAsync() => await container.StartAsync();

    public async ValueTask DisposeAsync() => await container.DisposeAsync();

    /// <summary>
    /// El connection string de <c>GetConnectionString()</c> apunta a <c>master</c>
    /// y ya trae <c>TrustServerCertificate=True</c> (el certificado del
    /// contenedor es autofirmado, igual que el del compose). Lo único que cambia
    /// aquí es la base a la que se conecta.
    /// </summary>
    public string ConnectionStringFor(string databaseName) =>
        new SqlConnectionStringBuilder(container.GetConnectionString())
        {
            InitialCatalog = databaseName,
        }.ConnectionString;

    public Task CreateDatabaseAsync(string databaseName) =>
        ExecuteOnMasterAsync($"CREATE DATABASE [{databaseName}];");

    /// <summary>
    /// El <c>SINGLE_USER WITH ROLLBACK IMMEDIATE</c> no sobra: el pool de
    /// conexiones de ADO.NET puede tener sockets abiertos contra la base incluso
    /// después de que el host de test se haya ido, y un DROP con conexiones vivas
    /// falla con el error 3702.
    /// </summary>
    public Task DropDatabaseAsync(string databaseName) => ExecuteOnMasterAsync(
        $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}];");

    private async Task ExecuteOnMasterAsync(string sql)
    {
        await using var connection = new SqlConnection(container.GetConnectionString());
        await connection.OpenAsync();

        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
