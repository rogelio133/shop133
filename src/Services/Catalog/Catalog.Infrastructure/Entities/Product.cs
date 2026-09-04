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

    public Product(
        string sku,
        string name,
        string description,
        decimal price,
        int stock,
        int categoryId,
        string? imageUrl = null)
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
        //
        // categoryId va antes de imageUrl porque el opcional tiene que quedar
        // último. Es obligatorio: un producto sin categoría no se puede colocar
        // en el catálogo, así que no hay estado válido en el que falte.
        Apply(sku, name, description, price, stock, categoryId, imageUrl);
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
    ///
    /// La <see cref="CategoryId"/> también se puede cambiar (1.4): un producto
    /// mal clasificado se recoloca, y es una operación de catálogo tan normal
    /// como corregirle el precio.
    ///
    /// ── Desde 4.8 lleva la contabilidad del precio anterior ──
    ///
    /// Es el único sitio donde <see cref="PreviousPrice"/> y
    /// <see cref="PriceChangedAt"/> se escriben, y por eso la lógica está aquí y
    /// no en <see cref="Apply"/>: el contrato de aquel método —validar en locales
    /// y asignar en bloque— es justo lo que no se puede debilitar. Poniendo la
    /// contabilidad **después** de que <c>Apply</c> vuelva, hereda su garantía de
    /// todo-o-nada en vez de romperla: un <c>Update</c> que lance no puede dejar
    /// el <c>PreviousPrice</c> movido con el <c>Price</c> viejo, que sería la
    /// mitad de un cambio y volvería auténtico un precio que nunca existió.
    ///
    /// *Descartado* pasarle un flag a <see cref="Apply"/> para distinguir alta de
    /// modificación: le daría un parámetro cuyo significado es "quién me llamó".
    /// Con esta forma el constructor público no cambia y **un producto nuevo nace
    /// con las dos columnas a null gratis**, que es exactamente lo que quiere
    /// decir "este producto nunca ha cambiado de precio".
    /// </summary>
    public void Update(
        string sku,
        string name,
        string description,
        decimal price,
        int stock,
        int categoryId,
        string? imageUrl = null)
    {
        // Se captura ANTES: Apply es quien reemplaza Price.
        var priceBefore = Price;

        Apply(sku, name, description, price, stock, categoryId, imageUrl);

        // decimal != compara valor numérico, no escala, así que un PUT que
        // reenvía 249.0 sobre un 249.00 almacenado **no** es un cambio de precio
        // y no quema la ventana. Es el comportamiento correcto y merece decirse
        // porque 3.3 midió que los ceros finales se pierden en tránsito: una foto
        // de 249.00 llega al consumer como 249.
        if (price != priceBefore)
        {
            PreviousPrice = priceBefore;
            PriceChangedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// ¿Es auténtico este precio unitario? Lo es si coincide con el precio actual,
    /// o si coincide con <see cref="PreviousPrice"/> y el cambio ocurrió dentro de
    /// la ventana de checkout.
    ///
    /// **La pregunta que contesta no es "¿es este el precio de hoy?".** Todo el
    /// <c>///</c> de <c>Shop133.Contracts.OrderLine</c> existe para afirmar que el
    /// precio de un pedido es una foto congelada, y comparar contra el precio
    /// actual rechazaría un pedido legítimo cuyo precio cambió a mitad del
    /// checkout. Lo que se valida es que el precio de la foto sea un precio que
    /// este catálogo **llegó a ofrecer**, y hace poco.
    ///
    /// ── Por qué es un predicado en la entidad y no cuatro cláusulas en el
    ///    consumer ──
    ///
    /// Precedente <c>StockItem.CanReserve</c> de Inventory: un predicado puro que
    /// nunca lanza, cuyo llamante decide qué publicar. Aquí compra algo que aquel
    /// caso no tenía: el código que **escribe** las dos columnas
    /// (<see cref="Update"/>) y el que las **lee** acaban en el mismo archivo, así
    /// que las dos mitades de la ventana no pueden divergir. Escrito en el
    /// consumer, sí podrían.
    ///
    /// No tiene gemelo que lance al estilo de <c>StockItem.Reserve</c>: allí el
    /// par existía porque después de comprobar había que mutar. Aquí no muta nada
    /// — validar un precio es una lectura.
    ///
    /// ── La limitación, que hay que decir en voz alta ──
    ///
    /// Hay **exactamente un paso de historia**. Dos cambios seguidos
    /// (249 → 199 → 179) invalidan una foto legítima de 249 tomada hace dos
    /// minutos: el cliente recibe una cancelación por un pedido correcto.
    ///
    /// *Descartada* una tabla <c>ProductPriceHistory</c> con vigencias, que sería
    /// la respuesta completa: entidad, configuración, migración, 50 filas de seed
    /// y una política de purga, para ensanchar una ventana que el proyecto no
    /// tiene ninguna evidencia de que sea estrecha. Si algún día la hay, es la
    /// forma de arreglarlo.
    ///
    /// Lee <c>DateTimeOffset.UtcNow</c> directamente, con el precedente del
    /// constructor de <c>ProcessedMessage</c>, cuyo <c>///</c> descarta
    /// explícitamente un <c>TimeProvider</c> inyectado. La consecuencia es que un
    /// test no puede fingir que el reloj avanzó — tiene que mover
    /// <see cref="PriceChangedAt"/> en la base.
    /// </summary>
    public bool IsAuthenticPrice(decimal unitPrice, TimeSpan window)
    {
        if (unitPrice == Price)
        {
            return true;
        }

        return PreviousPrice is { } previous
            && PriceChangedAt is { } changedAt
            && unitPrice == previous
            && DateTimeOffset.UtcNow - changedAt <= window;
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
    private void Apply(
        string sku,
        string name,
        string description,
        decimal price,
        int stock,
        int categoryId,
        string? imageUrl)
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

        // Lo único que la entidad puede afirmar sobre la categoría es que un id
        // válido es positivo. Que *exista* es una pregunta sobre otra tabla, y
        // una entidad no consulta la base de datos: la responden la FK del
        // esquema y la comprobación explícita del ProductsController.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(categoryId);

        Sku = validatedSku;
        Name = validatedName;
        Description = validatedDescription;
        ImageUrl = validatedImageUrl;
        Price = price;
        Stock = stock;
        CategoryId = categoryId;
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
    /// El precio que tenía antes del último cambio, o <c>null</c> si nunca ha
    /// cambiado (4.8). Lo escribe <see cref="Update"/> y lo lee
    /// <see cref="IsAuthenticPrice"/>; nadie más lo toca.
    ///
    /// **No es un histórico**: es un solo paso hacia atrás, y solo sirve para
    /// reconocer como auténtica la foto de un pedido que empezó su checkout justo
    /// antes de un cambio de precio. Ver la limitación en
    /// <see cref="IsAuthenticPrice"/>.
    ///
    /// **Deliberadamente no se publica en <c>ProductResponse</c>.** Exponerla le
    /// daría a un cliente la cifra exacta que necesita para forjar una foto que
    /// pase por auténtica, que es lo contrario de para lo que existe 4.8.
    /// </summary>
    public decimal? PreviousPrice { get; private set; }

    /// <summary>
    /// Cuándo cambió el precio por última vez, o <c>null</c> si nunca cambió.
    /// Siempre en UTC. Es la otra mitad de <see cref="PreviousPrice"/>: sin fecha,
    /// el precio anterior sería auténtico para siempre y la ventana no existiría.
    ///
    /// <c>DateTimeOffset</c> y no <c>DateTime</c>, como en todas las marcas de
    /// tiempo del proyecto: mapea a <c>datetimeoffset</c> sin ambigüedad de
    /// <c>Kind</c>.
    ///
    /// Tampoco se publica en <c>ProductResponse</c>, por lo mismo que la anterior
    /// — con las dos, forjar una foto auténtica sería trivial.
    /// </summary>
    public DateTimeOffset? PriceChangedAt { get; private set; }

    /// <summary>
    /// Unidades que el catálogo anuncia. Ver la nota de la clase: no es el
    /// stock reservable, que a partir de 3.4 pertenece a Inventory.API.
    /// </summary>
    public int Stock { get; private set; }

    /// <summary>
    /// Imagen del producto, opcional — un producto sin foto es válido. No se
    /// comprueba que sea una URI absoluta a propósito: el seed de 1.4 puede
    /// usar rutas relativas servidas por el propio frontend. Y las usa: todas
    /// las 50 filas del seed llevan <c>/img/products/&lt;sku&gt;.jpg</c>.
    /// </summary>
    public string? ImageUrl { get; private set; }

    /// <summary>
    /// La categoría a la que pertenece (1.4). Clave foránea contra
    /// <c>Categories</c>, obligatoria y con <c>DeleteBehavior.Restrict</c>.
    /// </summary>
    public int CategoryId { get; private set; }

    /// <summary>
    /// Navegación hacia la categoría. Es **anulable a propósito**, aunque
    /// <see cref="CategoryId"/> sea obligatoria: nula no significa "producto sin
    /// categoría", significa "esta consulta no la cargó". Solo viene rellena si
    /// alguien pidió el <c>Include</c> o si EF hizo *fix-up* porque la categoría
    /// ya estaba rastreada por el mismo contexto.
    ///
    /// La navegación es unidireccional: <see cref="Category"/> no tiene una
    /// colección <c>Products</c>. Nadie la necesita hoy, y una relación con las
    /// dos puntas obliga a mantenerlas sincronizadas a mano en memoria.
    /// </summary>
    public Category? Category { get; private set; }

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
