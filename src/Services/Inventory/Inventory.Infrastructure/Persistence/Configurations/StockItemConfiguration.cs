using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Inventory.Infrastructure.Entities;
using Inventory.Infrastructure.Persistence.Seed;

namespace Inventory.Infrastructure.Persistence.Configurations;

/// <summary>
/// El mapeo de <see cref="StockItem"/> a la tabla <c>StockItems</c>.
///
/// En una clase aparte y no con Data Annotations sobre la entidad, igual que en
/// Catalog y Orders. Aquí es una decisión de estilo y no una imposibilidad
/// técnica (las entidades de Inventory viven en el mismo proyecto que EF, como
/// las de Catalog), pero se mantiene: la entidad describe reglas de negocio y
/// esta clase describe columnas, y mezclarlas hace que ninguna de las dos se lea
/// entera en un sitio.
///
/// Todo está declarado a mano aunque parte coincida con las convenciones de EF:
/// este archivo es el sitio donde se lee el esquema, y una convención implícita
/// no se lee en ninguna parte.
/// </summary>
internal sealed class StockItemConfiguration : IEntityTypeConfiguration<StockItem>
{
    public void Configure(EntityTypeBuilder<StockItem> builder)
    {
        builder.ToTable("StockItems");

        builder.HasKey(item => item.ProductId);

        // ── La línea más importante del archivo ──
        //
        // ProductId es la clave que acuñó el IDENTITY de CatalogDb; aquí solo se
        // copia. Sin ValueGeneratedNever(), la convención de EF para una PK int
        // es ValueGeneratedOnAdd, o sea un IDENTITY propio — y entonces insertar
        // el StockItem del producto 7 crearía la fila 1, con el número que le
        // tocara al contador de esta tabla. El fallo no sería un error: sería
        // stock apuntando al producto equivocado, en silencio.
        //
        // Tiene un segundo efecto, en el seed: al no haber IDENTITY, EF no
        // genera SET IDENTITY_INSERT alrededor de los InsertData, al contrario
        // que en Catalog.
        builder.Property(item => item.ProductId)
            .ValueGeneratedNever();

        // No hay clave foránea a Products y no la va a haber: el producto vive
        // en CatalogDb, SQL Server no soporta FK entre bases e inventory_user ni
        // siquiera puede abrirla. Es un puntero débil, como OrderItem.ProductId.
        // Que apunte a un producto ya borrado es un resultado aceptado.

        builder.Property(item => item.QuantityOnHand)
            .IsRequired();

        builder.Property(item => item.QuantityReserved)
            .IsRequired();

        // Calculada, no persistida: una sola fuente de verdad. Sin Ignore(), EF
        // ve una propiedad int pública y le busca columna — y como no tiene
        // setter ni campo de respaldo, el modelo ni siquiera llega a
        // construirse. Mismo caso que Order.Total en 2.2.
        builder.Ignore(item => item.QuantityAvailable);

        // Sin CHECK constraint que exija QuantityReserved <= QuantityOnHand, y
        // no por descuido: la invariante ya la sostiene StockItem.Reserve(), y
        // un CHECK convertiría un error de programación en un DbUpdateException
        // dentro del consumer, o sea en un mensaje en la error queue en lugar de
        // en una excepción que nombra el producto. Se reconsidera si alguna vez
        // algo escribe en esta tabla sin pasar por la entidad.

        // Sin índices más allá de la PK. Ninguna consulta los necesita: 3.4 lee
        // por ProductId, que es la clave. Un índice se añade cuando existe la
        // consulta que lo justifica — mismo criterio que 2.2, y el contrario que
        // el índice único de Sku en 1.2, que era una invariante y no un
        // rendimiento.

        // El seed de 3.4: 50 filas, una por producto sembrado en 1.4. Ver
        // InventorySeedData para las tres consecuencias de HasData.
        //
        // Está en su propia migración (SeedStockItems) y no dentro de
        // InitialCreate, igual que 1.4 separó SeedSouvenirCatalog de
        // AddProductCategories: así se puede revertir el seed sin desmontar las
        // tablas. Para conseguir la separación hubo que comentar esta línea,
        // generar InitialCreate y volver a ponerla — EF compara el modelo actual
        // contra la última migración, no sabe de intenciones.
        builder.HasData(InventorySeedData.StockItems);
    }
}
