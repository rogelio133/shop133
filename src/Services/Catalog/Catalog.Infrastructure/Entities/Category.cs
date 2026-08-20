namespace Catalog.Infrastructure.Entities;

/// <summary>
/// Una categoría del catálogo: el grupo al que pertenece un <see cref="Product"/>.
///
/// Es un **catálogo en base de datos**, no un <c>enum</c>. La diferencia importa
/// y es la decisión que define este punto: un enum vive en el ensamblado, así
/// que añadir una categoría obliga a recompilar y desplegar Catalog.API, y el
/// nombre que ve el usuario queda atrapado dentro del código. Una tabla se
/// consulta, se ordena y se amplía con un INSERT — y el día que la Fase 6
/// necesite pintar un menú de categorías, ya hay de dónde leerlo.
///
/// El precio de esa elección es una FK y un viaje extra a la base al dar de
/// alta un producto (ver <c>ProductsController</c>): la comprobación de que la
/// categoría existe deja de hacerla el compilador y pasa a hacerla el motor.
///
/// Sigue el mismo estilo que <see cref="Product"/>: clase mutable con setters
/// privados y constructor que valida, no un record inmutable como los tipos de
/// Shop133.Contracts.
/// </summary>
public sealed class Category
{
    /// <summary>
    /// Una sola fuente para la longitud, igual que las constantes de
    /// <see cref="Product"/>: la configuración de EF la traduce a
    /// <c>nvarchar(100)</c> y el DTO de salida no necesita repetirla.
    ///
    /// 100 y no 200 como <see cref="Product.NameMaxLength"/>: el nombre de un
    /// producto describe el producto ("Taza de cerámica Talavera Puebla 350 ml");
    /// el de una categoría es una etiqueta de menú.
    /// </summary>
    public const int NameMaxLength = 100;

    public Category(string name)
    {
        // El Id lo pone SQL Server con IDENTITY, igual que en Product. Que el
        // seed de 1.4 use ids fijos (1..5) no lo contradice: HasData los inserta
        // con SET IDENTITY_INSERT, que es precisamente la vía de excepción.
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var trimmed = name.Trim();

        if (trimmed.Length > NameMaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(name),
                trimmed.Length,
                $"El valor supera el máximo de {NameMaxLength} caracteres.");
        }

        Name = trimmed;
    }

    /// <summary>
    /// Constructor que usa EF Core al materializar filas. Mismo motivo que el de
    /// <see cref="Product"/>: una fila ya persistida no se vuelve a validar.
    /// </summary>
    private Category()
    {
        Name = null!;
    }

    /// <summary>Clave sustituta. La asigna SQL Server con <c>IDENTITY</c>.</summary>
    public int Id { get; private set; }

    /// <summary>
    /// El nombre que se muestra: "Tazas", "Llaveros"…
    ///
    /// **No se normaliza a mayúsculas** a diferencia de <see cref="Product.Sku"/>.
    /// El Sku es un código de máquina que nadie lee en una pantalla; esto es
    /// texto de interfaz, y "TAZAS" en una pestaña del catálogo sería un error
    /// de presentación introducido por la capa de datos. La unicidad no depende
    /// de ello: el índice único de este campo se apoya en la collation por
    /// defecto de SQL Server, que es *case-insensitive* — y aquí eso sí es
    /// aceptable, porque un catálogo de 5 filas fijas no se alimenta de entrada
    /// de usuario. El día que exista un POST /categories, esta nota es la que
    /// hay que releer.
    /// </summary>
    public string Name { get; private set; }
}
