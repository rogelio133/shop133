using Microsoft.EntityFrameworkCore;

using Notifications.Infrastructure.Entities;
using Notifications.Infrastructure.Persistence.Configurations;

namespace Notifications.Infrastructure.Persistence;

/// <summary>
/// La sesión con <c>NotificationsDb</c>, la **quinta** base de datos del sistema
/// y la primera que aparece después de la Fase 0 — las otras cuatro las creó
/// <c>db/init/01-create-databases.sql</c> desde el principio.
///
/// Se conecta con <c>notifications_user</c>, que tiene <c>db_owner</c> sobre
/// <c>NotificationsDb</c> y **ningún permiso** sobre las otras cuatro: la regla 1
/// de CLAUDE.md aplicada por el motor, no por convención. Un intento de leer el
/// pedido "de verdad" en OrdersDb no falla en revisión de código, falla con
/// <c>Msg 916</c> — y por eso el <c>CustomerEmail</c> viaja dentro del evento.
///
/// Está en Notifications.Infrastructure y no en Notifications.API porque lo
/// comprueba el test <c>DbContextFiles_LiveOnlyIn_OwningServiceInfrastructure</c>,
/// que exige la ruta <c>src/Services/&lt;S&gt;/&lt;S&gt;.Infrastructure/…</c>.
/// </summary>
public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options)
    : DbContext(options)
{
    /// <summary>
    /// Los avisos "mandados", uno por pedido y desenlace. Es la única tabla de
    /// negocio del servicio y todo el contenido observable de 4.6: el punto pide
    /// "log o mock de email" y esta tabla es lo que hace que el mock se pueda
    /// comprobar con un SELECT en vez de contando líneas en una consola.
    /// </summary>
    public DbSet<Notification> Notifications => Set<Notification>();

    /// <summary>
    /// Los mensajes ya procesados, una fila por (MessageId, consumer). Es la
    /// idempotencia de transporte de 3.6, la que exige la regla 6 de CLAUDE.md.
    ///
    /// No es de negocio: Notifications no gestiona mensajes, los recibe. Vive en
    /// esta base y no en una compartida porque la regla 1 no admite una base
    /// transversal, y porque marcar el mensaje y hacer el trabajo tienen que caber
    /// en la misma transacción — algo que dos bases no pueden dar sin transacción
    /// distribuida.
    /// </summary>
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ApplyConfiguration explícito y no ApplyConfigurationsFromAssembly, por
        // el mismo motivo que en los otros cuatro servicios: el escaneo por
        // reflexión hace que añadir una configuración sea invisible desde aquí, y
        // que renombrarla mal la deje fuera del modelo sin que nada avise.
        modelBuilder.ApplyConfiguration(new NotificationConfiguration());
        modelBuilder.ApplyConfiguration(new ProcessedMessageConfiguration());

        base.OnModelCreating(modelBuilder);
    }

    // Sin las tres tablas del outbox de MassTransit, al revés que OrdersDbContext
    // desde 4.5. No las necesita: Notifications **no publica nada** — es el final
    // de la coreografía, el único servicio del sistema que solo consume. Sin
    // publicación no hay doble escritura que cerrar.
}
