using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Payments.Infrastructure.Entities;

namespace Payments.Infrastructure.Persistence.Configurations;

/// <summary>
/// El mapeo de <see cref="Payment"/> a la tabla <c>Payments</c>.
///
/// En una clase aparte y nunca con Data Annotations, igual que en los otros tres
/// servicios: <see cref="Payment"/> no debe saber que existe EF Core. Todo lo
/// que hay aquí está declarado a mano aunque parte coincida con las convenciones
/// de EF — una convención que cambie de versión no debe cambiar el esquema en
/// silencio.
/// </summary>
internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("Payments");

        // La PK es el OrderId. No hay identificador propio del cobro, por el
        // mismo criterio con el que la PK de StockReservations es el OrderId
        // (3.4): la única forma en que alguien va a buscar un cobro es por su
        // pedido, así que una identidad propia solo añadiría un índice que
        // mantener.
        //
        // El segundo efecto es de idempotencia: dos StockReserved del mismo
        // pedido no pueden crear dos cobros, porque el segundo no cabe en la
        // tabla. Es idempotencia de negocio y no sustituye a la de transporte por
        // MessageId del sobre, que es 3.6.
        builder.HasKey(payment => payment.OrderId);

        // El Guid lo acuñó Orders.API y llegó, por tercera vez, dentro de
        // StockReserved; aquí solo se copia. Sin esta línea la convención de EF
        // para una PK Guid es ValueGeneratedOnAdd, que declara al modelo que el
        // valor lo pone otro — lo contrario de lo que hace el código. Misma línea
        // y mismo razonamiento que en OrderConfiguration y en
        // StockReservationConfiguration.
        builder.Property(payment => payment.OrderId)
            .ValueGeneratedNever();

        // La PK sale CLUSTERED sobre un uniqueidentifier aleatorio, y eso
        // fragmenta. No se toca, por lo mismo que en 2.2 y 3.4: la sonda de
        // docs/fase_1_1.md midió que SQL Server compara uniqueidentifier
        // empezando por los ÚLTIMOS 6 bytes, así que ni un UUID v7 llegaría
        // ordenado — el remedio habitual no es un remedio aquí. Y optimizar una
        // tabla de cero filas es optimizar sin medir.

        // decimal(18,2) explícito. Sin HasPrecision, EF mapea decimal a
        // decimal(18,2) por convención en SQL Server, pero dejarlo a la
        // convención significa que el esquema del dinero depende de una decisión
        // del provider. Mismo criterio que ProductConfiguration desde 1.2.
        builder.Property(payment => payment.Amount)
            .HasPrecision(18, 2)
            .IsRequired();

        // El enum se guarda como int, que es lo que EF hace por defecto — pero
        // declarado, porque de esa elección depende que los valores explícitos de
        // PaymentStatus signifiquen algo. Con HasConversion<string>() la tabla
        // sería legible a ojo y el orden de los valores dejaría de importar; se
        // queda en int por simetría con OrderStatus en OrdersDb.
        builder.Property(payment => payment.Status)
            .HasConversion<int>()
            .IsRequired();

        // Las dos cadenas son NULL de verdad y no cadenas vacías: una fila
        // Completed no tiene motivo de fallo, y una Failed no tiene transacción.
        // Las longitudes salen de las constantes de la entidad, nunca de
        // literales aquí — la regla que Catalog fijó en 1.3.
        builder.Property(payment => payment.TransactionId)
            .HasMaxLength(Payment.TransactionIdMaxLength);

        builder.Property(payment => payment.FailureReason)
            .HasMaxLength(Payment.FailureReasonMaxLength);

        // Sin CHECK constraint que ate Status con las dos columnas anulables.
        // Esa invariante ya la sostienen las dos factorías de Payment, que son el
        // único camino para crear una fila; repetirla en el esquema sería decir lo
        // mismo dos veces en dos idiomas, y el día que divergieran habría que leer
        // las dos para saber cuál manda.

        // DateTimeOffset mapea a datetimeoffset sin ambigüedad de Kind, que es
        // justo por lo que la entidad no usa DateTime.
        builder.Property(payment => payment.ProcessedAt)
            .IsRequired();

        // Sin índices más allá de la PK, deliberadamente y por el mismo criterio
        // que OrdersDb en 2.2: ninguna consulta necesita uno todavía — el consumer
        // solo busca por clave primaria. El contraste con el índice único de Sku
        // en 1.2 es la lección: aquél entró antes que su endpoint porque era una
        // INVARIANTE que la entidad no podía sostener sola, no un índice de
        // rendimiento. Las dos cosas no se deciden con la misma regla.

        // Sin HasData. Al revés que Catalog (1.4) e Inventory (3.4), aquí no hay
        // nada que precargar: un cobro solo existe si alguien pidió algo.
    }
}
