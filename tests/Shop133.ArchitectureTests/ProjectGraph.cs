using System.Xml.Linq;

namespace Shop133.ArchitectureTests;

/// <summary>
/// Un proyecto de la solución, leído de su .csproj.
/// </summary>
internal sealed class ProjectInfo
{
    /// <summary>Nombre del ensamblado: "Catalog.API", "Shop133.Contracts".</summary>
    public required string Name { get; init; }

    /// <summary>Ruta relativa a la raíz del repo, con barras normales.</summary>
    public required string RelativePath { get; init; }

    /// <summary>
    /// Servicio al que pertenece ("Catalog", "Orders", …) o null si el proyecto
    /// no vive bajo src/Services/ (Contracts, Gateway, Web).
    /// </summary>
    public required string? Service { get; init; }

    /// <summary>Nombres de los proyectos referenciados directamente.</summary>
    public required IReadOnlyList<string> ProjectReferences { get; init; }

    /// <summary>Ids de los paquetes NuGet referenciados directamente.</summary>
    public required IReadOnlyList<string> PackageReferences { get; init; }

    public bool IsApi => Name.EndsWith(".API", StringComparison.Ordinal);

    public bool IsInfrastructure => Name.EndsWith(".Infrastructure", StringComparison.Ordinal);

    public bool IsDomain => Name.EndsWith(".Domain", StringComparison.Ordinal);

    public override string ToString() => Name;
}

/// <summary>
/// El grafo de referencias entre proyectos, construido leyendo los .csproj de
/// src/ — no por reflexión sobre los ensamblados.
///
/// El motivo es que Roslyn poda del manifiesto las referencias que el código no
/// usa. Con los proyectos de servicio todavía vacíos (Fase 0),
/// Assembly.GetReferencedAssemblies() devuelve prácticamente nada y cualquier
/// regla del tipo "X no referencia a Y" sería cierta en vacío. El .csproj es
/// donde la referencia se *declara*, así que una infracción se detecta en el
/// momento en que alguien la añade, no cuando escribe el primer using.
/// </summary>
internal static class ProjectGraph
{
    public const string ContractsProjectName = "Shop133.Contracts";

    /// <summary>Raíz del repositorio, localizada por la presencia de shop133.slnx.</summary>
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    /// <summary>Todos los proyectos bajo src/, indexados por nombre.</summary>
    public static IReadOnlyDictionary<string, ProjectInfo> Projects { get; } = LoadProjects();

    /// <summary>Los proyectos que pertenecen a un servicio (viven bajo src/Services/).</summary>
    public static IReadOnlyList<ProjectInfo> ServiceProjects { get; } =
        Projects.Values.Where(p => p.Service is not null).OrderBy(p => p.Name).ToList();

    public static ProjectInfo Get(string name) =>
        Projects.TryGetValue(name, out var project)
            ? project
            : throw new InvalidOperationException(
                $"No existe el proyecto '{name}' bajo src/. Proyectos encontrados: " +
                string.Join(", ", Projects.Keys.OrderBy(k => k)));

    /// <summary>
    /// Cierre transitivo de las referencias de un proyecto, sin incluirlo a él.
    /// Recorrido iterativo para no depender de que el grafo sea acíclico.
    /// </summary>
    public static IReadOnlyList<ProjectInfo> TransitiveReferencesOf(ProjectInfo project)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ProjectInfo>();
        var pending = new Stack<string>(project.ProjectReferences);

        while (pending.Count > 0)
        {
            var name = pending.Pop();
            if (!visited.Add(name) || name == project.Name)
            {
                continue;
            }

            var referenced = Get(name);
            result.Add(referenced);

            foreach (var next in referenced.ProjectReferences)
            {
                pending.Push(next);
            }
        }

        return result;
    }

    private static string FindRepositoryRoot()
    {
        // AppContext.BaseDirectory es .../tests/Shop133.ArchitectureTests/bin/Debug/net10.0/
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "shop133.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"No se encontró shop133.slnx subiendo desde '{AppContext.BaseDirectory}'. " +
            "Los tests de arquitectura leen los .csproj del repositorio y necesitan su raíz.");
    }

    private static Dictionary<string, ProjectInfo> LoadProjects()
    {
        var sourceRoot = Path.Combine(RepositoryRoot, "src");
        var projects = new Dictionary<string, ProjectInfo>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var document = XDocument.Load(path);
            var name = Path.GetFileNameWithoutExtension(path);
            var relativePath = Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/');

            projects.Add(name, new ProjectInfo
            {
                Name = name,
                RelativePath = relativePath,
                Service = ServiceOf(relativePath),
                ProjectReferences = document.Descendants("ProjectReference")
                    .Select(element => (string?)element.Attribute("Include"))
                    .Where(include => !string.IsNullOrWhiteSpace(include))
                    .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', '/')))
                    .OrderBy(referenceName => referenceName, StringComparer.Ordinal)
                    .ToList(),
                PackageReferences = document.Descendants("PackageReference")
                    .Select(element => (string?)element.Attribute("Include"))
                    .Where(include => !string.IsNullOrWhiteSpace(include))
                    .Select(include => include!)
                    .OrderBy(packageId => packageId, StringComparer.Ordinal)
                    .ToList(),
            });
        }

        return projects;
    }

    /// <summary>
    /// "src/Services/Catalog/Catalog.API/Catalog.API.csproj" → "Catalog".
    /// Cualquier otra ruta → null.
    /// </summary>
    private static string? ServiceOf(string relativePath)
    {
        var segments = relativePath.Split('/');

        return segments is ["src", "Services", var service, ..] ? service : null;
    }
}
