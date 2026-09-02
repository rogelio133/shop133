using Microsoft.EntityFrameworkCore;

using Orders.Domain.Entities;
using Orders.Infrastructure.Entities;
using Orders.Infrastructure.Persistence.Configurations;

namespace Orders.Infrastructure.Persistence;

/// <summary>
/// La única puerta a <c>OrdersDb</c>. Mismo papel que <c>CatalogDbContext</c> en
/// Catalog: la regla 1 de CLAUDE.md dice que ningún servicio abre una conexión a
/// la base de otro, y desde 0.4 lo aplica el motor — este contexto se conecta con
/// <c>orders_user</c>, que no tiene permiso sobre CatalogDb, InventoryDb ni
/// PaymentsDb. Si algún día alguien intenta leer productos desde aquí, SQL Server
/// responde <c>Msg 916</c> antes que ninguna revisión de código.
///
/// Vive en Orders.Infrastructure y no en Orders.Domain ni en Orders.API: la
/// flecha va .API → .Infrastructure → .Domain (regla 5), y el test
/// <c>DbContextFiles_LiveOnlyIn_OwningServiceInfrastructure</c> falla si algún
/// <c>*DbContext.cs</c> aparece fuera del .Infrastructure de su servicio. Las
/// entidades, en cambio, sí están en Orders.Domain (2.1) — la persistencia mira
/// al dominio, nunca al revés.
///
/// No aplica migraciones al arrancar. Se hace a mano con
/// <c>dotnet ef database update</c>; ver la sección Commands de CLAUDE.md.
///
/// El estado de la **saga** (4.5) no vive aquí todavía: MassTransit persiste su
/// propia instancia, con su tabla y su token de concurrencia. Que acabe en este
/// mismo DbContext o en uno aparte se decide en ese punto.
/// </summary>
public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    // Sin DbSet<OrderItem> a propósito. Es un tipo *owned* (2.2): no tiene
    // identidad fuera de su pedido, así que no se consulta por su cuenta. EF ni
    // siquiera lo permitiría — un owned type solo se alcanza desde su dueño, que
    // es exactamente la garantía que se buscaba al elegir OwnsMany.

    /// <summary>
    /// Los mensajes ya procesados, una fila por (MessageId, consumer). Es la
    /// idempotencia de transporte de 3.6, que llega a Orders en 4.3 con los dos
    /// primeros consumers del servicio.
    ///
    /// Es la única tabla de aquí que **no es de negocio**: Orders gestiona
    /// pedidos, no mensajes. Vive en esta base y no en una compartida porque la
    /// regla 1 no admite una base transversal, y —lo que de verdad decide— porque
    /// marcar el mensaje y mover el <c>Order.Status</c> tienen que caber en la
    /// misma transacción, algo que dos bases no pueden dar sin transacción
    /// distribuida.
    /// </summary>
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ApplyConfiguration explícito, no ApplyConfigurationsFromAssembly:
        // mismo criterio que Catalog (decisión 2 de docs/fase_1_2.md). Una línea
        // por entidad, y este método es la lista de lo que hay.
        //
        // OrderItem no aparece: se configura dentro de OrderConfiguration, que es
        // como se configuran los tipos owned — desde el builder de su dueño.
        modelBuilder.ApplyConfiguration(new OrderConfiguration());
        modelBuilder.ApplyConfiguration(new ProcessedMessageConfiguration());

        base.OnModelCreating(modelBuilder);
    }
}
