using System.ComponentModel.DataAnnotations;

using Catalog.Infrastructure.Entities;

namespace Catalog.API.Models;

/// <summary>
/// Cuerpo de <c>POST /products</c>.
///
/// Vive en Catalog.API y no en Shop133.Contracts a propósito: la regla 4 de
/// CLAUDE.md reserva ese proyecto para los mensajes que viajan por RabbitMQ y
/// le prohíbe explícitamente las validation attributes. Esto no es un mensaje,
/// es la forma de una petición HTTP de un solo servicio.
///
/// Tampoco es la entidad. Que hoy coincidan campo a campo es una casualidad del
/// primer día: <see cref="Product"/> es el modelo de persistencia y su forma no
/// puede ser el contrato público, o cada columna nueva se convierte en un
/// cambio de API.
///
/// Las longitudes salen de las constantes de la entidad, nunca de literales.
/// Eran el tercer consumidor previsto para ellas en 1.1 — la guarda del
/// constructor, el nvarchar(n) de 1.2 y esto.
/// </summary>
public sealed record CreateProductRequest
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

    /// <summary>
    /// El rango expresa exactamente el <c>decimal(18,2)</c> de la columna: 16
    /// dígitos enteros más 2 decimales.
    ///
    /// <c>ParseLimitsInInvariantCulture</c> no es adorno. La sobrecarga con
    /// <c>Type</c> recibe los límites como cadenas y, sin esa bandera, los
    /// parsea con <c>CurrentCulture</c> — en una máquina con locale español el
    /// punto de "0.01" no es el separador decimal y el límite sale mal.
    /// </summary>
    [Range(typeof(decimal), "0.01", "9999999999999999.99", ParseLimitsInInvariantCulture = true)]
    public required decimal Price { get; init; }

    /// <summary>
    /// Cero es válido: un producto agotado sigue siendo un producto. Ver la nota
    /// de <see cref="Product.Stock"/> — este número es el que el catálogo
    /// muestra, no el reservable, que desde 3.4 pertenece a InventoryDb.
    /// </summary>
    [Range(0, int.MaxValue)]
    public required int Stock { get; init; }

    /// <summary>
    /// Opcional, como en la entidad. Sin <c>[Url]</c>: 1.1 decidió no exigir URI
    /// absoluta porque el seed de 1.4 puede usar rutas relativas.
    /// </summary>
    [MaxLength(Product.ImageUrlMaxLength)]
    public string? ImageUrl { get; init; }
}
