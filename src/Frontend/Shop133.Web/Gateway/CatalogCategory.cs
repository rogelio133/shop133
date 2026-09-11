namespace Shop133.Web.Gateway;

/// <summary>
/// Una categoria del catalogo, tal y como la sirve <c>GET /api/catalog/categories</c>.
///
/// Se re-declara por el mismo motivo que <see cref="CatalogProduct"/>: cero ProjectReference.
///
/// Catalog.API las devuelve ordenadas por nombre, no por id (1.4), asi que el filtro de la
/// vista no tiene que volver a ordenarlas.
/// </summary>
public sealed record CatalogCategory
{
    public required int Id { get; init; }

    public required string Name { get; init; }

    /// <summary>
    /// Cuantos productos tiene la categoria en el catalogo ENTERO — ni la pagina que se esta
    /// viendo, ni el filtro aplicado. Lo manda la API desde 6.2.1.
    ///
    /// Hasta 6.2.1 este numero lo calculaba <c>CatalogController</c> con un <c>GroupBy</c> sobre
    /// los productos ya traidos, lo cual solo funcionaba porque se traia el catalogo entero en
    /// cada render. Con paginacion eso deja de ser cierto: un <c>GroupBy</c> sobre una pagina de
    /// veinte contaria veinte. Que el recuento llegue de la API no es una optimizacion, es lo
    /// unico que sigue dando la respuesta correcta.
    /// </summary>
    public required int ProductCount { get; init; }
}
