using Catalog.Infrastructure.Entities;

namespace Catalog.API.Models;

/// <summary>
/// Lo que devuelve <c>GET /categories</c>.
///
/// Existe por el mismo motivo que <see cref="ProductResponse"/>: no serializar
/// la entidad. Aquí el argumento se ve más claro que nunca — si mañana
/// <see cref="Category"/> gana un <c>Slug</c> o un orden de visualización,
/// esas columnas aparecerían en la respuesta HTTP sin que nadie lo decidiera.
///
/// Sin validation attributes: son cosa de la entrada, y aquí no hay entrada.
/// </summary>
public sealed record CategoryResponse
{
    public required int Id { get; init; }

    public required string Name { get; init; }

    public static CategoryResponse From(Category category) => new()
    {
        Id = category.Id,
        Name = category.Name,
    };
}
