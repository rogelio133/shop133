using Xunit;

namespace Shop133.ArchitectureTests;

/// <summary>
/// Reglas sobre los paquetes NuGet que no son una regla de capas.
///
/// La diferencia con <see cref="LayeringRulesTests"/> es deliberada: allí
/// <c>EfCorePackages_LiveOnlyIn_InfrastructureProjects</c> mira paquetes, pero
/// para afirmar la regla 5 de CLAUDE.md —la dirección de las flechas dentro de
/// un servicio—. Lo que se comprueba aquí no tiene que ver con las capas, sino
/// con qué versión de una dependencia es legítimo usar.
/// </summary>
[Trait("Category", "Fast")]
public sealed class PackageRulesTests
{
    private const string MassTransitPackagePrefix = "MassTransit";

    /// <summary>La única rama de MassTransit con licencia Apache-2.0.</summary>
    private const string AllowedMassTransitMajor = "8.";

    /// <summary>
    /// MassTransit 8.x es Apache-2.0 y recibe correcciones al menos hasta final
    /// de 2026. La v9 pasó a licencia comercial — y ya está publicada en
    /// nuget.org (9.2.0 al escribir esto, 3.1), así que un
    /// <c>dotnet add package MassTransit.RabbitMQ</c> sin fijar la versión
    /// instala la de pago sin avisar de nada.
    ///
    /// Ese es exactamente el fallo silencioso que este proyecto existe para
    /// evitar: la advertencia lleva desde la Fase 0 escrita en CLAUDE.md, y una
    /// regla que solo vive en prosa se rompe sin que nadie se entere. Aquí se
    /// rompe la build.
    ///
    /// La misma trampa se descartó ya una vez con FluentAssertions 8.x, por eso
    /// las aserciones de este repositorio son las de xUnit.
    ///
    /// Si alguna vez hay que subir a la v9, esto no se "arregla" ampliando el
    /// prefijo: se habla primero de la licencia.
    /// </summary>
    [Fact]
    public void MassTransitPackages_StayOnMajorVersion8()
    {
        var offenders = ProjectGraph.Projects.Values
            .SelectMany(
                project => project.PackageReferences.Where(IsDisallowedMassTransitVersion),
                (project, package) => $"{project.Name} → {package}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "MassTransit se queda en 8.x: la v9 tiene licencia comercial y este proyecto no la " +
            "tiene. Fijar la versión en el .csproj, nunca dejarla al criterio de 'dotnet add " +
            "package'. Referencias fuera de la 8.x: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// Un paquete del ecosistema MassTransit cuya versión no es de la rama 8.x.
    ///
    /// Una versión vacía cuenta como infracción a propósito: significaría que
    /// alguien la dejó al criterio de otro sitio, que es justo lo que esta regla
    /// impide. El prefijo cubre toda la familia
    /// (<c>MassTransit.RabbitMQ</c>, <c>MassTransit.EntityFrameworkCore</c> en
    /// 4.5, <c>MassTransit.TestFramework</c> en 3.7) porque comparten versión y
    /// licencia.
    /// </summary>
    private static bool IsDisallowedMassTransitVersion(PackageReferenceInfo package) =>
        package.Id.StartsWith(MassTransitPackagePrefix, StringComparison.Ordinal)
        && !package.Version.StartsWith(AllowedMassTransitMajor, StringComparison.Ordinal);
}
