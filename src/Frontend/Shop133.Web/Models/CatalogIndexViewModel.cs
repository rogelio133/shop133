using Shop133.Web.Gateway;

namespace Shop133.Web.Models;

/// <summary>
/// Lo que pinta la vista del catalogo.
///
/// Existe —y <c>CatalogProduct</c> se usa tal cual dentro de el— por un motivo que es de la
/// PAGINA y no de los campos: la pagina tiene estado que la API no manda en ningun sitio (cual es
/// el filtro activo) y estado que hay que juntar de DOS respuestas distintas (los productos de una
/// llamada, los chips de otra). Ese es el trabajo que este tipo hace.
///
/// Descartado un <c>ProductViewModel</c> que copiara los nueve campos de <c>CatalogProduct</c>:
/// serian nueve asignaciones y ni una decision, o sea el passthrough que 1.3 rechazo al no
/// meter un repositorio sobre un CRUD, e inventar la forma antes del caso de uso, que es lo que
/// 1.1 rechazo con <c>Product.Update()</c> y 2.1 con <c>Order.Confirm()</c>. Se gana su sitio el
/// dia que la card necesite algo que el cable no trae — probablemente 6.3, con su formulario de
/// "anadir al carrito", su cantidad y su token antiforgery.
///
/// **Desde 6.2.1 ya no hay un <c>CategoryFilterViewModel</c>.** Existia para cargar un recuento
/// que este controller calculaba en memoria con un <c>GroupBy</c>; ahora ese numero lo manda
/// <c>GET /categories</c>, asi que <see cref="CatalogCategory"/> —id, nombre y recuento— ES el
/// chip, y un tipo que solo copiara esos tres campos seria el passthrough del parrafo anterior.
/// </summary>
public sealed record CatalogIndexViewModel
{
    /// <summary>Los de ESTA pagina, no el catalogo entero. Para el total esta <see cref="TotalItems"/>.</summary>
    public required IReadOnlyList<CatalogProduct> Products { get; init; }

    public required IReadOnlyList<CatalogCategory> Categories { get; init; }

    /// <summary>
    /// <c>null</c> significa "sin filtro", no "categoria desconocida".
    /// </summary>
    public int? SelectedCategoryId { get; init; }

    public bool IsFiltered => SelectedCategoryId is not null;

    public required int Page { get; init; }

    public required int TotalPages { get; init; }

    /// <summary>
    /// Los productos que cumplen el filtro en el catalogo entero. Es lo que la cabecera dice
    /// mientras el grid pinta veinte, y lo que distingue los dos estados vacios de la vista.
    /// </summary>
    public required int TotalItems { get; init; }

    /// <summary>
    /// Cierto cuando se pidio una pagina que no existe y HAY resultados — un <c>?page=99</c>
    /// escrito a mano, o un enlace viejo.
    ///
    /// Tiene rama propia porque el mensaje "no hay productos en esta categoria" seria FALSO: si
    /// hay, solo que no en esta pagina. Es el mismo criterio con el que la vista ya separaba
    /// "categoria vacia" de "catalogo vacio" desde 6.2.
    /// </summary>
    public bool IsPastLastPage => Products.Count == 0 && TotalItems > 0;
}
