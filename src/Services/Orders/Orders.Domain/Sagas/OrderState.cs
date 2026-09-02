using MassTransit;

namespace Orders.Domain.Sagas;

/// <summary>
/// La instancia de saga: lo que la <see cref="OrderStateMachine"/> recuerda de un
/// pedido entre un mensaje y el siguiente.
///
/// **No es el pedido.** <c>Order</c> vive en Entities/, se persiste en
/// <c>OrdersDb.Orders</c> desde 2.2 y su <c>Status</c> es lo que el cliente ve.
/// Esto de aquí es el estado del *proceso* que coordina a Inventory y a Payments,
/// y por eso sus estados no son los de <c>OrderStatus</c>: la decisión 2 de
/// docs/fase_2_1.md ya dejó escrito que <c>StockPending</c>, <c>PaymentPending</c>
/// y <c>CompensatingStock</c> "son estados de la *instancia de saga*, no del
/// pedido, y van en el tipo que persiste 4.5". Este es ese tipo.
///
/// En 4.1 no se persiste: el registro de Orders.API usa <c>InMemoryRepository()</c>
/// y todo esto se pierde al reiniciar el servicio. La tabla, su token de
/// concurrencia optimista y la pregunta de si comparte <c>OrdersDbContext</c> son
/// 4.5.
/// </summary>
public sealed class OrderState : SagaStateMachineInstance
{
    /// <summary>
    /// La clave de correlación, y **es el <c>OrderId</c>**. No hay conversión ni
    /// tabla de equivalencias: la decisión 5 de docs/fase_0_3.md descartó meter un
    /// <c>CorrelationId</c> en los contratos precisamente para que este campo y el
    /// <c>OrderId</c> del negocio fueran el mismo número. Quien lo iguala es la
    /// línea <c>CorrelateById(m => m.Message.OrderId)</c> de la máquina de estados.
    ///
    /// Medido en 3.3 y 3.4 contra el broker real: el sobre de los mensajes lleva
    /// <c>correlationId: null</c>. La correlación no viaja — se configura.
    /// </summary>
    public Guid CorrelationId { get; set; }

    /// <summary>
    /// En qué estado está la saga, por nombre.
    ///
    /// <c>string</c> y no <c>int</c>, que es la otra forma que admite
    /// <c>InstanceState</c>. El <c>int</c> ahorra espacio y obliga a declarar los
    /// estados en un orden que nadie puede tocar después — el mismo peligro de
    /// renumeración que documenta el enum <c>OrderStatus</c> en 2.1, pero aquí sin
    /// la ventaja de tener los números escritos a mano. Con <c>string</c>, la tabla
    /// que cree 4.5 se lee sin descifrar nada, que es lo que este proyecto valora.
    /// </summary>
    public string CurrentState { get; set; } = null!;

    /// <summary>
    /// A quién se le notifica el desenlace.
    ///
    /// Se captura en <c>Initially</c>, y no por comodidad: **después ya no vuelve a
    /// pasar por delante**. <c>OrderConfirmed</c> y <c>OrderCancelled</c> lo llevan
    /// dentro porque Notifications.API no puede leer <c>OrdersDb</c> (regla 1), y
    /// ni <c>StockRejected</c> ni <c>PaymentFailed</c> lo traen. Si no se guarda
    /// aquí, en 4.3 la saga no tiene a quién avisar de la cancelación.
    /// </summary>
    public string CustomerEmail { get; set; } = null!;

    /// <summary>
    /// Cuándo arrancó la saga. <c>DateTimeOffset</c> por el mismo motivo que
    /// <c>Order.CreatedAt</c>: mapea a <c>datetimeoffset</c> sin la ambigüedad de
    /// <c>Kind</c> que tiene <c>DateTime</c>.
    ///
    /// No es lo mismo que <c>Order.CreatedAt</c> aunque hoy disten milisegundos:
    /// uno dice cuándo se aceptó el pedido, este cuándo llegó el evento que arrancó
    /// el proceso. Con el outbox de 4.5 esos dos instantes pueden separarse de
    /// verdad.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Por qué se canceló el pedido, guardado para poder publicarlo más tarde.
    ///
    /// **Es la consecuencia no obvia del estado intermedio que estrena 4.4.** El
    /// camino corto —<c>StockRejected</c>— publica <c>OrderCancelled</c> en la
    /// misma transición en la que recibe el motivo, así que lo lee del mensaje que
    /// está entrando y no necesita nada de aquí; su comentario en la máquina de
    /// estados dice justamente eso. El camino largo ya no puede: el motivo llega
    /// en <c>PaymentFailed</c>, pero <c>OrderCancelled</c> no sale hasta que
    /// Inventory contesta <c>StockReleased</c> —una transición después— y ese
    /// evento no lleva ningún texto. Entre los dos mensajes hay que recordarlo.
    ///
    /// Que solo lo escriba un camino de los dos es deliberado y no una asimetría
    /// que haya que "arreglar": guardar también el de <c>StockRejected</c> sería
    /// un campo escrito para no leerse nunca.
    ///
    /// Inicializado a cadena vacía y no a <c>null!</c> como los dos de arriba:
    /// aquí sí existe un camino que llega a publicar sin haber pasado por el
    /// <c>.Then</c> que lo rellena (una instancia que alguien lleve a
    /// <c>CompensatingStock</c> por otra vía), y un email de 4.6 con un motivo
    /// vacío se lee mejor que una NullReferenceException.
    /// </summary>
    public string CancellationReason { get; set; } = string.Empty;

    // Fuera a propósito:
    //
    // - El importe. PaymentCompleted ya trae su Amount, así que guardarlo aquí
    //   sería un segundo sitio con el mismo número — el argumento por el que
    //   Order.Total se calcula y no se persiste (2.1).
    //
    // - Las líneas del pedido, y **4.4 lo dejó cerrado**: ReleaseStock perdió su
    //   Lines. La decisión 6 de docs/fase_3_4.md había dejado la pregunta abierta
    //   observando que la PK de StockReservations *es* el OrderId; con el consumer
    //   delante, eso resultó bastar. Guardarlas aquí habría significado que 4.5
    //   tuviera que persistir una colección en la fila de la saga (columna JSON o
    //   tipo owned) solo para devolverle a Inventory lo que Inventory ya tiene.
    //
    // - Un token de concurrencia optimista (rowversion / int Version). No tiene
    //   sentido sin repositorio persistente: es 4.5.

    // Setters públicos y sin constructor con invariantes, al contrario que Order,
    // OrderItem, Product o StockItem. No es un descuido de estilo: MassTransit
    // materializa esta instancia y la muta desde fuera (los .Then de la máquina de
    // estados escriben sobre context.Saga), así que un agregado que se construye
    // válido y se defiende no encaja aquí. Lo que protege la coherencia de este
    // tipo no son sus setters, es que solo la OrderStateMachine lo toca.
}
