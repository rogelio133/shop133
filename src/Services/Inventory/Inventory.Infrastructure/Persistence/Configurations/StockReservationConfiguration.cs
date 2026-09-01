using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Inventory.Infrastructure.Entities;

namespace Inventory.Infrastructure.Persistence.Configurations;

/// <summary>
/// El mapeo de <see cref="StockReservation"/> a la tabla
/// <c>StockReservations</c> y, dentro, el de <see cref="StockReservationLine"/>
/// a <c>StockReservationLines</c>.
///
/// Es la traducción literal de lo que 2.2 decidió para <c>Order</c>/<c>OrderItem</c>,
/// y a propósito: son la misma forma —un agregado con líneas que no tienen vida
/// propia— y resolverla dos veces de dos maneras distintas obligaría a leer las
/// dos para saber cuál rige.
/// </summary>
internal sealed class StockReservationConfiguration : IEntityTypeConfiguration<StockReservation>
{
    public void Configure(EntityTypeBuilder<StockReservation> builder)
    {
        builder.ToTable("StockReservations");

        // La PK es el OrderId. No hay identificador propio de la reserva, y esa
        // es la decisión que docs/fase_3_2.md aplazó a este punto para que 4.4
        // pudiera decidir si ReleaseStock prescinde de Lines: con esta clave,
        // soltar el stock de un pedido es un SELECT por PK.
        //
        // El segundo efecto es de idempotencia: dos OrderCreated del mismo
        // pedido no pueden crear dos reservas, porque la segunda no cabe en la
        // tabla. Es idempotencia de negocio y no sustituye a la de transporte
        // por MessageId del sobre, que es 3.6.
        builder.HasKey(reservation => reservation.OrderId);

        // El Guid lo acuñó Orders.API y llegó dentro de OrderCreated; aquí solo
        // se copia. Sin esta línea la convención de EF para una PK Guid es
        // ValueGeneratedOnAdd, que declara al modelo que el valor lo pone otro
        // —lo contrario de lo que hace el código—. Mismo razonamiento y misma
        // línea que en OrderConfiguration.
        builder.Property(reservation => reservation.OrderId)
            .ValueGeneratedNever();

        // La PK sale CLUSTERED sobre un uniqueidentifier aleatorio, y eso
        // fragmenta. No se toca, por lo mismo que en 2.2: la sonda de
        // docs/fase_1_1.md midió que SQL Server compara uniqueidentifier
        // empezando por los ÚLTIMOS 6 bytes, así que ni un UUID v7 llegaría
        // ordenado — el remedio habitual no es un remedio aquí. Y optimizar una
        // tabla de cero filas es optimizar sin medir.

        // DateTimeOffset mapea a datetimeoffset sin ambigüedad de Kind, que es
        // justo por lo que la entidad no usa DateTime.
        builder.Property(reservation => reservation.CreatedAt)
            .IsRequired();

        // ── Las líneas: tipo owned, no entidad propia ──
        //
        // Igual que OrderItem en 2.2. Lo que se gana: EF impide consultar una
        // línea suelta, la carga siempre con su reserva (sin Include, que es un
        // olvido menos en 4.4) y el borrado en cascada sale del propio mapeo.
        //
        // Lo que se paga: la PK compuesta (OrderId, Id) con un Id IDENTITY que
        // **no existe en C#**. Así construye EF la clave de una colección owned;
        // no es una mala configuración y no hay que "arreglarlo".
        builder.OwnsMany(reservation => reservation.Lines, lines =>
        {
            lines.ToTable("StockReservationLines");

            // La FK al dueño es una propiedad en la sombra: StockReservationLine
            // no tiene OrderId y no debe tenerlo.
            lines.WithOwner().HasForeignKey("OrderId");

            lines.Property(line => line.ProductId)
                .IsRequired();

            lines.Property(line => line.Quantity)
                .IsRequired();
        });

        // La colección se lee y se escribe por el CAMPO, nunca por la propiedad.
        //
        // StockReservation.Lines devuelve _lines.AsReadOnly(), o sea un
        // ReadOnlyCollection nuevo en cada lectura, cuyo Add lanza
        // NotSupportedException (medido en 2.1). Si EF materializara las líneas
        // a través de la propiedad, leer una reserva reventaría. La convención
        // de EF ya prefiere el campo, pero esa preferencia descansa en una
        // coincidencia de nombres entre _lines y Lines que nada vigila.
        builder.Navigation(reservation => reservation.Lines)
            .HasField("_lines")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
