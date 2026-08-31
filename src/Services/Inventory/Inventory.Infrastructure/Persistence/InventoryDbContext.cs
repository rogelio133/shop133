using Microsoft.EntityFrameworkCore;

using Inventory.Infrastructure.Entities;
using Inventory.Infrastructure.Persistence.Configurations;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// La sesión con <c>InventoryDb</c>, la tercera base de datos del sistema.
///
/// Se conecta con <c>inventory_user</c>, que tiene <c>db_owner</c> sobre
/// <c>InventoryDb</c> y **ningún permiso** sobre CatalogDb, OrdersDb ni
/// PaymentsDb: la regla 1 de CLAUDE.md aplicada por el motor desde 0.4, no por
/// convención. Un intento de leer el stock "de verdad" en CatalogDb no falla en
/// revisión de código, falla con <c>Msg 916</c>.
///
/// Está en Inventory.Infrastructure y no en Inventory.API porque lo comprueba el
/// test <c>DbContextFiles_LiveOnlyIn_OwningServiceInfrastructure</c>, que exige
/// la ruta <c>src/Services/&lt;S&gt;/&lt;S&gt;.Infrastructure/…</c>.
/// </summary>
public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    /// <summary>
    /// El stock reservable, una fila por producto. La clave es el
    /// <c>ProductId</c> de Catalog: un producto sin fila aquí es, para efectos
    /// de una reserva, un producto que no existe.
    /// </summary>
    public DbSet<StockItem> StockItems => Set<StockItem>();

    /// <summary>
    /// Las reservas vivas, una fila por pedido. Es lo que permitirá a 4.4 soltar
    /// el stock con solo el <c>OrderId</c>.
    /// </summary>
    public DbSet<StockReservation> StockReservations => Set<StockReservation>();

    // Sin DbSet<StockReservationLine> a propósito: es un tipo *owned* (ver
    // StockReservationConfiguration), igual que OrderItem en Orders. EF impide
    // consultarlo suelto y lo carga siempre con su reserva, sin Include — un
    // olvido menos en 4.4.

    /// <summary>
    /// Los mensajes ya procesados, una fila por (MessageId, consumer). Es la
    /// idempotencia de transporte de 3.6, la que exige la regla 6 de CLAUDE.md.
    ///
    /// Es la única tabla de aquí que **no es de negocio**: Inventory no gestiona
    /// mensajes, los recibe. Vive en esta base y no en una compartida porque la
    /// regla 1 no admite una base transversal, y porque marcar el mensaje y hacer
    /// el trabajo tienen que caber en la misma transacción — algo que dos bases
    /// no pueden dar sin transacción distribuida.
    /// </summary>
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ApplyConfiguration explícito y no ApplyConfigurationsFromAssembly, por
        // el mismo motivo que en Catalog y Orders: el escaneo por reflexión hace
        // que añadir una configuración sea invisible desde aquí, y que
        // renombrarla mal la deje fuera del modelo sin que nada avise.
        modelBuilder.ApplyConfiguration(new StockItemConfiguration());
        modelBuilder.ApplyConfiguration(new StockReservationConfiguration());
        modelBuilder.ApplyConfiguration(new ProcessedMessageConfiguration());

        base.OnModelCreating(modelBuilder);
    }
}
