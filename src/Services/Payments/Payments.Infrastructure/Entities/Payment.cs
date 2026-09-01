namespace Payments.Infrastructure.Entities;

/// <summary>
/// El registro de que un pedido concreto ya se intentó cobrar, y con qué
/// resultado. Es la primera pieza de código de negocio de Payments y la única
/// fila que este servicio escribe.
///
/// Vive en Payments.Infrastructure y no en un Payments.Domain porque Payments no
/// tiene capa de dominio: la saga vive en Orders.Domain. Mismo criterio que
/// llevó a Catalog (1.1) y a Inventory (3.4) a no tenerla.
///
/// ── Por qué existe la tabla, si el importe ya viaja en el evento ──
///
/// La decisión 2 de docs/fase_3_2.md descartó que Payments tuviera base de datos
/// propia, y ese descarte **sigue siendo válido para lo que descartaba**: era
/// una base para *conseguir el importe*, alimentada por un consumer de
/// OrderCreated, y la tumbó una carrera real —RabbitMQ no ordena entre colas
/// distintas, así que StockReserved puede llegar antes—. El importe sigue
/// llegando en StockReserved.Amount y esta tabla no lo cambia.
///
/// Esta tabla entra por otro motivo: **sin ella el consumer no puede ser
/// idempotente de ninguna forma**. Una reentrega de StockReserved —que RabbitMQ
/// garantiza *al menos* una vez— cobraría dos veces y publicaría dos
/// PaymentCompleted con TransactionId distinto. Inventory se libró de esto
/// gratis porque la PK de StockReservations es el OrderId (3.4); aquí hace falta
/// escribirlo.
///
/// Y da además un sitio donde vive el <c>TransactionId</c>. El /// de
/// PaymentCompleted dice desde 0.3 que ese campo existe "porque es lo que
/// permitiría emitir la devolución si hubiera que compensar el pago": un
/// identificador de cobro que no se guarda en ningún sitio no permite nada.
/// </summary>
public sealed class Payment
{
    /// <summary>
    /// Longitud máxima del identificador simulado. 100 caracteres es holgado
    /// para el <c>SIM-{32 hex}</c> que acuña 3.5 y para cualquier referencia de
    /// una pasarela real, que suelen rondar los 30.
    /// </summary>
    public const int TransactionIdMaxLength = 100;

    /// <summary>
    /// Longitud máxima del motivo del rechazo. Mismo criterio que
    /// <c>StockRejected.Reason</c>: es texto de diagnóstico para un humano y
    /// material para el email de 4.6, así que sobra sitio.
    /// </summary>
    public const int FailureReasonMaxLength = 500;

    private Payment(
        Guid orderId,
        decimal amount,
        PaymentStatus status,
        string? transactionId,
        string? failureReason)
    {
        // El Guid no se acuña aquí, al revés que en Order: lo acuñó Orders.API,
        // viajó en OrderCreated, Inventory lo copió a StockReserved y aquí llega
        // por tercera vez. Es la clave de correlación de la saga; inventar una
        // identidad propia obligaría a mantener un índice para volver a
        // encontrar el cobro por pedido, que es la única forma en que alguien lo
        // va a buscar.
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("El OrderId de un cobro no puede ser Guid.Empty.", nameof(orderId));
        }

        // El importe se guarda tal cual llegó, incluso en un rechazo: saber
        // cuánto se intentó cobrar es la mitad de la información de un fallo.
        // No se valida que sea positivo — un importe absurdo es precisamente uno
        // de los motivos de rechazo que 3.5 registra, y una fila Failed tiene que
        // poder contarlo.
        OrderId = orderId;
        Amount = amount;
        Status = status;
        TransactionId = transactionId;
        FailureReason = failureReason;

        // DateTimeOffset y no DateTime: mapea a datetimeoffset sin ambigüedad de
        // Kind. UtcNow directo y no un TimeProvider inyectado, igual que en Order
        // y en StockReservation — ningún test afirma este sello.
        ProcessedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Constructor que usa EF Core al materializar filas. Mismo motivo que en
    /// <c>Product</c>, <c>Order</c> y <c>StockItem</c>: una fila ya persistida no
    /// se vuelve a validar — las guardas protegen la escritura, no la lectura.
    ///
    /// Las dos cadenas son anulables de verdad (una fila Completed no tiene
    /// motivo de fallo y una Failed no tiene transacción), así que aquí no hace
    /// falta ningún <c>null!</c>.
    /// </summary>
    private Payment()
    {
    }

