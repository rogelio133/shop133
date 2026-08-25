using System.ComponentModel.DataAnnotations;

using Orders.Domain.Entities;

namespace Orders.API.Models;

/// <summary>
/// Cuerpo de <c>POST /orders</c>.
///
/// Vive en Orders.API y no en Shop133.Contracts, igual que
/// <c>CreateProductRequest</c> vive en Catalog.API: la regla 4 reserva ese
/// proyecto para los mensajes que viajan por RabbitMQ y le prohíbe las
/// validation attributes. Esto no es un mensaje, es la forma de una petición HTTP.
///
/// Tampoco es <see cref="Order"/>: la entidad acuña su propio <c>Id</c>, fija el
/// estado y el sello de tiempo, y sus líneas llevan tres campos congelados que el
/// cliente no manda. El parecido entre ambos tipos es mucho menor que en Catalog
/// — aquí el DTO es de verdad una vista recortada.
/// </summary>
public sealed record CreateOrderRequest
{
    /// <summary>
    /// A quién se le notificará el desenlace (viaja dentro de
    /// <c>OrderConfirmed</c>/<c>OrderCancelled</c> en la Fase 4).
    ///
    /// **Aquí está el <c>[EmailAddress]</c> que 2.1 dejó fuera de la entidad**, y
    /// esa separación es la decisión: la entidad valida lo que sabe (no vacío,
    /// longitud) y el DTO valida la *forma de la entrada*. Un pedido que ya está
    /// en la base no se vuelve a validar contra la moda actual de expresiones
    /// regulares de correo, y la saga no debería reventar porque un dato histórico
    /// no le guste al validador de hoy.
    ///
    /// La longitud sale de la constante de la entidad, nunca del literal 320:
    /// era el tercer consumidor previsto para ella en 2.1 — la guarda del
    /// constructor, el <c>nvarchar(n)</c> de 2.2 y esto.
    ///
    /// El <c>[EmailAddress]</c> de DataAnnotations es deliberadamente laxo (busca
    /// una arroba con algo a cada lado, sin comprobar el dominio). Es lo correcto:
    /// la única validación real de un correo es mandarle un mensaje, y eso es
    /// trabajo de Notifications.API en 4.6.
    /// </summary>
    [Required]
    [EmailAddress]
    [MaxLength(Order.CustomerEmailMaxLength)]
    public required string CustomerEmail { get; init; }

    /// <summary>
    /// Las líneas. <c>[MinLength(1)]</c> devuelve el 400 antes de que el
    /// constructor de <see cref="Order"/> lance su "un pedido necesita al menos
    /// una línea": la misma invariante, comprobada dos veces a propósito, porque
    /// la entidad tiene que sostenerla venga de donde venga la llamada.
    ///
    /// **El tope de 50 sí tiene un motivo concreto en este punto**: cada línea
    /// distinta cuesta una ida y vuelta HTTP a Catalog. El tamaño del cuerpo es,
    /// literalmente, el coste del acoplamiento síncrono — un pedido de 200 líneas
    /// serían 200 peticiones secuenciales antes de poder responder. En la Fase 3
    /// ese número deja de importar, porque publicar <c>OrderCreated</c> cuesta lo
    /// mismo con 1 línea que con 200; cuando llegue, este tope se puede releer.
    ///
    /// Las líneas repetidas **no** son un error de validación: el controller las
    /// agrupa sumando cantidades antes de construir el pedido.
    /// </summary>
    [Required]
    [MinLength(1)]
    [MaxLength(50)]
    public required IReadOnlyList<CreateOrderItemRequest> Items { get; init; }
}
