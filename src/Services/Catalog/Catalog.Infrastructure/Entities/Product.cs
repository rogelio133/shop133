using System.Diagnostics.CodeAnalysis;

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
        Apply(sku, name, description, price, stock, imageUrl);
    }

    /// <summary>
    /// Reemplaza el estado completo del producto — la vía de mutación que
    /// <c>PUT /products/{id}</c> necesita (1.3). 1.1 la dejó deliberadamente sin
    /// escribir para no inventarle la firma antes de tener el caso de uso.
    ///
    /// Es un reemplazo total y no una serie de setters por campo: el verbo es
    /// PUT, que por definición manda el recurso entero. Un PATCH por campos
    /// necesitaría otra firma, y no está en el roadmap.
    ///
    /// El <see cref="Sku"/> sí se puede cambiar; el <see cref="Id"/> no. Es la
    /// tabla de la decisión 9 de docs/fase_1_1.md: el código de negocio se
    /// corrige y se renumera, la clave sustituta no cambia nunca. Cambiarlo
    /// puede chocar con el índice único de 1.2, así que el PUT devuelve 409
    /// igual que el POST.
    /// </summary>
    public void Update(string sku, string name, string description, decimal price, int stock, string? imageUrl = null)
    {
        Apply(sku, name, description, price, stock, imageUrl);
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
    /// Valida y asigna el estado completo. Lo comparten el constructor público
    /// y <see cref="Update"/>, que hacen exactamente lo mismo: uno sobre una
    /// entidad recién nacida y otro sobre una que EF ya rastrea.
    ///
    /// **Valida todo en locales y asigna al final, en bloque.** No es estilo: si
    /// se asignara sobre la marcha, un precio inválido dejaría el producto con
    /// el Sku y el Name nuevos y el precio viejo. En un constructor daría igual
    /// (el objeto se descarta), pero en <see cref="Update"/> esa entidad medio
    /// mutada está en el ChangeTracker y el siguiente SaveChanges la escribiría.
    ///
    /// El <c>[MemberNotNull]</c> es lo que permite que el constructor público
    /// delegue aquí: sin él el compilador no ve que las tres propiedades no
    /// anulables quedan inicializadas y avisa con CS8618.
    /// </summary>
    [MemberNotNull(nameof(Sku), nameof(Name), nameof(Description))]
    private void Apply(string sku, string name, string description, decimal price, int stock, string? imageUrl)
    {
        // ToUpperInvariant antes de validar, no después: lo que tiene que caber
        // en SkuMaxLength es el valor que se persiste, no el que llegó.
        //
        // CORRECCIÓN (1.3): 1.1 justificaba este orden diciendo que pasar a
        // mayúsculas puede alargar la cadena (ß -> SS). Es falso en .NET.
        // ToUpperInvariant usa *simple case mapping*, que es 1:1 — recorridos
        // los 63.488 caracteres del BMP, ninguno cambia de longitud al pasarlo
        // por ToUpperInvariant, ß incluido. El orden se mantiene porque sigue
        // siendo el correcto (se valida lo que se guarda), pero ya no descansa
        // sobre un caso que no existe.
        //
        // La guarda de null va suelta y duplica la que hace Validated: sin ella
        // sku.ToUpperInvariant() reventaría con NullReferenceException antes de
        // llegar allí, y 1.3 necesita el paramName para devolver un 400 que
        // nombre el campo.
        ArgumentException.ThrowIfNullOrWhiteSpace(sku, nameof(sku));

        var validatedSku = Validated(sku.ToUpperInvariant(), SkuMaxLength, nameof(sku));
        var validatedName = Validated(name, NameMaxLength, nameof(name));
        var validatedDescription = Validated(description, DescriptionMaxLength, nameof(description));
        var validatedImageUrl = imageUrl is null ? null : Validated(imageUrl, ImageUrlMaxLength, nameof(imageUrl));

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(price);
        ArgumentOutOfRangeException.ThrowIfNegative(stock);

        Sku = validatedSku;
        Name = validatedName;
        Description = validatedDescription;
        ImageUrl = validatedImageUrl;
        Price = price;
        Stock = stock;
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
