using Xunit;

namespace Shop133.ArchitectureTests;

/// <summary>
/// Reglas 1 y 3 de CLAUDE.md: una base de datos por servicio y un frontend que
/// solo habla con el Gateway.
///
/// La regla 1 tiene ya un cerrojo en SQL Server desde 0.4 (un login por
/// servicio, sin permisos sobre las bases ajenas). Lo que se comprueba aquí es
/// el escalón anterior: que un servicio no pueda ni siquiera *compilar* contra
/// el DbContext de otro, porque no lo referencia.
/// </summary>
[Trait("Category", "Fast")]
public sealed class ServiceBoundaryRulesTests
{
    [Fact]
    public void ServiceProjects_DoNotReference_OtherServices()
    {
        var offenders = ProjectGraph.ServiceProjects
            .SelectMany(
                project => ProjectGraph.TransitiveReferencesOf(project)
                    .Where(reference => reference.Service is not null && reference.Service != project.Service),
                (project, reference) => $"{project.Name} → {reference.Name}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Un servicio no referencia a otro; lo único compartido es Shop133.Contracts. " +
            "Si necesita sus datos, van por evento o por API. Referencias prohibidas: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void DbContextFiles_LiveOnlyIn_OwningServiceInfrastructure()
    {
        // Escrito en 0.6 cuando no había ninguno; desde 3.4 vigila tres
        // (CatalogDbContext, OrdersDbContext, InventoryDbContext).
        var offenders = Directory
            .EnumerateFiles(Path.Combine(ProjectGraph.RepositoryRoot, "src"), "*DbContext.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(ProjectGraph.RepositoryRoot, path).Replace('\\', '/'))
            .Where(relativePath => !relativePath.Contains("/obj/", StringComparison.Ordinal))
            .Where(relativePath => !IsInsideOwnInfrastructure(relativePath))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Un DbContext vive en el .Infrastructure de su propio servicio y en ningún otro sitio. " +
            "Fuera de lugar: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// La convención de CLAUDE.md —"MassTransit consumers are *not* controllers:
    /// they live in <c>Consumers/</c>"— hecha ejecutable en 3.4, con el primer
    /// consumer del proyecto delante.
    ///
    /// Merece test y no solo prosa porque el sitio de un consumer no es
    /// cosmético: es el único código del servicio que se ejecuta sin que nadie
    /// haga una petición HTTP, y mezclarlo con <c>Controllers/</c> hace que deje
    /// de verse. Una regla que solo vive en prosa se rompe en silencio, que es
    /// justo el fallo que este proyecto existe para evitar.
    /// </summary>
    [Fact]
    public void ConsumerFiles_LiveOnlyIn_ServiceApiConsumersFolder()
    {
        var offenders = Directory
            .EnumerateFiles(Path.Combine(ProjectGraph.RepositoryRoot, "src"), "*Consumer.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(ProjectGraph.RepositoryRoot, path).Replace('\\', '/'))
            .Where(relativePath => !relativePath.Contains("/obj/", StringComparison.Ordinal))
            .Where(relativePath => !IsInsideOwnApiConsumersFolder(relativePath))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Un consumer de MassTransit vive en Consumers/, dentro del .API de su propio servicio, " +
            "y no es un controller. Fuera de lugar: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// La regla 5 de CLAUDE.md dice que la excepción de Orders.Domain existe
    /// "because saga state machines live in Orders.Domain". Hasta 4.1 eso era solo
    /// prosa: ninguna regla miraba dónde estaba una máquina de estados, porque no
    /// había ninguna.
    ///
    /// Merece test por lo mismo que lo merecía el sitio de un consumer: mover la
    /// saga a Orders.Infrastructure o a Orders.API compilaría sin una queja y
    /// dejaría el proyecto de dominio sin la única razón por la que existe. Es
    /// exactamente el fallo silencioso que este proyecto quiere evitar.
    ///
    /// Nótese que la regla es más estrecha que las otras dos de este fichero: no
    /// dice "en el .Domain de su servicio" sino "en Orders.Domain", en singular.
    /// Es deliberado — hoy solo hay una saga y solo Orders tiene capa de dominio,
    /// y una regla que ya contemplara servicios que no existen sería un filtro que
    /// nunca engancha. Si algún día hay una segunda saga, se ensancha entonces.
    /// </summary>
    [Fact]
    public void StateMachineFiles_LiveOnlyIn_OrdersDomain()
    {
        var offenders = Directory
            .EnumerateFiles(Path.Combine(ProjectGraph.RepositoryRoot, "src"), "*StateMachine.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(ProjectGraph.RepositoryRoot, path).Replace('\\', '/'))
            .Where(relativePath => !relativePath.Contains("/obj/", StringComparison.Ordinal))
            .Where(relativePath => !IsInsideOrdersDomain(relativePath))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Una máquina de estados de saga vive en Orders.Domain: es la razón por la que ese " +
            "proyecto existe y por la que la regla 5 le permite referenciar Shop133.Contracts. " +
            "Fuera de lugar: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Frontend_DoesNotReference_ServicesOrGateway()
    {
        var frontend = ProjectGraph.Get("Shop133.Web");

        var offenders = ProjectGraph.TransitiveReferencesOf(frontend)
            .Where(reference => reference.Service is not null || reference.Name == "Shop133.Gateway")
            .Select(reference => reference.Name)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Shop133.Web solo habla con el Gateway por HTTP, sin referencia a ningún servicio. " +
            "Referencias prohibidas: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// "src/Services/Catalog/Catalog.Infrastructure/CatalogDbContext.cs" es válido;
    /// cualquier otra ubicación no lo es.
    /// </summary>
    private static bool IsInsideOwnInfrastructure(string relativePath)
    {
        var segments = relativePath.Split('/');

        return segments is ["src", "Services", var service, var project, ..]
            && project == $"{service}.Infrastructure";
    }

    /// <summary>
    /// "src/Services/Inventory/Inventory.API/Consumers/OrderCreatedConsumer.cs" es
    /// válido; cualquier otra ubicación no lo es. Nótese que exige la carpeta
    /// <c>Consumers/</c> justo debajo del proyecto, no en cualquier profundidad:
    /// un consumer bajo <c>Controllers/</c> es exactamente lo que se persigue.
    /// </summary>
    private static bool IsInsideOwnApiConsumersFolder(string relativePath)
    {
        var segments = relativePath.Split('/');

        return segments is ["src", "Services", var service, var project, "Consumers", ..]
            && project == $"{service}.API";
    }

    /// <summary>
    /// "src/Services/Orders/Orders.Domain/Sagas/OrderStateMachine.cs" es válido;
    /// cualquier otra ubicación no lo es. No se exige la carpeta <c>Sagas/</c> —
    /// lo que la regla defiende es el proyecto, no el orden interno de sus
    /// carpetas.
    /// </summary>
    private static bool IsInsideOrdersDomain(string relativePath) =>
        relativePath.Split('/') is ["src", "Services", "Orders", "Orders.Domain", ..];
}
