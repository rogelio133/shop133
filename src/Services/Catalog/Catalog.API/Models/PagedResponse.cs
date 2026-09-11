namespace Catalog.API.Models;

/// <summary>
/// El sobre de una respuesta paginada (6.2.1).
///
/// Vive en <c>Catalog.API/Models/</c> y **no** en <c>Shop133.Contracts</c>: la
/// regla 4 de CLAUDE.md reserva ese proyecto para los mensajes que viajan por
/// RabbitMQ. Este es el cuerpo de una respuesta HTTP de un solo servicio, el
/// mismo argumento que el <c>///</c> de <see cref="CreateProductRequest"/>.
///
/// *Descartada* la cabecera <c>X-Total-Count</c>, que es la alternativa clásica
/// y no habría roto el cuerpo de <c>GET /products</c>. Dos motivos: una cabecera
/// **no sale en el documento OpenAPI** —así que quien genere un cliente desde él
/// no se entera de que existe— y se pierde en cualquier capa intermedia que no
/// la propague explícitamente, que es justo lo que hay delante de este servicio
/// desde 5.1. El envelope **sí es un cambio rompedor** del cuerpo, y se puede
/// pagar hoy precisamente porque <c>GET /products</c> solo tiene **dos**
/// consumidores —el frontend y <c>Catalog.Tests</c>— y los dos se tocan en este
/// mismo punto.
///
/// Genérico aunque hoy solo lo use <see cref="ProductResponse"/>: la alternativa
/// era un <c>PagedProductResponse</c> concreto, y el día que <c>GET /orders</c>
/// de 6.2 se pagine habría dos tipos con la misma forma y distinto nombre. Aquí
/// el genérico no inventa nada — no hay ni una decisión que dependa de <c>T</c>.
/// </summary>
public sealed record PagedResponse<T>
{
    public required IReadOnlyList<T> Items { get; init; }

    /// <summary>La página devuelta, empezando en 1. Es la que pidió el cliente, no la que cupo.</summary>
    public required int Page { get; init; }

    public required int PageSize { get; init; }

    /// <summary>
    /// El total de elementos que cumplen el filtro, **no** los de esta página.
    /// Es lo que permite al frontend decir "50 productos" mientras pinta 12, y
    /// lo que hace calculable el paginador sin pedir todas las páginas.
    /// </summary>
    public required int TotalItems { get; init; }

    /// <summary>
    /// Calculado, no almacenado: es <c>ceil(TotalItems / PageSize)</c> y va en
    /// la respuesta para que cada cliente no lo re-derive —y redondee mal— por
    /// su cuenta. Con cero elementos son **cero** páginas, no una vacía.
    /// </summary>
    public required int TotalPages { get; init; }

    /// <summary>
    /// El único sitio donde se hace la aritmética del paginado. El <c>double</c>
    /// del <c>Math.Ceiling</c> no es casual: con enteros, <c>total / pageSize</c>
    /// trunca y la última página parcial desaparece sin que nada avise.
    /// </summary>
    public static PagedResponse<T> From(IReadOnlyList<T> items, int page, int pageSize, int totalItems) => new()
    {
        Items = items,
        Page = page,
        PageSize = pageSize,
        TotalItems = totalItems,
        TotalPages = (int)Math.Ceiling(totalItems / (double)pageSize),
    };
}
