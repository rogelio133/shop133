using MassTransit;

using Microsoft.EntityFrameworkCore;

using Orders.Domain.Entities;
using Orders.Domain.Sagas;
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
/// ── Lo que 4.5 mete aquí, y por qué en ESTE contexto y no en uno aparte ──
///
/// Desde 4.5 esta clase deja de contener solo lo que Orders escribe a mano:
/// entran la instancia de la saga (<c>OrderStates</c>) y las tres tablas del
/// outbox transaccional de MassTransit. 2.2 dejó la pregunta abierta —"habrá que
/// decidir si comparte <c>OrdersDbContext</c> o tiene el suyo"— y la respuesta es
/// **compartirlo**, sin ninguna duda razonable:
///
/// Un <c>SagaDbContext</c> aparte anularía el punto entero. Lo que hace valioso al
/// outbox es que el mensaje se escribe en la MISMA transacción que el trabajo, y
/// una transacción es de una conexión y un <c>SaveChangesAsync</c>. Con dos
/// contextos volvería exactamente la doble escritura que este punto cierra, solo
/// que dentro de la misma base y por tanto más difícil de ver.
///
/// El precio, dicho en voz alta: este contexto ya tiene cinco tablas y **solo dos
/// son de negocio**. Las otras tres existen porque la entrega de mensajes es "al
/// menos una vez" y porque no hay transacción distribuida entre SQL Server y
/// RabbitMQ. Es el coste de la mensajería fiable, hecho visible en un
/// <c>OnModelCreating</c> en vez de escondido en una librería.
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

    /// <summary>
    /// La instancia de la saga, una fila por pedido (4.5). Su PK **es** el
    /// <c>OrderId</c>, igual que la de <c>Orders</c>, y entre las dos tablas no
    /// hay ninguna FK: son el pedido y el proceso que lo coordina. Ver
    /// <c>OrderStateConfiguration</c>.
    ///
    /// El <c>DbSet</c> no lo necesita MassTransit —el repositorio resuelve la
    /// entidad por el modelo— y se declara igual, por el mismo motivo que el
    /// <c>ApplyConfiguration</c> explícito de abajo: este archivo es la lista de
    /// lo que hay en <c>OrdersDb</c>, y una tabla que solo apareciera al mirar la
    /// migración no estaría en ninguna lista.
    /// </summary>
    public DbSet<OrderState> OrderStates => Set<OrderState>();

    // Sin DbSet para InboxState, OutboxMessage y OutboxState, al revés que la de
    // arriba, y la asimetría es la decisión: esas tres son estructuras internas
    // de MassTransit y nadie de este repositorio las consulta por código. Se
    // mapean (abajo) porque tienen que existir en la base; no se exponen porque
    // exponerlas invitaría a leerlas desde un controller.

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
        modelBuilder.ApplyConfiguration(new OrderStateConfiguration());

        // ── Las tres tablas del outbox transaccional (4.5) ──
        //
        // Aquí sí se delega el esquema a MassTransit, al contrario que con
        // OrderState, y la diferencia es de propiedad: OrderState es un tipo de
        // este repositorio y estas tres son estructuras internas de la librería,
        // que las lee y las escribe ella. Escribir a mano su mapeo sería fijar un
        // esquema que no decidimos nosotros y que una actualización menor podría
        // necesitar cambiar.
        //
        // Qué hace cada una, porque los nombres no lo dicen:
        //
        //   OutboxMessage — los mensajes ya "publicados" por el código pero
        //     todavía no entregados al broker. Es la fila que entra en la MISMA
        //     transacción que el pedido (OrdersController) o que el cambio de
        //     estado de la saga. Cierra la doble escritura de la decisión 3 de
        //     docs/fase_3_3.md.
        //   OutboxState  — el puntero de entrega: por dónde va el servicio de
        //     fondo que vacía OutboxMessage hacia RabbitMQ.
        //   InboxState   — el INBOX: un mensaje ya consumido, por (MessageId,
        //     ConsumerId). Es idempotencia de transporte, la misma idea que la
        //     tabla ProcessedMessages de 3.6.
        //
        // Y sí, InboxState y ProcessedMessages se solapan a propósito. La
        // decisión 2 de docs/fase_3_6.md descartó este inbox entonces porque
        // "resuelve la regla 6 escondiéndola", y prometió que el día que 4.5
        // trajera el outbox de verdad "la comparación entre las dos cosas estaría
        // escrita en el repo". Está: conviven, y ninguna sobra. InboxState
        // reconoce la misma ENTREGA y lo hace sin que se vea una línea de código;
        // la guarda de ProcessedMessages reconoce lo mismo pero se lee, y encima
        // los consumers tienen una segunda guarda —la de NEGOCIO, "este pedido ya
        // está en Confirmed"— que ninguna tabla de transporte puede dar.
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();

        base.OnModelCreating(modelBuilder);
    }
}
