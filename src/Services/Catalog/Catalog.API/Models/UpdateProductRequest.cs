using System.ComponentModel.DataAnnotations;

using Catalog.Infrastructure.Entities;

namespace Catalog.API.Models;

/// <summary>
/// Cuerpo de <c>PUT /products/{id}</c>. Reemplazo completo del recurso: el
/// cliente manda el producto entero, no los campos que cambian. Un PATCH por
/// campos sería otro tipo y otra acción, y no está en el roadmap.
///
/// Es un tipo aparte de <see cref="CreateProductRequest"/> aunque hoy tenga la
/// misma forma. Compartir uno solo obligaría a llamarlo algo que mintiera sobre
/// uno de los dos verbos, y en cuanto POST y PUT diverjan —un campo que solo se
/// fija al alta, o al revés— habría que separarlos con endpoints ya publicados.
/// La duplicación de seis propiedades es más barata que ese día.
///
/// El <c>Sku</c> se puede cambiar: la decisión 9 de docs/fase_1_1.md dice que el
/// código de negocio "se corrige y se renumera", a diferencia del Id. Por eso
/// este endpoint también puede devolver 409.
/// </summary>
public sealed record UpdateProductRequest
{
    [Required]
    [MaxLength(Product.SkuMaxLength)]
    public required string Sku { get; init; }

    [Required]
    [MaxLength(Product.NameMaxLength)]
    public required string Name { get; init; }

    [Required]
    [MaxLength(Product.DescriptionMaxLength)]
    public required string Description { get; init; }

    /// <summary>Mismo rango y misma bandera de cultura que en el alta — ver <see cref="CreateProductRequest.Price"/>.</summary>
    [Range(typeof(decimal), "0.01", "9999999999999999.99", ParseLimitsInInvariantCulture = true)]
    public required decimal Price { get; init; }

    [Range(0, int.MaxValue)]
    public required int Stock { get; init; }

    [MaxLength(Product.ImageUrlMaxLength)]
    public string? ImageUrl { get; init; }
}
