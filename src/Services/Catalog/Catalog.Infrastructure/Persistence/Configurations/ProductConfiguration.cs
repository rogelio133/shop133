using Catalog.Infrastructure.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Catalog.Infrastructure.Persistence.Configurations;

/// <summary>
/// El mapeo de <see cref="Product"/> a la tabla <c>Products</c>.
///
/// Está en una clase aparte y no con Data Annotations sobre la entidad porque
/// <see cref="Product"/> no debe saber que existe EF Core — es el mismo
/// argumento que la regla 4 de CLAUDE.md aplica a Shop133.Contracts. Si mañana
/// el catálogo se persistiera de otra forma, lo que se tira es este archivo, no
/// la entidad.
///
/// Todo lo que hay aquí está declarado a mano aunque parte coincida con las
/// convenciones de EF: este archivo es el sitio donde se lee el esquema, y una
/// convención implícita no se lee en ninguna parte.
/// </summary>
internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products");

        builder.HasKey(product => product.Id);

        // int IDENTITY, decisión 2 de docs/fase_1_1.md: el id de un producto lo
        // acuña SQL Server en el INSERT, no el productor. La PK va clustered
        // (default de SQL Server) y aquí eso es lo correcto: un IDENTITY es
        // creciente, que es justo lo que un índice clustered quiere.
        builder.Property(product => product.Id)
            .ValueGeneratedOnAdd();

        // Las cuatro longitudes salen de las constantes de la entidad, nunca de
        // literales: son la única fuente y 1.3 las reutiliza al validar el DTO
        // de entrada. Sin HasMaxLength, EF generaría nvarchar(max) — que además
        // de desperdiciar espacio no se puede indexar, y Sku necesita índice.
        builder.Property(product => product.Sku)
            .IsRequired()
            .HasMaxLength(Product.SkuMaxLength);

        // El entregable clave de 1.2. La entidad normaliza el Sku a mayúsculas
        // pero no puede garantizar que no haya dos productos con el mismo: eso
        // es una pregunta sobre el conjunto entero de filas, y solo la base
        // puede responderla. Ver la decisión 9 de docs/fase_1_1.md.
        builder.HasIndex(product => product.Sku)
            .IsUnique();

        builder.Property(product => product.Name)
            .IsRequired()
            .HasMaxLength(Product.NameMaxLength);

        builder.Property(product => product.Description)
            .IsRequired()
            .HasMaxLength(Product.DescriptionMaxLength);

        // Opcional: un producto sin foto es válido.
        builder.Property(product => product.ImageUrl)
            .IsRequired(false)
            .HasMaxLength(Product.ImageUrlMaxLength);

        // decimal(18,2) ya es el default del provider de SQL Server, pero se
        // declara: dejarlo implícito hace que un cambio de provider mueva el
        // tipo de una columna de dinero sin que nadie lo note.
        builder.Property(product => product.Price)
            .HasPrecision(18, 2);

        builder.Property(product => product.Stock)
            .IsRequired();
    }
}
