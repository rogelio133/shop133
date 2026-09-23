namespace Shop133.Web.Gateway;

/// <summary>
/// La foto de un producto tal y como la sirve Catalog.API a traves del Gateway.
///
/// Re-declara los campos de <c>ProductResponse</c> en lugar de importar el tipo real, y no es
/// una duplicacion por descuido: <c>Shop133.Web</c> tiene CERO ProjectReference y la regla 3 de
/// CLAUDE.md lo exige, con <c>Frontend_DoesNotReference_ServicesOrGateway</c> haciendolo
/// ejecutable desde 0.6. Mismo motivo y misma forma que el <c>CatalogProduct</c> que 2.3 tenia
/// en Orders, que re-declaraba 4 de los 9 campos por la regla hermana entre servicios.
///
/// Aqui se re-declaran los NUEVE, al reves que en 2.3: la card pinta siete y la ficha de
/// detalle los pinta todos.
///
/// Los <c>required</c> no son decoracion. 3.1 midio contra el serializador real que un cuerpo
/// al que le falta una propiedad requerida lanza <c>JsonException</c> nombrandola, en vez de
/// materializar un objeto a medias — asi que un catalogo truncado revienta en el cliente en
/// lugar de pintar un producto de 0,00.
/// </summary>
public sealed record CatalogProduct
{
    public required int Id { get; init; }

    public required string Sku { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required decimal Price { get; init; }

    /// <summary>
    /// El stock que el catalogo MUESTRA, no el que se puede reservar. Desde 3.4 el reservable
    /// vive en <c>InventoryDb</c> y lo decide Inventory al consumir <c>OrderCreated</c>; nadie
    /// sincroniza las dos columnas. La vista tiene que decirlo en voz alta en vez de presentar
    /// este numero como una garantia — es justo lo que 6.5 ensena cuando un pedido se cancela
    /// despues de que esta cifra dijera 42.
    /// </summary>
    public required int Stock { get; init; }

    /// <summary>
    /// Anulable porque lo es en origen: <c>CreateProductRequest.ImageUrl</c> es opcional, asi
    /// que un producto creado por <c>POST /products</c> puede no traer imagen. Las 50 filas del
    /// seed de 1.4 si la traen —<c>/img/products/&lt;sku&gt;.jpg</c>—, y esa ruta esta anclada al
    /// origen del PROPIO frontend, no al del Gateway: las imagenes las sirve
    /// <c>MapStaticAssets()</c> de este proyecto y no gastan cupo del rate limiter de 5.2.
    /// </summary>
    public string? ImageUrl { get; init; }

    public required int CategoryId { get; init; }

    /// <summary>
    /// Viene resuelto en la propia respuesta desde 1.4, que es lo que permite pintar la card
    /// sin una segunda llamada por producto.
    /// </summary>
    public required string CategoryName { get; init; }
}
