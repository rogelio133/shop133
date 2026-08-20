using Catalog.Infrastructure.Entities;
using Catalog.Infrastructure.Persistence.Seed;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Catalog.Infrastructure.Persistence.Configurations;

/// <summary>
/// El mapeo de <see cref="Category"/> a la tabla <c>Categories</c>, con el mismo
/// criterio que <see cref="ProductConfiguration"/>: todo declarado a mano aunque
/// coincida con las convenciones de EF, porque este archivo es el sitio donde se
/// lee el esquema y una convención implícita no se lee en ninguna parte.
/// </summary>
internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("Categories");

        builder.HasKey(category => category.Id);

        builder.Property(category => category.Id)
            .ValueGeneratedOnAdd();

        builder.Property(category => category.Name)
            .IsRequired()
            .HasMaxLength(Category.NameMaxLength);

        // Mismo argumento que el índice único sobre Product.Sku: la entidad no
        // puede responder una pregunta sobre el conjunto entero de filas.
        builder.HasIndex(category => category.Name)
            .IsUnique();

        // Las 5 categorías del catálogo (1.4), con ids fijos. Va en una
        // migración aparte de la que creó esta tabla: ver CatalogSeedData.
        builder.HasData(CatalogSeedData.Categories);
    }
}
