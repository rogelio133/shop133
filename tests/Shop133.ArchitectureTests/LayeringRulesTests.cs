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
}
