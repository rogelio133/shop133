namespace Orders.Infrastructure.Entities;

/// <summary>
/// La constancia de que un mensaje concreto ya pasó por un consumer concreto.
/// Es la regla 6 de CLAUDE.md hecha tabla: *"Persist processed MessageIds and
/// skip repeats"*.
///
/// **El identificador viene del sobre de MassTransit, no del contrato.** Es una
/// promesa escrita en 0.3, repetida en 2.1 y en 3.2: <c>Shop133.Contracts</c> no
/// tiene —ni tendrá— un campo de idempotencia. El sobre ya trae
/// <c>MessageId</c>, <c>ConversationId</c> y <c>SentTime</c>, y en 3.3 se
/// comprobó contra un broker real que el <c>messageId</c> llega relleno y el
/// <c>correlationId</c> nulo. Aquí se lee con <c>ConsumeContext.MessageId</c>.
///
/// ── Por qué esta tabla vive en Orders.Infrastructure y no en Orders.Domain ──
///
/// Es la primera entidad de Orders que **no** está en <c>Orders.Domain/Entities/</c>,
/// donde 2.1 puso <c>Order</c> y <c>OrderItem</c>, y la asimetría es deliberada:
/// esto no es una pieza del negocio de pedidos, es una constancia de transporte.
/// Un pedido existe aunque nadie use RabbitMQ; esta fila solo tiene sentido
/// porque los mensajes se entregan al menos una vez. Meterla en el dominio
/// obligaría a Orders.Domain —que solo referencia Shop133.Contracts y
/// MassTransit— a conocer un problema de mensajería que no es suyo, y la dejaría
/// al lado de un agregado con invariantes cuando esto es una fila de bitácora.
///
/// Además coincide con donde está en Inventory y Payments, que no tienen
/// proyecto de dominio: las tres copias viven en el mismo sitio relativo.
///
/// ── Por qué la clave es compuesta ──
///
/// Ver <c>ProcessedMessageConfiguration</c>. En corto: un mismo mensaje puede
/// llegar a varios consumers del mismo servicio, cada uno con su propia cola, y
/// con la PK en el <c>MessageId</c> a secas el segundo creería que es un
/// duplicado del primero. **Aquí eso deja de ser hipotético**: Orders estrena en
/// 4.3 sus dos primeros consumers a la vez, así que esta tabla es la primera del
/// proyecto con dos <c>ConsumerName</c> distintos escribiendo en ella.
///
/// El tipo está duplicado literalmente en Inventory.Infrastructure y en
/// Payments.Infrastructure. No se comparte porque no hay dónde: los
/// <c>.Infrastructure</c> de esos dos servicios tienen **cero ProjectReference**
/// —ni siquiera a Shop133.Contracts— y no existe un proyecto de infraestructura
/// común. Mismo precedente que el bloque AddMassTransit (3.1, 3.4, 3.5) y que
/// SqlServerContainerFixture (2.4): con la tercera copia delante la conclusión no
/// cambia, porque el proyecto compartido que haría falta no existe y crearlo para
/// una clase de datos sería más estructura que ahorro.
/// </summary>
public sealed class ProcessedMessage
{
    /// <summary>
    /// Holgura de sobra para un nombre de tipo de C#. Lo que se guarda es
    /// <c>nameof(OrderConfirmedConsumer)</c>, no su nombre con espacio de
    /// nombres: la tabla vive en OrdersDb y ahí no hay dos consumers que puedan
    /// llamarse igual.
    /// </summary>
    public const int ConsumerNameMaxLength = 200;

    /// <summary>
    /// Cabe el nombre completo de un mensaje de Shop133.Contracts con su espacio
    /// de nombres y de sobra.
    /// </summary>
    public const int MessageTypeMaxLength = 250;

    public ProcessedMessage(Guid messageId, string consumerName, string messageType)
    {
        // El Guid no se acuña aquí, igual que el Id de Order no se acuña en la
        // base: lo puso MassTransit al publicar y llegó en el sobre. Guid.Empty
        // significaría que alguien lo inventó, y un identificador inventado no
        // deduplica nada.
        if (messageId == Guid.Empty)
        {
            throw new ArgumentException(
                "El MessageId de un mensaje procesado no puede ser Guid.Empty.",
                nameof(messageId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);

        if (consumerName.Length > ConsumerNameMaxLength)
        {
            throw new ArgumentException(
                $"El nombre del consumer no puede pasar de {ConsumerNameMaxLength} caracteres.",
                nameof(consumerName));
        }

        if (messageType.Length > MessageTypeMaxLength)
        {
            throw new ArgumentException(
                $"El tipo del mensaje no puede pasar de {MessageTypeMaxLength} caracteres.",
                nameof(messageType));
        }

        MessageId = messageId;
        ConsumerName = consumerName;
        MessageType = messageType;

        // DateTimeOffset y no DateTime, igual que en Order.CreatedAt: mapea a
        // datetimeoffset sin ambigüedad de Kind. UtcNow directo y no un
        // TimeProvider inyectado — ningún test afirma este sello.
        ProcessedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Constructor que usa EF Core al materializar filas.
    ///
    /// Las cadenas van a <c>null!</c> y eso es correcto para strings: EF las
    /// asigna justo después. Lo que **nunca** puede ir a <c>null!</c> es una
    /// colección —EF rellena la que encuentra en vez de reemplazarla, medido en
    /// 2.1 con <c>Order._items</c>—, y aquí no hay ninguna.
    /// </summary>
    private ProcessedMessage()
    {
        ConsumerName = null!;
        MessageType = null!;
    }

    /// <summary>
    /// El <c>MessageId</c> del sobre. Primera mitad de la clave primaria, y se
    /// mapea con <c>ValueGeneratedNever()</c>: el valor lo puso MassTransit.
    /// </summary>
    public Guid MessageId { get; private set; }

    /// <summary>
    /// Qué consumer lo procesó. Segunda mitad de la clave primaria.
    /// </summary>
    public string ConsumerName { get; private set; }

    /// <summary>
    /// Qué mensaje era. **No lo lee nadie**: está para que la tabla se pueda
    /// mirar a mano y se entienda sin cruzarla con los logs. Si algún día alguien
    /// consulta por esta columna, necesitará un índice que hoy no existe.
    /// </summary>
    public string MessageType { get; private set; }

    /// <summary>Cuándo se procesó. Siempre en UTC.</summary>
    public DateTimeOffset ProcessedAt { get; private set; }
}
