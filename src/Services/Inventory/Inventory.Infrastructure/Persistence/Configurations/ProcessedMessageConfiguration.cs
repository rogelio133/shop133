using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Inventory.Infrastructure.Entities;

namespace Inventory.Infrastructure.Persistence.Configurations;

/// <summary>
/// El mapeo de <see cref="ProcessedMessage"/> a la tabla <c>ProcessedMessages</c>.
///
/// En una clase aparte y nunca con Data Annotations, igual que en los otros tres
/// servicios. Todo está declarado a mano aunque parte coincida con las
/// convenciones de EF — una convención que cambie de versión no debe cambiar el
/// esquema en silencio.
/// </summary>
internal sealed class ProcessedMessageConfiguration : IEntityTypeConfiguration<ProcessedMessage>
{
    public void Configure(EntityTypeBuilder<ProcessedMessage> builder)
    {
        builder.ToTable("ProcessedMessages");

        // ── Clave compuesta, no el MessageId a secas ──
        //
        // Cada consumer de MassTransit tiene su propia cola, así que un mismo
        // mensaje se entrega a TODOS los consumers del servicio que lo escuchen,
        // con el mismo MessageId. Con la PK en el MessageId solo, el segundo
        // consumer encontraría la fila del primero y creería que ya lo procesó:
        // se saltaría un trabajo que nunca hizo, en silencio y sin error.
        //
        // Hoy Inventory tiene un consumer y la clave compuesta parece de más. La
        // Fase 4 trae el consumidor de ReleaseStock (4.4), y para entonces la
        // tabla ya tendría filas — cambiar la PK después es una migración con
        // datos dentro, no una línea.
        //
        // Descartado un Id propio (identity) con índice único sobre el par: es la
        // misma restricción escrita en dos sitios, y aquí no hay ninguna FK
        // apuntando a esta tabla que agradezca una clave estrecha.
        builder.HasKey(processed => new { processed.MessageId, processed.ConsumerName });

        // El Guid lo acuñó MassTransit al publicar y llegó en el sobre; aquí solo
        // se copia. Sin esta línea la convención de EF para un Guid en la PK es
        // ValueGeneratedOnAdd, que declara al modelo que el valor lo pone otro —
        // lo contrario de lo que hace el código. Misma línea y mismo razonamiento
        // que en StockReservationConfiguration.
        builder.Property(processed => processed.MessageId)
            .ValueGeneratedNever();

        // Las longitudes salen de las constantes de la entidad, nunca de
        // literales aquí — la regla que Catalog fijó en 1.3.
        //
        // En ConsumerName la longitud no es solo higiene: es parte de la clave, y
        // el índice de una PK en SQL Server no admite más de 900 bytes. Sin
        // HasMaxLength, EF no deja nvarchar(max) en una clave —le pone
        // nvarchar(450) por su cuenta—, así que el esquema saldría de un límite
        // del proveedor en vez de una decisión de este archivo. 200 caracteres
        // (400 bytes) sobran para un nombre de tipo y dejan margen de sobra.
        builder.Property(processed => processed.ConsumerName)
            .HasMaxLength(ProcessedMessage.ConsumerNameMaxLength)
            .IsRequired();

        builder.Property(processed => processed.MessageType)
            .HasMaxLength(ProcessedMessage.MessageTypeMaxLength)
            .IsRequired();

        // DateTimeOffset mapea a datetimeoffset sin ambigüedad de Kind, que es
        // justo por lo que la entidad no usa DateTime.
        builder.Property(processed => processed.ProcessedAt)
            .IsRequired();

        // Sin índice sobre ProcessedAt, y es una renuncia consciente: esta tabla
        // crece sin techo —una fila por mensaje, para siempre— y lo natural sería
        // purgarla por fecha. Nadie la purga todavía y no hay proceso que lo haga,
        // así que el índice no tendría consulta que servir. Cuando aparezca la
        // purga, aparece con su índice. Optimizar una tabla de cero filas es
        // optimizar sin medir (mismo criterio que 2.2, 3.4 y 3.5).
    }
}