    /// <summary>
    /// Clave primaria, y es el id del pedido — no un identificador propio de
    /// Payments. Se mapea con <c>ValueGeneratedNever()</c>: sin esa línea la
    /// convención de EF para una PK Guid es <c>ValueGeneratedOnAdd</c>, que le
    /// declara al modelo que el valor lo pone otro, justo lo contrario de lo que
    /// hace el código.
    ///
    /// Que la PK sea el OrderId tiene un segundo efecto, buscado: dos
    /// <c>StockReserved</c> del mismo pedido no pueden crear dos cobros. Eso es
    /// idempotencia **de negocio**, por clave de pedido, y no sustituye a la de
    /// transporte por <c>MessageId</c> del sobre, que sigue siendo 3.6.
    /// </summary>
    public Guid OrderId { get; private set; }

    /// <summary>
    /// El importe que se intentó cobrar, llegado en <c>StockReserved.Amount</c>.
    /// Se guarda también en los rechazos.
    /// </summary>
    public decimal Amount { get; private set; }

    /// <summary>El desenlace. Un cobro nace ya resuelto.</summary>
    public PaymentStatus Status { get; private set; }

    /// <summary>
    /// El identificador del cobro en la pasarela. Simulado en 3.5, con prefijo
    /// visible para que en un log no se confunda con uno real.
    ///
    /// Nulo si el cobro se rechazó. Que sea nulo exactamente cuando
    /// <see cref="Status"/> es <c>Failed</c> lo garantizan las dos factorías, no
    /// una restricción de la base: un CHECK constraint diría lo mismo una
    /// segunda vez y en otro idioma.
    /// </summary>
    public string? TransactionId { get; private set; }

    /// <summary>
    /// Por qué se rechazó, en texto legible. Nulo si el cobro salió bien.
    ///
    /// Es diagnóstico, **no un código que nadie deba parsear** para decidir — lo
    /// dice el /// de <c>PaymentFailed</c> desde 0.3, igual que
    /// <c>StockRejected.Reason</c>.
    /// </summary>
    public string? FailureReason { get; private set; }

    /// <summary>Cuándo se resolvió el cobro. Siempre en UTC.</summary>
    public DateTimeOffset ProcessedAt { get; private set; }

    /// <summary>
    /// Un cobro que salió bien.
    ///
    /// **Dos factorías en vez de un constructor público con cinco parámetros**, y
    /// ese es el motivo: son las que hacen imposible una fila con
    /// <c>Status = Failed</c> y <c>TransactionId</c> relleno, o una
    /// <c>Completed</c> sin él. Un constructor con los cinco argumentos deja esa
    /// invariante en manos de quien llama, y aquí hay exactamente dos llamantes
    /// que no deben poder equivocarse.
    /// </summary>
    public static Payment Completed(Guid orderId, decimal amount, string transactionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);

        var trimmed = transactionId.Trim();

        if (trimmed.Length > TransactionIdMaxLength)
        {
            throw new ArgumentException(
                $"El TransactionId no puede superar los {TransactionIdMaxLength} caracteres.",
                nameof(transactionId));
        }

        return new Payment(orderId, amount, PaymentStatus.Completed, trimmed, failureReason: null);
    }

    /// <summary>
    /// Un cobro rechazado, con el motivo. Ver <see cref="Completed"/> para por
    /// qué son dos factorías y no un constructor.
    /// </summary>
    public static Payment Declined(Guid orderId, decimal amount, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var trimmed = reason.Trim();

        if (trimmed.Length > FailureReasonMaxLength)
        {
            throw new ArgumentException(
                $"El motivo del rechazo no puede superar los {FailureReasonMaxLength} caracteres.",
                nameof(reason));
        }

        return new Payment(orderId, amount, PaymentStatus.Failed, transactionId: null, failureReason: trimmed);
    }

    // Sin Refund() y sin Retry(). No tienen llamante en 3.5: nadie consume
    // PaymentCompleted hasta que la saga de 4.2 lo haga, y la compensación de un
    // cobro no está en el roadmap —la de la Fase 4 libera stock, no devuelve
    // dinero—. Inventar aquí su firma sería lo que 1.1 evitó dejando a Product
    // sin Update() hasta que 1.3 lo necesitó, lo que 2.1 evitó no escribiendo
    // Order.Confirm(), y lo que 3.4 evitó dejando a StockItem sin Release().
}
