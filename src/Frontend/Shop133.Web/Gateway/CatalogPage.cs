namespace Shop133.Web.Gateway;

/// <summary>
/// El sobre que devuelve <c>GET /api/catalog/products</c> desde 6.2.1.
///
/// Re-declara <c>PagedResponse&lt;T&gt;</c> de Catalog.API en lugar de importar el tipo real, por
/// el mismo motivo que <see cref="CatalogProduct"/>: <c>Shop133.Web</c> tiene CERO
/// ProjectReference y la regla 3 de CLAUDE.md lo exige, con
/// <c>Frontend_DoesNotReference_ServicesOrGateway</c> haciendolo ejecutable desde 0.6.
///
/// Se llama <c>CatalogPage</c> y no <c>PagedResponse</c> a proposito: este es el modelo de CABLE
/// del frontend, y darle el mismo nombre que al tipo del servidor invitaria a "unificarlos" algun
/// dia, que es exactamente lo que la regla 3 prohibe.
///
/// <c>TotalItems</c> NO es <c>Items.Count</c>: cuenta los que cumplen el filtro en el catalogo
/// entero, que es lo que la cabecera de la vista tiene que decir mientras pinta veinte.
/// </summary>
public sealed record CatalogPage<T>
{
    public required IReadOnlyList<T> Items { get; init; }

    public required int Page { get; init; }

    public required int PageSize { get; init; }

    public required int TotalItems { get; init; }

    public required int TotalPages { get; init; }
}
