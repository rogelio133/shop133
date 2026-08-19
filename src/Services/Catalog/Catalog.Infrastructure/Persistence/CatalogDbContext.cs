using Catalog.Infrastructure.Entities;
using Catalog.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;

namespace Catalog.Infrastructure.Persistence;

/// <summary>
/// La única puerta a <c>CatalogDb</c>. Es el DbContext de Catalog y de nadie
/// más: la regla 1 de CLAUDE.md dice que ningún servicio abre una conexión a la
/// base de otro, y desde 0.4 el motor la aplica — este contexto se conecta con
/// <c>catalog_user</c>, que no tiene permiso sobre OrdersDb, InventoryDb ni
/// PaymentsDb.
///
/// Vive en Catalog.Infrastructure y no en Catalog.API a propósito: la flecha va
/// .API → .Infrastructure (regla 5), y el test
/// <c>DbContextFiles_LiveOnlyIn_OwningServiceInfrastructure</c> falla si algún
/// <c>*DbContext.cs</c> aparece fuera del .Infrastructure de su servicio.
///
/// No aplica migraciones al arrancar. Se hace a mano con
/// <c>dotnet ef database update</c>; ver la sección Commands de CLAUDE.md.
/// </summary>
public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ApplyConfiguration explícito, no ApplyConfigurationsFromAssembly: con
        // una sola entidad el escaneo por reflexión no ahorra nada y esconde
        // qué se está registrando. Se cambia el día que haya media docena.
        modelBuilder.ApplyConfiguration(new ProductConfiguration());

        base.OnModelCreating(modelBuilder);
    }
}
