using Orders.Domain.Entities;

namespace Orders.API.Models;

/// <summary>
/// Lo que devuelven el 201 de <c>POST /orders</c> y el 200 de
/// <c>GET /orders/{id}</c>.
///
/// Existe para no serializar <see cref="Order"/> directamente: la entidad va a
/// ganar campos internos en la Fase 4 (la saga mueve el estado, y 4.5 le pone al
/// lado una tabla de instancias) y ninguno de ellos debería aparecer en la
/// respuesta HTTP sin que alguien lo decida.
/// </summary>
public sealed record OrderResponse
{
    /// <summary>
    /// El <c>Guid</c> que acuñó la entidad. Desde la Fase 4 es también la clave de
    /// correlación de la saga, así que este valor es el que permite seguir el
    /// pedido por RabbitMQ y por Jaeger.
    /// </summary>
    public required Guid Id { get; init; }

    public required string CustomerEmail { get; init; }

    /// <summary>
    /// El estado como texto (<c>"Pending"</c>), no como el número que guarda la
    /// columna. Un cliente que lea <c>1</c> se acopla al orden del enum, y ese
    /// orden es un detalle de persistencia: 2.1 fijó valores explícitos justo
    /// para poder añadir estados al final sin renumerar filas.
    ///
    /// *Descartado* registrar un <c>JsonStringEnumConverter</c> global en
    /// Program.cs: cambiaría la serialización de todo el servicio por un solo
    /// campo, y de forma invisible desde aquí. La conversión vive en
    /// <see cref="From"/>, que ya es el sitio donde se traduce entidad → JSON.
    ///
    /// En la Fase 2 solo se alcanza <c>Pending</c>; los demás valores los produce
    /// la saga.
    /// </summary>
    public required string Status { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// La suma de los subtotales. Calculado por la entidad, sin columna propia:
    /// una sola fuente de verdad — ver la nota de <c>Order.Total</c>.
    /// </summary>
    public required decimal Total { get; init; }

    public required IReadOnlyList<OrderItemResponse> Items { get; init; }

    /// <summary>
    /// El único mapeo entidad → DTO del servicio, fuera del controller para que
    /// las acciones sigan siendo bind, delega y devuelve.
    ///
    /// A diferencia de <c>ProductResponse.From</c>, aquí no hace falta guarda
    /// contra una navegación sin cargar: las líneas son un tipo *owned* (decisión
    /// 1 de docs/fase_2_2.md), así que EF las trae siempre con el pedido y
    /// <c>Include</c> ni siquiera es una opción. Un modo de fallo menos.
    /// </summary>
    public static OrderResponse From(Order order) => new()
    {
        Id = order.Id,
        CustomerEmail = order.CustomerEmail,
        Status = order.Status.ToString(),
        CreatedAt = order.CreatedAt,
        Total = order.Total,
        Items = order.Items.Select(OrderItemResponse.From).ToList(),
    };
}
