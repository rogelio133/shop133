using Xunit;

namespace Shop133.ArchitectureTests;

/// <summary>
/// Regla 5 de CLAUDE.md: dentro de un servicio las flechas van en un solo
/// sentido, .API → .Infrastructure → .Domain. Invertir una sola de ellas
/// convierte las tres capas en una.
/// </summary>
[Trait("Category", "Fast")]
public sealed class LayeringRulesTests
{
    private const string EfCorePackagePrefix = "Microsoft.EntityFrameworkCore";

    /// <summary>
    /// El único paquete de EF Core que puede vivir fuera de .Infrastructure, y
    /// solo en un .API. No es una concesión estética: <c>dotnet ef</c> construye
    /// el host del startup project para leer la configuración, y busca ahí las
    /// herramientas de diseño.
    /// </summary>
    private const string DesignPackageId = "Microsoft.EntityFrameworkCore.Design";

    [Fact]
    public void OrdersDomain_ProjectReferences_ContainOnlyContracts()
    {
        var domain = ProjectGraph.Get("Orders.Domain");

        var offenders = domain.ProjectReferences
            .Where(reference => reference != ProjectGraph.ContractsProjectName)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Orders.Domain solo puede referenciar Shop133.Contracts (la OrderStateMachine consume " +
            "sus mensajes). Referencias de más: " + string.Join(", ", offenders));
    }

    [Fact]
    public void DomainProjects_DoNotReference_InfrastructureOrApi()
    {
        var offenders = ProjectGraph.Projects.Values
            .Where(project => project.IsDomain)
            .SelectMany(
                project => ProjectGraph.TransitiveReferencesOf(project)
                    .Where(reference => reference.IsInfrastructure || reference.IsApi),
                (project, reference) => $"{project.Name} → {reference.Name}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "La capa de dominio no mira hacia fuera. Referencias prohibidas: " +
            string.Join(", ", offenders));
    }

    /// <summary>
    /// La misma regla 5 pero mirando paquetes en vez de proyectos: EF Core es
    /// una decisión de la capa de persistencia y no debe filtrarse a las otras.
    /// Un <c>DbSet</c> en un controller o un <c>[Index]</c> sobre una entidad de
    /// dominio compilan perfectamente; lo que los impide es no tener el paquete.
    ///
    /// Vive desde 1.2, que es cuando entró el primer paquete de EF Core en el
    /// repositorio (Catalog.Infrastructure).
    /// </summary>
    [Fact]
    public void EfCorePackages_LiveOnlyIn_InfrastructureProjects()
    {
        var offenders = ProjectGraph.Projects.Values
            .Where(project => !project.IsInfrastructure)
            .SelectMany(
                project => project.PackageReferences.Where(package => IsForbiddenEfCorePackage(project, package.Id)),
                (project, package) => $"{project.Name} → {package.Id}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "EF Core solo se declara en la capa .Infrastructure. La única excepción es " +
            $"{DesignPackageId} en un proyecto .API: las herramientas dotnet-ef lo buscan en el " +
            "startup project, no en el que contiene el DbContext. Paquetes fuera de sitio: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void InfrastructureProjects_DoNotReference_ApiProjects()
    {
        var offenders = ProjectGraph.Projects.Values
            .Where(project => project.IsInfrastructure)
            .SelectMany(
                project => ProjectGraph.TransitiveReferencesOf(project).Where(reference => reference.IsApi),
                (project, reference) => $"{project.Name} → {reference.Name}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "La flecha .API → .Infrastructure no se invierte. Referencias prohibidas: " +
            string.Join(", ", offenders));
    }

    /// <summary>
    /// Un paquete de EF Core en un proyecto que no es .Infrastructure, salvo el
    /// caso de <see cref="DesignPackageId"/> en un .API.
    /// </summary>
    private static bool IsForbiddenEfCorePackage(ProjectInfo project, string packageId)
    {
        if (!packageId.StartsWith(EfCorePackagePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        return !(project.IsApi && packageId == DesignPackageId);
    }
}
