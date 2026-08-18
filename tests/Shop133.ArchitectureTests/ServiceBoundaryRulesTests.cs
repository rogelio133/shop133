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
        // Hoy no hay ningún DbContext — el primero llega en 1.2 (CatalogDb).
        // Este test pasa en vacío a propósito: existe para que el día que
        // aparezca uno fuera de su sitio, falle solo.
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
}
