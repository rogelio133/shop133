using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Orders.Domain.Entities;

namespace Orders.Infrastructure.Persistence.Configurations;

/// <summary>
/// El mapeo de <see cref="Order"/> a la tabla <c>Orders</c> y, dentro, el de
/// <see cref="OrderItem"/> a <c>OrderItems</c>.
///
/// Está en una clase aparte y no con Data Annotations sobre las entidades porque
/// Orders.Domain **no puede** saber que existe EF Core: la regla 5 de CLAUDE.md
/// dice que la capa de dominio solo referencia Shop133.Contracts, y el test
/// <c>OrdersDomain_ProjectReferences_ContainOnlyContracts</c> lo comprueba. En
/// Catalog era una decisión de estilo (decisión 1 de docs/fase_1_2.md); aquí es
/// además una imposibilidad técnica.
///
/// Todo está declarado a mano aunque parte coincida con las convenciones de EF:
/// este archivo es el sitio donde se lee el esquema, y una convención implícita
/// no se lee en ninguna parte.
/// </summary>
internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders");

        builder.HasKey(order => order.Id);

        // ── La línea más importante del archivo ──
        //
        // El Guid lo acuña el constructor de Order, no la base de datos. Es la
        // decisión 4 de docs/fase_0_3.md y el motivo está escrito en Order.cs:
        // el Id es la clave de correlación de la saga, así que Orders.API tiene
        // que poder publicar OrderCreated sin haber esperado a un INSERT.
        //
        // Sin esta línea, la convención de EF para una PK Guid es
        // ValueGeneratedOnAdd, que declara al modelo que el valor lo pone otro.
        // Hoy no cambiaría el resultado — EF solo genera cuando encuentra
        // Guid.Empty, y el constructor nunca deja eso — pero el esquema estaría
        // diciendo lo contrario de lo que hace el código, y en 4.5 esa mentira
        // se paga: la saga correlaciona por un valor que el modelo declara ajeno.
        builder.Property(order => order.Id)
            .ValueGeneratedNever();

        // La PK sale CLUSTERED (default de SQL Server) sobre un uniqueidentifier
        // aleatorio, y eso fragmenta. No se toca aquí, y es deliberado:
        //
        // La sonda de docs/fase_1_1.md midió que SQL Server compara
        // uniqueidentifier empezando por los ÚLTIMOS 6 bytes, así que ni siquiera
        // un UUID v7 llegaría ordenado a la tabla — el remedio habitual no es un
        // remedio. La alternativa real sería IsClustered(false) aquí más un
        // índice clustered sobre CreatedAt, y eso es optimizar sin haber medido
        // nada sobre esta tabla, que hoy tiene cero filas.
        //
        // La pregunta la dejó abierta la sección Pendiente de docs/fase_1_2.md
        // para 4.5, que es cuando OrdersDb reciba escrituras de verdad.
        //
        // Releída en 4.5 con OrderStates delante —una tabla con la misma forma de
        // clave y muchas más escrituras por pedido— y la respuesta es la misma:
        // se queda clustered. El razonamiento completo está en
        // OrderStateConfiguration, para no tenerlo a medias en dos archivos. Lo
        // único que cambia es que ahora la pregunta se puede *medir*, y eso pasa
        // a 8.2 con el resto de la infraestructura real.

        builder.Property(order => order.CustomerEmail)
            .IsRequired()
            .HasMaxLength(Order.CustomerEmailMaxLength);

        // Sin índice sobre CustomerEmail: no hay ninguna consulta que lo use.
        // 2.3 solo inserta y 6.5 lee por id. Un índice se añade cuando existe la
        // consulta que lo justifica, no por si acaso — cuesta escrituras y
        // espacio desde el primer INSERT.

        // El enum se persiste como su valor numérico. HasConversion<int>() es
        // redundante (es lo que EF hace por defecto) y se declara igual: los
        // números explícitos de OrderStatus son un contrato con esta columna
        // — insertar un estado en medio de la lista renumeraría filas ya
        // guardadas — y ese contrato tiene que verse desde los dos lados.
        builder.Property(order => order.Status)
            .HasConversion<int>()
            .IsRequired();

        // DateTimeOffset mapea a datetimeoffset sin ambigüedad de Kind, que es
        // justo por lo que la entidad no usa DateTime.
        builder.Property(order => order.CreatedAt)
            .IsRequired();

        // Calculado, no persistido: una sola fuente de verdad. Sin Ignore(), EF
        // ve una propiedad decimal pública y le busca columna — y como no tiene
        // setter ni campo de respaldo, el modelo ni siquiera llega a construirse.
        builder.Ignore(order => order.Total);

        // ── Las líneas: tipo owned, no entidad propia ──
        //
        // Es la pregunta que 2.1 dejó explícitamente para este punto. OwnsMany
        // es la traducción literal de lo que dice Order.Items: una línea de
        // pedido no tiene identidad fuera de su pedido. Nadie la pide por id y
        // ningún mensaje de Shop133.Contracts la referencia.
        //
        // Lo que se gana frente a mapearla como entidad normal con clave sombra:
        // EF impide consultarla suelta, la carga siempre con el pedido (sin
        // Include, que es un olvido menos en 2.3 y 6.5) y el borrado en cascada
        // sale del propio mapeo. Lo que se paga: la PK compuesta (OrderId, Id)
        // con un Id en la sombra, que no se puede usar para nada desde C# — y
        // eso es una consecuencia buscada, no un efecto colateral.
        builder.OwnsMany(order => order.Items, items =>
        {
            items.ToTable("OrderItems");

            // La FK al dueño es una propiedad en la sombra: OrderItem no tiene
            // OrderId y no debe tenerlo (ver la nota de Order.Items). EF la crea
            // en la tabla, no en la clase.
            items.WithOwner().HasForeignKey("OrderId");

            // ProductId es un puntero DÉBIL, no una clave foránea. No hay
            // FK posible ni la habrá: el producto vive en CatalogDb, SQL Server
            // no soporta FK entre bases y orders_user no puede ni abrirla
            // (regla 1). Que ese id apunte a un producto ya borrado es un
            // resultado aceptado — por eso las tres columnas de abajo congelan
            // lo que Catalog dijo ese día.
            items.Property(item => item.ProductId)
                .IsRequired();

            // Longitudes desde las constantes de OrderItem, nunca literales.
            // Ojo: son las constantes de OrderItem y NO las de Product, que
            // valen lo mismo. La duplicación es la decisión de 2.1 y estas dos
            // pueden divergir de las de Catalog sin que nada se rompa.
            items.Property(item => item.ProductSku)
                .IsRequired()
                .HasMaxLength(OrderItem.ProductSkuMaxLength);

            items.Property(item => item.ProductName)
                .IsRequired()
                .HasMaxLength(OrderItem.ProductNameMaxLength);

            items.Property(item => item.Quantity)
                .IsRequired();

            // decimal(18,2), igual que Product.Price. Es el default del provider
            // de SQL Server y se declara por el mismo motivo que allí: dejarlo
            // implícito hace que un cambio de provider mueva el tipo de una
            // columna de dinero sin que nadie lo note.
            items.Property(item => item.UnitPrice)
                .HasPrecision(18, 2);

            // Calculado, igual que Order.Total.
            items.Ignore(item => item.Subtotal);
        });

        // La colección se lee y se escribe por el CAMPO, nunca por la propiedad.
        //
        // Order.Items devuelve _items.AsReadOnly(), o sea un ReadOnlyCollection
        // nuevo en cada lectura, cuyo Add lanza NotSupportedException (medido en
        // 2.1). Si EF materializara las líneas a través de la propiedad, cargar
        // un pedido reventaría. La convención de EF ya prefiere el campo cuando
        // encuentra uno con este nombre, pero aquí no puede quedar implícito:
        // depende de una coincidencia de nombres entre _items e Items que nada
        // vigila, y el fallo aparecería en la primera lectura de un pedido.
        builder.Navigation(order => order.Items)
            .HasField("_items")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
