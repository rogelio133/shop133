using Microsoft.EntityFrameworkCore;

using Payments.Infrastructure.Entities;
using Payments.Infrastructure.Persistence.Configurations;

namespace Payments.Infrastructure.Persistence;

/// <summary>
/// La sesión con <c>PaymentsDb</c>, la cuarta y última base de datos del sistema
/// — creada vacía en 0.4 y sin usar hasta 3.5.
///
/// Se conecta con <c>payments_user</c>, que tiene <c>db_owner</c> sobre
/// <c>PaymentsDb</c> y **ningún permiso** sobre CatalogDb, OrdersDb ni
/// InventoryDb: la regla 1 de CLAUDE.md aplicada por el motor desde 0.4, no por
/// convención. Es lo que hace que "leer el total del pedido en OrdersDb" no sea
/// una tentación sino un <c>Msg 916</c> — y por eso el importe tiene que viajar
/// dentro de <c>StockReserved.Amount</c>.
///
/// Está en Payments.Infrastructure y no en Payments.API porque lo comprueba el
/// test <c>DbContextFiles_LiveOnlyIn_OwningServiceInfrastructure</c>, que exige
/// la ruta <c>src/Services/&lt;S&gt;/&lt;S&gt;.Infrastructure/…</c>.
///
/// No migra al arrancar. Las migraciones se aplican a mano con
/// <c>dotnet ef database update</c>, igual que en los otros tres servicios.
/// </summary>
public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ApplyConfiguration explícito y no ApplyConfigurationsFromAssembly, por
        // el mismo motivo que en Catalog, Orders e Inventory: el escaneo por
        // reflexión hace que añadir una configuración sea invisible desde aquí, y
        // que renombrarla mal la deje fuera del modelo sin que nada avise.
        modelBuilder.ApplyConfiguration(new PaymentConfiguration());

        base.OnModelCreating(modelBuilder);
    }
}
