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
///
/// **Ya no tiene un factory <c>From(Category)</c>** (lo tuvo de 1.4 a 6.2.1):
/// desde que lleva <see cref="ProductCount"/>, este tipo no se puede construir a
/// partir de la entidad sola, así que el mapeo se hace en la proyección de la
/// consulta —que es donde puede salir el recuento— y el factory se quedaba sin
/// llamantes. <c>ProductResponse.From</c> sí sigue existiendo, porque un
/// producto sí se mapea entero desde su entidad.
/// </summary>
public sealed record CategoryResponse
{
    public required int Id { get; init; }

    public required string Name { get; init; }

    /// <summary>
    /// Cuántos productos hay en esta categoría (6.2.1). Cuenta el catálogo
    /// **entero**: ni la página que se esté viendo ni el filtro aplicado. Es lo
    /// que necesita un menú de categorías —donde el número tiene que seguir
    /// diciendo lo mismo después de elegir una— y es lo que el frontend hacía
    /// en memoria con un <c>GroupBy</c> sobre las 50 filas que se traía.
    ///
    /// Sale de una subconsulta correlacionada en <c>CategoriesController</c>.
    /// *Descartada* una navegación inversa <c>Category.Products</c>: la entidad
    /// son hoy dos propiedades, y añadirle una colección para contar regalaría
    /// de paso una forma de cargar diez productos sin querer.
    /// </summary>
    public required int ProductCount { get; init; }
}
