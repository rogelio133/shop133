namespace Notifications.Infrastructure.Entities;

/// <summary>
/// El email que se le "mandó" al cliente por el desenlace de un pedido. Es la
/// única fila de negocio que Notifications escribe, y todo el contenido de 4.6:
/// el punto del roadmap pide "log o mock de email", y esto es el mock.
///
/// Vive en Notifications.Infrastructure y no en un Notifications.Domain porque
/// este servicio no tiene capa de dominio — mismo criterio que Catalog (1.1),
/// Inventory (3.4) y Payments (3.5). La saga vive en Orders.Domain y no hay más.
///
/// ── Por qué Notifications tiene base de datos, si el /// de los contratos decía
///    lo contrario ──
///
/// El <c>///</c> de <c>OrderConfirmed</c> afirma desde 0.3 que este servicio "no
/// tiene base de datos propia y no puede leer OrdersDb", y por eso el
/// <c>CustomerEmail</c> viaja dentro del evento. **Esa frase sigue siendo cierta
/// en lo que decía**: Notifications no puede consultar el pedido en ningún sitio,
/// así que o el dato llega en el mensaje o el servicio no puede trabajar. La base
/// que aparece aquí no le da acceso a nada ajeno.
///
/// Aparece por el mismo motivo que la de Payments en 3.5: **sin una fila que
/// consultar, el consumer no puede ser idempotente de ninguna forma**, y la regla
/// 6 de CLAUDE.md no admite excepciones. Descartada una guarda en memoria (un
/// diccionario de MessageId): cumple mientras el proceso viva y se pierde al
/// reiniciar, justo cuando una reentrega es más probable.
///
/// El segundo motivo es que hace el punto **verificable con un SELECT**. Con solo
/// un log, comprobar que un duplicado no mandó dos emails es contar líneas en una
/// consola.
/// </summary>
public sealed class Notification
{
    /// <summary>
    /// 320 caracteres, el máximo de una dirección de correo según la RFC 5321
    /// (64 de parte local + @ + 255 de dominio).
    ///
    /// **Duplica a propósito la constante de <c>Order.CustomerEmail</c>** en vez
    /// de importarla: hacerlo obligaría a Notifications.Infrastructure a
    /// referenciar Orders.Domain, que rompe la regla 1 en tiempo de compilación y
    /// la 5 de plano. Mismo precedente que <c>OrderItem</c> duplicando las
    /// longitudes de <c>Product</c> en 2.1. Las dos pueden divergir sin drama: aquí
    /// solo hay que poder guardar la dirección que llegó ese día.
    /// </summary>
    public const int RecipientMaxLength = 320;

    /// <summary>Holgura de sobra para un asunto de correo.</summary>
    public const int SubjectMaxLength = 200;

    /// <summary>
    /// El cuerpo. 2000 caracteres: dentro caben las plantillas de abajo con el
    /// <c>Reason</c> completo de <c>OrderCancelled</c>, que a su vez arrastra los
    /// motivos acumulados de <c>StockRejected</c>.
    /// </summary>
    public const int BodyMaxLength = 2000;

    private Notification(
        Guid orderId,
        NotificationKind kind,
        string recipient,
        string subject,
        string body)
    {
        // El Guid no se acuña aquí, igual que en Payment y en StockReservation: lo
        // acuñó Orders.API, lo llevó la saga en OrderState y llegó dentro del
        // evento del desenlace. Es la clave de correlación de la saga.
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException(
                "El OrderId de una notificación no puede ser Guid.Empty.",
                nameof(orderId));
        }

        OrderId = orderId;
        Kind = kind;
        Recipient = recipient;
        Subject = subject;
        Body = body;

        // DateTimeOffset y no DateTime, igual que en el resto del proyecto: mapea a
        // datetimeoffset sin ambigüedad de Kind. UtcNow directo y no un
        // TimeProvider inyectado — ningún test afirma este sello.
        SentAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Constructor que usa EF Core al materializar filas. Mismo motivo que en
    /// <c>Product</c>, <c>Order</c>, <c>StockItem</c> y <c>Payment</c>: una fila ya
    /// persistida no se vuelve a validar — las guardas protegen la escritura, no la
    /// lectura.
    ///
    /// Las tres cadenas van a <c>null!</c> y eso es correcto para strings: EF las
    /// asigna justo después. Lo que **nunca** puede ir a <c>null!</c> es una
    /// colección —EF rellena la que encuentra en vez de reemplazarla, medido en
    /// 2.1—, y aquí no hay ninguna.
    /// </summary>
    private Notification()
    {
        Recipient = null!;
        Subject = null!;
        Body = null!;
    }

    /// <summary>
    /// Primera mitad de la clave primaria. Ver <c>NotificationConfiguration</c>
    /// para por qué la clave es <c>(OrderId, Kind)</c> y no un identificador propio.
    /// </summary>
    public Guid OrderId { get; private set; }

    /// <summary>
    /// Segunda mitad de la clave primaria: qué desenlace se notificó.
    /// </summary>
    public NotificationKind Kind { get; private set; }

