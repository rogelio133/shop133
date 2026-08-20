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

    /// <summary>
    /// El catálogo de categorías (1.4). Es de solo lectura en la práctica —
    /// las 5 filas las pone el seed y no hay endpoint de escritura— pero se
    /// expone como <see cref="DbSet{TEntity}"/> normal: el
    /// <c>ProductsController</c> necesita consultarlo para validar el
    /// <c>CategoryId</c> que llega en un POST o un PUT.
    /// </summary>
    public DbSet<Category> Categories => Set<Category>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ApplyConfiguration explícito, no ApplyConfigurationsFromAssembly: con
        // dos entidades el escaneo por reflexión sigue sin ahorrar nada y
        // esconde qué se está registrando. Se cambia el día que haya media
        // docena.
        modelBuilder.ApplyConfiguration(new CategoryConfiguration());
        modelBuilder.ApplyConfiguration(new ProductConfiguration());

        base.OnModelCreating(modelBuilder);
    }
}
