using System.Reflection;
using System.Runtime.CompilerServices;

using NetArchTest.Rules;

using Shop133.Contracts.Events;

using Xunit;

namespace Shop133.ArchitectureTests;

/// <summary>
/// Regla 4 de CLAUDE.md: "Shop133.Contracts stays thin". Records inmutables
/// para eventos y DTOs; sin lógica de negocio, sin EF Core, sin MassTransit,
/// sin atributos de validación. Todos los servicios lo referencian; él no
/// referencia a nadie.
/// </summary>
[Trait("Category", "Fast")]
public sealed class ContractsRulesTests
{
    private static readonly Assembly ContractsAssembly = typeof(OrderCreated).Assembly;

    /// <summary>
    /// Prefijos de ensamblado que sí puede referenciar Contracts: solo la BCL.
    /// </summary>
    private static readonly string[] AllowedAssemblyPrefixes =
    [
        "System",
        "netstandard",
        "mscorlib",
    ];

    /// <summary>
    /// Namespaces prohibidos de forma explícita, uno por cada forma conocida de
    /// engordar el contrato: mensajería, persistencia y validación.
    /// </summary>
    private static readonly string[] ForbiddenNamespaces =
    [
        "MassTransit",
        "Microsoft.EntityFrameworkCore",
        "System.ComponentModel.DataAnnotations",
    ];

    [Fact]
    public void Contracts_Csproj_DeclaresNoProjectOrPackageReferences()
    {
        var contracts = ProjectGraph.Get(ProjectGraph.ContractsProjectName);

        Assert.True(
            contracts.ProjectReferences.Count == 0,
            $"{contracts.Name} no debe referenciar ningún proyecto, pero referencia: " +
            string.Join(", ", contracts.ProjectReferences));

        Assert.True(
            contracts.PackageReferences.Count == 0,
            $"{contracts.Name} no debe referenciar ningún paquete NuGet, pero referencia: " +
            string.Join(", ", contracts.PackageReferences));
    }

    [Fact]
    public void Contracts_ReferencedAssemblies_AreBclOnly()
    {
        var offenders = ContractsAssembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => !AllowedAssemblyPrefixes.Any(
                prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Shop133.Contracts solo puede depender de la BCL. Referencias ajenas: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void Contracts_Types_HaveNoDependencyOnForbiddenNamespaces()
    {
        var result = Types.InAssembly(ContractsAssembly)
            .Should()
            .NotHaveDependencyOnAny(ForbiddenNamespaces)
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            "Tipos de Contracts que dependen de " + string.Join(" / ", ForbiddenNamespaces) + ": " +
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Contracts_PublicTypes_AreSealedRecords()
    {
        var offenders = ContractsAssembly.GetExportedTypes()
            .Where(type => !IsRecord(type) || !type.IsSealed)
            .Select(type => type.FullName ?? type.Name)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Todo tipo público de Contracts debe ser un 'sealed record'. Incumplen: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void Contracts_PublicMembers_AreImmutable()
    {
        var offenders = new List<string>();

        foreach (var type in ContractsAssembly.GetExportedTypes())
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.SetMethod is { } setter && !IsInitOnly(setter))
                {
                    offenders.Add($"{type.Name}.{property.Name} (set mutable)");
                }
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                offenders.Add($"{type.Name}.{field.Name} (campo público)");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Los mensajes viajan entre servicios: sus miembros deben ser 'init' o de solo lectura. Incumplen: " +
            string.Join(", ", offenders));
    }

    /// <summary>
    /// El compilador sintetiza un método &lt;Clone&gt;$ en cada record — es la
    /// forma fiable de distinguir un record de una clase a nivel de reflexión,
    /// porque "record" no deja ningún flag en los metadatos del tipo.
    /// </summary>
    private static bool IsRecord(Type type) =>
        type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) is not null;

    /// <summary>
    /// Un setter 'init' es un setter normal cuyo parámetro de retorno lleva el
    /// modificador requerido IsExternalInit. No hay otra marca que lo distinga.
    /// </summary>
    private static bool IsInitOnly(MethodInfo setter) =>
        setter.ReturnParameter
            .GetRequiredCustomModifiers()
            .Contains(typeof(IsExternalInit));
}
