namespace Orders.Infrastructure.Catalog;

/// <summary>
/// PHASE-2 DEBT: replaced by OrderCreated event in Phase 3.
///
/// La forma del JSON que devuelve <c>GET /products/{id}</c> de Catalog.API,
/// recortada a lo que Orders necesita.
///
/// **Cuatro campos y no los nueve que manda Catalog.** No es pereza: tres de
/// ellos (<see cref="Sku"/>, <see cref="Name"/>, <see cref="Price"/>) son
/// exactamente los que <c>OrderItem</c> congela, y el <see cref="Id"/> es el
/// puntero débil. Copiar también <c>description</c>, <c>stock</c>,
/// <c>imageUrl</c> o <c>categoryName</c> sería declarar una dependencia sobre
/// campos que este servicio no usa: el día que Catalog renombre uno, Orders se
/// enteraría sin motivo. System.Text.Json ignora las propiedades sobrantes del
/// JSON por defecto, así que recortar aquí no cuesta nada.
///
/// **No se importa <c>Catalog.API.Models.ProductResponse</c>**, y no por gusto:
/// <c>ServiceProjects_DoNotReference_OtherServices</c> lo prohíbe, y esa regla es
/// justo la barrera que este punto existe para tocar. Un servicio que consume a
/// otro por HTTP declara su propia vista del contrato; si los dos tipos se
/// desincronizan, eso *es* la información — significa que el contrato cambió.
///
/// El propio <c>ProductResponse</c> tampoco es la entidad <c>Product</c>: lo que
/// llega aquí ya es la tercera copia del mismo dato. Ese coste desaparece en la
/// Fase 3, cuando lo que viaje sea <c>OrderLine</c> de Shop133.Contracts, un tipo
/// compartido de verdad.
/// </summary>
public sealed record CatalogProduct
{
    /// <summary>
    /// El <c>int</c> del IDENTITY de <c>CatalogDb</c>. Es lo que acaba en
    /// <c>OrderItem.ProductId</c>, y desde 3.4 lo que Inventory usa para
    /// reservar.
    /// </summary>
    public required int Id { get; init; }

    /// <summary>Ya viene recortado y en mayúsculas: lo normaliza la entidad de Catalog.</summary>
    public required string Sku { get; init; }

    public required string Name { get; init; }

    /// <summary>
    /// La única fuente de precios del sistema (regla 1: Orders no puede leer
    /// <c>CatalogDb</c>). Se congela en la línea de pedido y nadie lo vuelve a
    /// consultar — ver la nota de <c>OrderItem</c>.
    /// </summary>
    public required decimal Price { get; init; }
}