    /// <summary>
    /// A quién se le mandó. Llega en <c>OrderConfirmed.CustomerEmail</c> /
    /// <c>OrderCancelled.CustomerEmail</c> y **se congela aquí**, exactamente como
    /// <c>OrderItem</c> congela el nombre y el precio del producto en 2.1: un email
    /// mandado es un hecho pasado, y si el cliente cambia de dirección mañana este
    /// registro tiene que seguir diciendo a dónde fue.
    ///
    /// No se valida el formato ni se pasa a minúsculas, igual que
    /// <c>Order.CustomerEmail</c>: aquí solo se copia lo que la saga mandó. Una
    /// foto copia, no corrige.
    /// </summary>
    public string Recipient { get; private set; }

    /// <summary>El asunto del email. Lo compone la factoría, nunca el consumer.</summary>
    public string Subject { get; private set; }

    /// <summary>
    /// El cuerpo del email. En una cancelación lleva dentro el <c>Reason</c> que
    /// arrastra <c>OrderCancelled</c> — que es el único sitio del sistema donde ese
    /// texto acaba delante de una persona.
    /// </summary>
    public string Body { get; private set; }

    /// <summary>Cuándo se "mandó". Siempre en UTC.</summary>
    public DateTimeOffset SentAt { get; private set; }

    /// <summary>
    /// El aviso de que el pedido salió bien.
    ///
    /// **Dos factorías en vez de un constructor público con cinco parámetros**, con
    /// el mismo criterio que <see cref="Notifications.Infrastructure.Entities"/>
    /// heredó de <c>Payment</c> en 3.5: son las que hacen imposible una fila con
    /// <c>Kind = Confirmation</c> y el texto de una cancelación dentro. El
    /// <c>Subject</c> y el <c>Body</c> los redacta la factoría y no quien llama —
    /// así el texto no puede divergir del <c>Kind</c>, que es la única incoherencia
    /// que esta tabla puede tener.
    /// </summary>
    public static Notification Confirmation(Guid orderId, string customerEmail)
    {
        var recipient = NormalizeRecipient(customerEmail);

        var subject = $"Tu pedido {orderId} está confirmado";

        var body =
            $"Hola,{Environment.NewLine}{Environment.NewLine}" +
            $"Tu pedido {orderId} se ha confirmado: hemos reservado el stock y el pago se ha " +
            $"completado correctamente.{Environment.NewLine}{Environment.NewLine}" +
            "Gracias por comprar en shop133.";

        return new Notification(orderId, NotificationKind.Confirmation, recipient, subject, Truncate(body));
    }

    /// <summary>
    /// El aviso de que el pedido no salió adelante, con el motivo dentro. Ver
    /// <see cref="Confirmation"/> para por qué son dos factorías y no un
    /// constructor.
    ///
    /// El <c>reason</c> llega de <c>OrderCancelled.Reason</c> y puede venir de los
    /// dos caminos de error indistintamente — es texto de diagnóstico, no un código
    /// que este método deba interpretar.
    /// </summary>
    public static Notification Cancellation(Guid orderId, string customerEmail, string reason)
    {
        var recipient = NormalizeRecipient(customerEmail);

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var subject = $"Tu pedido {orderId} no se ha podido completar";

        var body =
            $"Hola,{Environment.NewLine}{Environment.NewLine}" +
            $"Lo sentimos: tu pedido {orderId} se ha cancelado y no se te ha cobrado nada." +
            $"{Environment.NewLine}{Environment.NewLine}" +
            $"Motivo: {reason.Trim()}{Environment.NewLine}{Environment.NewLine}" +
            "Si crees que ha sido un error, vuelve a intentarlo desde la tienda.";

        return new Notification(orderId, NotificationKind.Cancellation, recipient, subject, Truncate(body));
    }

    private static string NormalizeRecipient(string customerEmail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerEmail);

        var trimmed = customerEmail.Trim();

        if (trimmed.Length > RecipientMaxLength)
        {
            throw new ArgumentException(
                $"La dirección del destinatario no puede superar los {RecipientMaxLength} caracteres.",
                nameof(customerEmail));
        }

        return trimmed;
    }

    /// <summary>
    /// Recorta el cuerpo en vez de lanzar, y es la única guarda del archivo que no
    /// revienta.
    ///
    /// El motivo: el <c>Reason</c> viene de <c>StockRejected</c>, que acumula un
    /// motivo por cada línea que no se pudo servir (3.4), así que su longitud la
    /// decide el tamaño del pedido y no nadie de este lado. Lanzar dejaría el
    /// mensaje en la cola de error y **al cliente sin aviso alguno** por un pedido
    /// grande — peor resultado que un email con el motivo cortado.
    ///
    /// No se le pone puntos suspensivos a propósito: quien lea la fila y vea
    /// exactamente <see cref="BodyMaxLength"/> caracteres sabe que hubo recorte.
    /// </summary>
    private static string Truncate(string body) =>
        body.Length <= BodyMaxLength ? body : body[..BodyMaxLength];

    // Sin Resend() y sin MarkAsRead(). No tienen llamante en 4.6: nadie relee esta
    // tabla todavía. Es el precedente de Product sin Update() hasta 1.3, Order sin
    // Confirm() hasta 4.3, StockItem sin Release() hasta 4.4 y Payment sin
    // Refund() — no se inventa la firma antes de que exista el caso de uso.
}
