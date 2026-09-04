using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

// Order, solo por su constante de longitud del email — ver la nota sobre
// CustomerEmail más abajo.
using Orders.Domain.Entities;
using Orders.Domain.Sagas;

namespace Orders.Infrastructure.Persistence.Configurations;

/// <summary>
/// El mapeo de <see cref="OrderState"/> —la instancia de saga— a la tabla
/// <c>OrderStates</c> de <c>OrdersDb</c>. Es la tabla que 2.2 anunció y no creó,
/// y la que convierte la máquina de estados de 4.1–4.4 en algo que sobrevive a un
/// reinicio del proceso.
///
/// ── Escrito a mano y no con el <c>SagaClassMap&lt;OrderState&gt;</c> de
///    MassTransit ──
///
/// El paquete <c>MassTransit.EntityFrameworkCore</c> —que este proyecto ya tiene,
/// porque el outbox lo necesita— trae <c>SagaClassMap&lt;T&gt;</c>, una clase base
/// que pone la clave y el <c>ValueGeneratedNever</c> por ti. Habrían sido tres
/// líneas.
///
/// *Descartado* por dos motivos. El primero es el que el repositorio lleva
/// aplicando desde 1.2: **todo se declara a mano aunque coincida con una
/// convención**, porque este archivo es el sitio donde se lee el esquema y una
/// convención no se lee en ninguna parte — con la clase base, las longitudes de
/// las tres columnas de texto y la <c>rowversion</c> habría que ponerlas igual, y
/// el archivo acabaría siendo mitad herencia mitad declaración. El segundo es que
/// heredar de un tipo de MassTransit ataría el esquema de una tabla de este
/// servicio a las convenciones de una librería, que es exactamente lo que la
/// decisión de no usar Data Annotations evita en el otro sentido.
///
/// Lo que **sí** se delega a MassTransit son las tres tablas del outbox
/// (<c>InboxState</c>, <c>OutboxMessage</c>, <c>OutboxState</c>): ver el
/// <c>OnModelCreating</c> de <c>OrdersDbContext</c>. La diferencia es de
/// propiedad — <see cref="OrderState"/> es un tipo de este proyecto y esas tres
/// son estructuras internas de la librería, cuyo esquema no nos toca decidir.
/// </summary>
internal sealed class OrderStateConfiguration : IEntityTypeConfiguration<OrderState>
{
    public void Configure(EntityTypeBuilder<OrderState> builder)
    {
        builder.ToTable("OrderStates");

        // La PK **es** el OrderId. No hay conversión ni tabla de equivalencias:
        // la decisión 5 de docs/fase_0_3.md descartó meter un CorrelationId en
        // los contratos para que este valor y el del pedido fueran el mismo, y
        // quien los iguala es el CorrelateById de OrderStateMachine.
        //
        // Consecuencia práctica que conviene tener presente: OrdersDb tiene ahora
        // dos tablas con la MISMA clave —Orders y OrderStates— y ninguna FK entre
        // ellas. Es correcto y deliberado: son el pedido y el proceso que lo
        // coordina, con ciclos de vida distintos. Una FK obligaría a la saga a
        // no poder existir sin su fila de pedido, y el orden de escritura de los
        // dos no está garantizado.
        builder.HasKey(saga => saga.CorrelationId);

        // El Guid lo acuñó el constructor de Order y llegó dentro de
        // OrderCreated; aquí solo se copia. Mismo razonamiento —y misma línea—
        // que en OrderConfiguration y ProcessedMessageConfiguration: sin esto,
        // la convención de EF para una PK Guid es ValueGeneratedOnAdd, que
        // declararía al modelo que el valor lo pone otro.
        //
        // Aquí la mentira sí se pagaría, y es justo lo que anticipaba el
        // comentario de 2.2: la saga correlaciona por este valor.
        builder.Property(saga => saga.CorrelationId)
            .ValueGeneratedNever();

        // ── La PK se queda CLUSTERED, y esta vez con la tabla delante ──
        //
        // La sección Pendiente de docs/fase_1_2.md y la decisión 6 de
        // docs/fase_2_2.md dejaron la pregunta abierta para este punto, "que es
        // cuando OrdersDb reciba escrituras de verdad". Ya las recibe: cada
        // pedido escribe aquí una fila y la actualiza entre tres y cinco veces.
        //
        // Se queda clustered igual, y el motivo no ha cambiado: la sonda de
        // docs/fase_1_1.md midió que SQL Server compara uniqueidentifier
        // empezando por los ÚLTIMOS 6 bytes, así que el remedio popular —"usa
        // UUID v7 y deja de fragmentar"— es falso en este motor. La alternativa
        // real sigue siendo IsClustered(false) más un clustered sobre CreatedAt,
        // y sigue siendo optimizar sin haber medido nada.
        //
        // Lo que sí cambia es que ahora la pregunta tiene dónde medirse: con
        // volumen, sys.dm_db_index_physical_stats sobre esta tabla da una
        // respuesta en vez de una opinión. Se anota para 8.2, que es donde el
        // roadmap pone la concurrencia y la infraestructura real.

        // 64 caracteres, desde la constante de la entidad y nunca un literal
        // (regla de 1.3). Sin esta línea EF deja nvarchar(max) en la columna que
        // más se lee de la tabla: es la que el repositorio compara para saber en
        // qué estado está la saga, y nvarchar(max) no puede indexarse el día que
        // haga falta.
        //
        // Es string y no int por la decisión 5 de docs/fase_4_1.md: el int
        // ahorraría espacio y obligaría a declarar los estados en un orden
        // intocable. Aquí se ve el resultado — un SELECT sobre esta tabla se lee
        // sin descifrar nada.
        builder.Property(saga => saga.CurrentState)
            .IsRequired()
            .HasMaxLength(OrderState.CurrentStateMaxLength);

        // La longitud sale de Order.CustomerEmailMaxLength y NO de una constante
        // propia. Aquí sí se comparte, al revés que en OrderItem: aquel duplica
        // las constantes de Product porque importarlas rompería la regla 1 y la
        // 5, y son datos de dos servicios que pueden divergir. Esto es el mismo
        // email del mismo pedido, en el mismo proyecto. Duplicar el número aquí
        // sería inventarse una divergencia que no existe.
        builder.Property(saga => saga.CustomerEmail)
            .IsRequired()
            .HasMaxLength(Order.CustomerEmailMaxLength);

        // IsRequired aunque el valor por defecto sea cadena vacía, no null: la
        // entidad lo inicializa a string.Empty precisamente para que exista un
        // valor en los caminos que llegan aquí sin haber pasado por el .Then que
        // lo rellena. Una columna NULL permitiría distinguir "no se canceló" de
        // "se canceló sin motivo", y eso ya lo dice CurrentState.
        builder.Property(saga => saga.CancellationReason)
            .IsRequired()
            .HasMaxLength(OrderState.CancellationReasonMaxLength);

        // ── El token de concurrencia optimista ──
        //
        // IsRowVersion() mapea el byte[] a una columna `rowversion` que SQL
        // Server incrementa solo en cada UPDATE, y hace que EF la incluya en el
        // WHERE. Dos mensajes del mismo pedido procesados a la vez leen la misma
        // fila; el segundo en escribir afecta a cero filas y salta
        // DbUpdateConcurrencyException, que el UseMessageRetry del Program.cs
        // convierte en un reintento.
        //
        // Ese retry es la mitad que falta de esta línea. Sin él, la protección
        // no protege: cambia un dato pisado por un mensaje en order-state_error.
        //
        // Descartado el modo pesimista de MassTransit (el default para SQL
        // Server), que no necesita esta columna porque bloquea la fila con
        // UPDLOCK/ROWLOCK al leerla — ver el /// de OrderState.RowVersion.
        builder.Property(saga => saga.RowVersion)
            .IsRowVersion();

        // Sin índices más allá de la PK. El repositorio de saga solo consulta por
        // CorrelationId —que es la clave— y nadie más lee esta tabla; la página
        // de estado del pedido de 6.5 leerá Orders, no esto. Mismo criterio que
        // 2.2, 3.4, 3.5, 3.6 y 4.3: un índice se añade cuando existe la consulta
        // que lo justifica.
        //
        // Tampoco hay índice sobre CreatedAt, y esa ausencia sí tiene un caso de
        // uso a la vista: la consulta "sagas que llevan demasiado tiempo sin
        // terminar" es lo que haría falta para detectar el pedido atascado en
        // CompensatingStock del que avisa OrderStateMachine. Como no hay nadie
        // que la ejecute —ese agujero sigue sin dueño en el roadmap—, el índice
        // no tendría consulta que servir. Entra con ella.
    }
}
