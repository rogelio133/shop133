using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Notifications.Infrastructure.Entities;

namespace Notifications.Infrastructure.Persistence.Configurations;

/// <summary>
/// El mapeo de <see cref="Notification"/> a la tabla <c>Notifications</c>.
///
/// En una clase aparte y nunca con Data Annotations, igual que en los otros
/// cuatro servicios: <see cref="Notification"/> no debe saber que existe EF Core.
/// Todo lo que hay aquí está declarado a mano aunque parte coincida con las
/// convenciones de EF — una convención que cambie de versión no debe cambiar el
/// esquema en silencio.
/// </summary>
internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("Notifications");

        // ── La PK es (OrderId, Kind), no un identificador propio ──
        //
        // Mismo criterio con el que la PK de StockReservations (3.4) y la de
        // Payments (3.5) son el OrderId: la única forma en que alguien va a buscar
        // una notificación es por su pedido, así que una identidad propia solo
        // añadiría un índice que mantener.
        //
        // El Kind entra en la clave por el efecto de idempotencia: **un pedido no
        // puede tener dos confirmaciones**, porque la segunda no cabe en la tabla.
        // Eso es idempotencia de negocio por clave, gratis, exactamente como la
        // consiguió Inventory en 3.4; no sustituye a la de transporte por MessageId
        // del sobre, que es 3.6 y vive en ProcessedMessages.
        //
        // Y deja pasar lo que tiene que dejar pasar: un pedido con las DOS filas
        // sería la saga confirmando y cancelando el mismo pedido — una incoherencia
        // real que esta tabla no debe tapar. Quien la impide es Order.Confirm() /
        // Cancel() en OrdersDb (4.3), que es donde vive esa invariante.
        //
        // Descartado un Id propio (identity) con índice único sobre el par: es la
        // misma restricción escrita en dos sitios, y aquí no hay ninguna FK
        // apuntando a esta tabla que agradezca una clave estrecha.
        builder.HasKey(notification => new { notification.OrderId, notification.Kind });

        // El Guid lo acuñó Orders.API, lo llevó la saga en OrderState y llegó
        // dentro del evento del desenlace; aquí solo se copia. Sin esta línea la
        // convención de EF para un Guid en la PK es ValueGeneratedOnAdd, que
        // declara al modelo que el valor lo pone otro — lo contrario de lo que hace
        // el código. Misma línea y mismo razonamiento que en OrderConfiguration,
        // StockReservationConfiguration y PaymentConfiguration.
        builder.Property(notification => notification.OrderId)
            .ValueGeneratedNever();

        // El enum se guarda como int, que es lo que EF hace por defecto — pero
        // declarado, porque de esa elección depende que los valores explícitos de
        // NotificationKind signifiquen algo. Aquí importa más que en
        // PaymentConfiguration: esta columna es **la mitad de la clave primaria**,
        // así que renumerar el enum no desordenaría unas filas, las dejaría
        // apuntando a otro desenlace.
        //
        // Con HasConversion<string>() la tabla sería legible a ojo; se queda en int
        // por simetría con OrderStatus en OrdersDb y PaymentStatus en PaymentsDb.
        builder.Property(notification => notification.Kind)
            .HasConversion<int>()
            .IsRequired();

        // La PK sale CLUSTERED y empieza por un uniqueidentifier aleatorio, lo que
        // fragmenta. No se toca, por lo mismo que en 2.2, 3.4, 3.5 y 4.5: la sonda
        // de docs/fase_1_1.md midió que SQL Server compara uniqueidentifier
        // empezando por los ÚLTIMOS 6 bytes, así que ni un UUID v7 llegaría
        // ordenado — el remedio habitual no es un remedio aquí. Y optimizar una
        // tabla de cero filas es optimizar sin medir.

        // Las longitudes salen de las constantes de la entidad, nunca de literales
        // aquí — la regla que Catalog fijó en 1.3.
        builder.Property(notification => notification.Recipient)
            .HasMaxLength(Notification.RecipientMaxLength)
            .IsRequired();

        builder.Property(notification => notification.Subject)
            .HasMaxLength(Notification.SubjectMaxLength)
            .IsRequired();

        // Con HasMaxLength y no nvarchar(max), aunque el cuerpo de un email tienda
        // a crecer: el límite es lo que le da sentido al Truncate() de la entidad.
        // Sin él, la guarda de allí no protegería de nada y un Reason
        // desproporcionado entraría entero sin que nadie lo hubiera decidido.
        builder.Property(notification => notification.Body)
            .HasMaxLength(Notification.BodyMaxLength)
            .IsRequired();

        // DateTimeOffset mapea a datetimeoffset sin ambigüedad de Kind, que es
        // justo por lo que la entidad no usa DateTime.
        builder.Property(notification => notification.SentAt)
            .IsRequired();

        // Sin índices más allá de la PK, deliberadamente y por el mismo criterio
        // que OrdersDb (2.2), InventoryDb (3.4) y PaymentsDb (3.5): ninguna
        // consulta necesita uno todavía — los dos consumers solo buscan por clave
        // primaria. El contraste con el índice único de Sku en 1.2 es la lección:
        // aquél entró antes que su endpoint porque era una INVARIANTE que la
        // entidad no podía sostener sola, no un índice de rendimiento.

        // Sin HasData. Al revés que Catalog (1.4) e Inventory (3.4), aquí no hay
        // nada que precargar: un aviso solo existe si antes hubo un pedido.
    }
}
