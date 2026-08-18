namespace Catalog.Infrastructure.Entities;

/// <summary>
/// Un producto del catálogo: lo que Catalog.API expone y lo que se persiste en
/// CatalogDb a partir de 1.2.
///
/// Es una entidad, no un mensaje: clase mutable con setters privados, no un
/// record inmutable como los tipos de Shop133.Contracts. La diferencia importa —
/// un mensaje es una foto que ya viajó y no cambia; una entidad la rastrea EF
/// Core y su precio y su stock cambian a lo largo de su vida.
///
/// Sobre <see cref="Stock"/>: es el stock que el catálogo *muestra*, no el que
/// se reserva. Desde la Fase 3.4 el stock reservable vive en InventoryDb y lo
/// gestiona Inventory.API con ReserveStock/ReleaseStock. Nadie descuenta de
/// aquí al crear un pedido — hacerlo pondría la reserva en el servicio
/// equivocado y dejaría dos fuentes de verdad para la misma cantidad.
/// </summary>
public sealed class Product
{
    /// <summary>
    /// Longitudes máximas de los campos de texto. Viven aquí para que haya una
    /// sola fuente: 1.2 las traduce a <c>nvarchar(n)</c> en la configuración de
    /// EF Core y 1.3 las reutiliza al validar el DTO de entrada. Sin ellas, EF
    /// generaría <c>nvarchar(max)</c> para las cuatro columnas — que además de
    /// desperdiciar espacio impide indexar <see cref="Sku"/>.
    /// </summary>
    public const int SkuMaxLength = 50;

    public const int NameMaxLength = 200;

    public const int DescriptionMaxLength = 2000;

    public const int ImageUrlMaxLength = 500;

    public Product(string sku, string name, string description, decimal price, int stock, string? imageUrl = null)
    {
        // El Id no se asigna aquí: lo pone SQL Server con IDENTITY en el INSERT
        // (1.2). Hasta que se guarda vale 0.
        //
        // Es un int y no un Guid, revirtiendo lo que dejó escrito la decisión 4
        // de docs/fase_0_3.md — ver la decisión 2 de docs/fase_1_1.md. El
        // argumento de aquel punto ("el productor acuña el id sin consultar a
        // nadie") solo vale para OrderId, que es la clave de correlación de la
        // saga y tiene que existir antes de tocar la base. Un producto lo crea
        // Catalog con un POST síncrono y su propia base es el único escritor,
        // así que no hay nada que adelantar.

        // ToUpperInvariant antes de validar, no después: puede alargar la
        // cadena en Unicode (ß -> SS), y lo que tiene que caber en
        // SkuMaxLength es el valor que se persiste. La guarda de null va suelta
        // porque sku.ToUpperInvariant() reventaría antes de llegar a Validated.
        ArgumentException.ThrowIfNullOrWhiteSpace(sku, nameof(sku));
        Sku = Validated(sku.ToUpperInvariant(), SkuMaxLength, nameof(sku));

        Name = Validated(name, NameMaxLength, nameof(name));
        Description = Validated(description, DescriptionMaxLength, nameof(description));
        ImageUrl = imageUrl is null ? null : Validated(imageUrl, ImageUrlMaxLength, nameof(imageUrl));

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(price);
        ArgumentOutOfRangeException.ThrowIfNegative(stock);

        Price = price;
        Stock = stock;
    }

    /// <summary>
    /// Constructor que usa EF Core al materializar filas (1.2). Los <c>null!</c>
    /// son deliberados: EF asigna las propiedades por reflexión justo después de
    /// llamar a este constructor, pero el compilador no lo sabe y avisaría de
    /// que Sku, Name y Description quedan sin inicializar.
    ///
    /// Existe para que las guardas del constructor público no se ejecuten al
    /// leer de la base de datos. Una fila que ya está persistida no se valida
    /// otra vez: si el dato es inválido, la excepción tiene que salir al
    /// escribirlo, no al leerlo tres meses después.
    /// </summary>
    private Product()
    {
        Sku = null!;
        Name = null!;
        Description = null!;
    }

    /// <summary>
    /// Clave sustituta. La asigna SQL Server con <c>IDENTITY</c> en el INSERT
    /// (1.2), así que vale 0 hasta que la entidad se guarda. Es el
    /// identificador que viaja en <c>OrderLine.ProductId</c> entre servicios.
    /// </summary>
    public int Id { get; private set; }

    /// <summary>
    /// Código de negocio del producto — el que se imprime en una etiqueta y el
    /// que usa un humano para referirse a él. Distinto del <see cref="Id"/>:
    /// este lo elige quien da de alta el producto, aquel lo genera la base.
    ///
    /// Se guarda normalizado en mayúsculas para que "lap-14" y "LAP-14" sean el
    /// mismo producto. La entidad **no** puede garantizar que sea único: eso lo
    /// impone un índice único en la configuración de EF Core de 1.2.
    /// </summary>
    public string Sku { get; private set; }

    public string Name { get; private set; }

    public string Description { get; private set; }

    /// <summary>Precio de venta. Siempre mayor que cero.</summary>
    public decimal Price { get; private set; }

    /// <summary>
    /// Unidades que el catálogo anuncia. Ver la nota de la clase: no es el
    /// stock reservable, que a partir de 3.4 pertenece a Inventory.API.
    /// </summary>
    public int Stock { get; private set; }

    /// <summary>
    /// Imagen del producto, opcional — un producto sin foto es válido. No se
    /// comprueba que sea una URI absoluta a propósito: el seed de 1.4 puede
    /// usar rutas relativas servidas por el propio frontend.
    /// </summary>
    public string? ImageUrl { get; private set; }

    private static string Validated(string value, int maxLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        var trimmed = value.Trim();

        if (trimmed.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                trimmed.Length,
                $"El valor supera el máximo de {maxLength} caracteres.");
        }

        return trimmed;
    }
}
