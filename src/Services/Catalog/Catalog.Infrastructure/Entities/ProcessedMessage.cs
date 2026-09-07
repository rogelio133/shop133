namespace Catalog.Infrastructure.Entities;

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
/// ── En Catalog esta guarda es la ÚNICA que hay, y eso es nuevo ──
///
/// En los otros cuatro servicios esta tabla convive con una guarda de **negocio**
/// que reconoce el mismo *pedido* en vez de la misma *entrega*: la PK de
/// <c>StockReservations</c> (3.4, gratis), la fila de <c>Payments</c> (3.5,
/// escrita a mano) y la de <c>Notifications</c> con su clave <c>(OrderId, Kind)</c>
/// (4.6, gratis). Todas salieron de una fila que el consumer tenía que escribir
/// **de todas formas**.
///
/// **El consumer de 4.8 no escribe nada de negocio**: valida la foto de precios
/// leyendo <c>Products</c> y contesta. Es una lectura pura, así que no hay
/// artefacto del que sacar una guarda de negocio, y por tanto esta tabla es la
/// única defensa que existe — lo cual, lejos de hacerla opcional, es lo que la
/// hace imprescindible. Inventar una tabla <c>PricingValidations</c> para tener la
/// otra mitad sería que <c>CatalogDb</c> guardara datos de pedidos que no le
/// pertenecen (el espíritu de la regla 1) en una tabla que nadie lee ni purga.
///
/// La consecuencia, dicha en voz alta: un <c>OrderCreated</c> del mismo pedido
/// reacuñado con <c>MessageId</c> nuevo vuelve a validarse y vuelve a contestar.
/// Se acepta porque la respuesta es función pura del mensaje y de
/// <c>CatalogDb</c> —no un segundo efecto como sería un segundo cobro o una
/// segunda reserva—, y desde 4.9 la saga la absorbe con sus <c>Ignore</c>.
///
/// ── La marca es la única escritura, y de ahí sale una diferencia real ──
///
/// La guarda de negocio de Inventory republica el desenlace **guardado**, así que
/// su segunda respuesta es idéntica por construcción. Aquí se **recalcula**, así
/// que una reentrega tardía —con la ventana de precios ya vencida— puede contestar
/// <c>OrderPricingRejected</c> donde la primera contestó
/// <c>OrderPricingValidated</c>. No es un fallo de la guarda: es lo que significa
/// que la autenticidad tenga fecha de caducidad.
///
/// El tipo es la **quinta copia literal** — está igual en Inventory.Infrastructure,
/// Payments.Infrastructure, Orders.Infrastructure y Notifications.Infrastructure.
/// No se comparte porque no hay dónde: los cinco <c>.Infrastructure</c> tienen
/// **cero ProjectReference** a propósito —ni siquiera a Shop133.Contracts— y no
/// existe un proyecto de infraestructura común. Mismo precedente que el bloque
/// AddMassTransit (3.1, 3.4, 3.5, 4.5, 4.6) y que SqlServerContainerFixture (2.4),
/// que sí se extrajo en 3.7 cuando hubo un proyecto de tests donde ponerlo.
/// </summary>
public sealed class ProcessedMessage
{
    /// <summary>
    /// Holgura de sobra para un nombre de tipo de C#. Lo que se guarda es
    /// <c>nameof(OrderCreatedPricingConsumer)</c>, no su nombre con espacio de
    /// nombres: la tabla vive en CatalogDb y ahí no hay dos consumers que puedan
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
        // El Guid no se acuña aquí: lo puso MassTransit al publicar y llegó en el
        // sobre. Guid.Empty significaría que alguien lo inventó, y un
        // identificador inventado no deduplica nada.
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

        // DateTimeOffset y no DateTime: mapea a datetimeoffset sin ambigüedad de
        // Kind. UtcNow directo y no un TimeProvider inyectado — ningún test
        // afirma este sello.
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
