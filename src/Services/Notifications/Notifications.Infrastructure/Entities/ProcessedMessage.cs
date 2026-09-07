namespace Notifications.Infrastructure.Entities;

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
/// ── Por qué esto no sobra teniendo la PK de Notifications ──
///
/// Porque son dos guardas distintas y reconocen cosas distintas: ésta reconoce la
/// misma **entrega**, la PK <c>(OrderId, Kind)</c> reconoce el mismo **pedido**.
/// Un <c>OrderConfirmed</c> reacuñado con un <c>MessageId</c> nuevo pasa por aquí
/// sin enterarse y lo para la otra; una reentrega del mismo mensaje la para ésta
/// antes incluso de consultar la tabla de negocio. Las dos conviven; ninguna
/// sustituye a la otra.
///
/// Aquí la de transporte tiene además un papel que en Payments no tenía: los dos
/// consumers de este servicio escriben en esta misma tabla, y es lo que hace que
/// la clave compuesta de abajo no sea hipotética desde el primer día.
///
/// El tipo es la **cuarta copia literal** — está igual en Inventory.Infrastructure,
/// Payments.Infrastructure y Orders.Infrastructure. No se comparte porque no hay
/// dónde: los cuatro <c>.Infrastructure</c> tienen **cero ProjectReference** a
/// propósito —ni siquiera a Shop133.Contracts— y no existe un proyecto de
/// infraestructura común. Mismo precedente que el bloque AddMassTransit (3.1, 3.4,
/// 3.5, 4.5) y que SqlServerContainerFixture (2.4), que sí se extrajo en 3.7
/// cuando hubo un proyecto de tests donde ponerlo.
/// </summary>
public sealed class ProcessedMessage
{
    /// <summary>
    /// Holgura de sobra para un nombre de tipo de C#. Lo que se guarda es
    /// <c>nameof(OrderConfirmedNotificationConsumer)</c>, no su nombre con espacio
    /// de nombres: la tabla vive en NotificationsDb y ahí no hay dos consumers que
    /// puedan llamarse igual.
    /// </summary>
    public const int ConsumerNameMaxLength = 200;

    /// <summary>
    /// Cabe el nombre completo de un mensaje de Shop133.Contracts con su espacio
    /// de nombres y de sobra.
    /// </summary>
    public const int MessageTypeMaxLength = 250;

    public ProcessedMessage(Guid messageId, string consumerName, string messageType)
    {
        // El Guid no se acuña aquí, igual que el OrderId de Notification: lo puso
        // MassTransit al publicar y llegó en el sobre. Guid.Empty significaría que
        // alguien lo inventó, y un identificador inventado no deduplica nada.
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

        // DateTimeOffset y no DateTime, igual que en Notification: mapea a
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
    /// 2.1—, y aquí no hay ninguna.
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
