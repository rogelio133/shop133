using Shop133.TestUtilities;

using Xunit;

namespace Catalog.Tests.Infrastructure;

/// <summary>
/// Reúne todas las clases de test bajo un único
/// <see cref="SqlServerContainerFixture"/>, para levantar un solo contenedor.
///
/// Tiene un segundo efecto, y es deliberado: xUnit paraleliza *entre*
/// collections, nunca dentro de una. Al estar todas las clases en la misma, se
/// ejecutan en serie — así el contenedor no recibe varias creaciones de base de
/// datos y varias migraciones a la vez, que es donde SQL Server empieza a dar
/// timeouts en una imagen con la memoria por defecto.
/// </summary>
[CollectionDefinition(Name)]
public sealed class CatalogApiCollection : ICollectionFixture<SqlServerContainerFixture>
{
    public const string Name = "catalog-api";
}
