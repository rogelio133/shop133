using Catalog.Infrastructure.Entities;

namespace Catalog.API.Models;

/// <summary>
/// Lo que devuelven los endpoints de lectura y el 201 del alta.
///
/// Existe para no serializar <see cref="Product"/> directamente. Hoy los campos
/// coinciden uno a uno y el tipo parece redundante, pero la entidad es el modelo
/// de persistencia: en cuanto 1.4 o la Fase 3 le añadan una columna interna, esa
/// columna aparecería en la respuesta HTTP sin que nadie lo decidiera. El DTO es
/// el punto donde se elige qué sale.
///
/// Sin validation attributes: son cosa de la entrada. Un tipo de salida no se
/// valida, se construye a partir de datos que ya pasaron por la entidad.
/// </summary>
public sealed record ProductResponse
{
    public required int Id { get; init; }

    /// <summary>Normalizado en mayúsculas por la entidad — lo que salga aquí es lo que hay en la tabla.</summary>
    public required string Sku { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required decimal Price { get; init; }

    public required int Stock { get; init; }

    public string? ImageUrl { get; init; }

    /// <summary>
    /// El único mapeo entidad → DTO del servicio. Está aquí y no en el
    /// controller para que las acciones sigan siendo bind, delega y devuelve.
    /// </summary>
    public static ProductResponse From(Product product) => new()
    {
        Id = product.Id,
        Sku = product.Sku,
        Name = product.Name,
        Description = product.Description,
        Price = product.Price,
        Stock = product.Stock,
        ImageUrl = product.ImageUrl,
    };
}
