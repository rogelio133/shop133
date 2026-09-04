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
/// **Desde 4.5 esto se persiste.** Hasta entonces el registro de Orders.API usaba
/// <c>InMemoryRepository()</c> y todo esto se perdia al reiniciar el servicio —
/// medido en la verificacion 7 de docs/fase_4_1.md, donde la saga no reconocia un
/// pedido que estaba esperando. Ahora vive en <c>OrdersDb.OrderStates</c>, dentro
/// del mismo <c>OrdersDbContext</c> que los pedidos, que es lo que permite que la
/// fila de la saga y el mensaje que ella publica entren en la misma transaccion.
/// El mapeo esta en <c>OrderStateConfiguration</c>, en Orders.Infrastructure: este
/// tipo no lleva ni un atributo de EF, porque Orders.Domain no puede referenciarlo
/// (regla 5).
/// </summary>
public sealed class OrderState : SagaStateMachineInstance
{
    /// <summary>
    /// Tope del nombre del estado. No es solo higiene: sin longitud declarada EF
    /// dejaria <c>nvarchar(max)</c> en una columna que solo guarda identificadores
    /// de C# como "PaymentPending". 64 sobra para cualquier estado que quepa en un
    /// nombre de propiedad.
    /// </summary>
    public const int CurrentStateMaxLength = 64;

    /// <summary>
    /// Tope del motivo de cancelacion, y el unico numero de este archivo que se
    /// eligio midiendo en vez de por gusto.
    ///
    /// Hoy solo escribe aqui el camino de <c>PaymentFailed</c> (ver el <c>///</c>
    /// de <see cref="CancellationReason"/>), y el texto que compone Payments es
    /// corto y de formato fijo. Pero el otro <c>Reason</c> que circula por el
    /// sistema —el de <c>StockRejected</c>— lo compone Inventory concatenando
    /// **una frase por linea que fallo**, y un pedido admite 50 lineas: del orden
    /// de 4.500 caracteres en el peor caso.
    ///
    /// 4000 no cubre ese peor caso y se elige igual, a sabiendas: ese texto no
    /// llega a esta columna por ningun camino que exista hoy. Lo que importa es
    /// que quede escrito que **si algun dia llega, SQL Server no trunca — lanza**
    /// (error 2628) y la saga acabaria en order-state_error. Queda anotado en la
    /// seccion Pendiente de docs/fase_4_5.md en vez de resolverse con un
    /// <c>nvarchar(max)</c> que escondiera la pregunta.
    /// </summary>
    public const int CancellationReasonMaxLength = 4000;


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

    /// <summary>
    /// El token de concurrencia optimista que 2.2, 4.1, 4.2, 4.3 y 4.4 llevaban
    /// prometiendo a este punto. Hasta 4.5 no significaba nada: sin repositorio
    /// persistente no hay fila que dos procesos puedan pisarse.
    ///
    /// Lo rellena SQL Server, no el codigo. <c>IsRowVersion()</c> lo mapea a una
    /// columna <c>rowversion</c> que el motor incrementa en cada <c>UPDATE</c>, y
    /// EF la mete en el <c>WHERE</c>: si otro mensaje del mismo pedido cambio la
    /// fila mientras este la tenia leida, el <c>UPDATE</c> afecta a cero filas y
    /// salta <c>DbUpdateConcurrencyException</c>. **Por eso el
    /// <c>UseMessageRetry</c> del Program.cs no es decoracion**: sin el, un choque
    /// legitimo manda el mensaje a order-state_error en vez de reintentarlo.
    ///
    /// *Descartado* el modo pesimista, que es el que MassTransit recomienda para
    /// SQL Server y no necesita esta propiedad: bloquea la fila al leerla con
    /// UPDLOCK/ROWLOCK. Es menos codigo y funciona, pero cambia un choque
    /// detectable por un bloqueo invisible, y este proyecto existe para que las
    /// carreras se vean. Ademas 8.2 pide expresamente "persistencia de la Saga en
    /// SQL Server con concurrencia optimista".
    ///
    /// *Descartado* un <c>int Version</c> con <c>[ConcurrencyCheck]</c>, la otra
    /// forma que admite MassTransit: obligaria a que alguien lo incremente, y el
    /// dia que un camino se olvide, la proteccion desaparece sin avisar. La
    /// <c>rowversion</c> no se puede olvidar porque no la escribe nadie.
    ///
    /// Inicializado a <c>[]</c> y no a <c>null!</c>: EF **rellena** el array que
    /// encuentra al materializar, igual que hace con las colecciones (la nota de
    /// 2.1 sobre el constructor sin parametros de <c>Order</c>).
    /// </summary>
    public byte[] RowVersion { get; set; } = [];

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
    //
    // (El token de concurrencia optimista estaba en esta lista hasta 4.5. Ya no:
    // es la propiedad RowVersion de arriba.)

    // Setters públicos y sin constructor con invariantes, al contrario que Order,
    // OrderItem, Product o StockItem. No es un descuido de estilo: MassTransit
    // materializa esta instancia y la muta desde fuera (los .Then de la máquina de
    // estados escriben sobre context.Saga), así que un agregado que se construye
    // válido y se defiende no encaja aquí. Lo que protege la coherencia de este
    // tipo no son sus setters, es que solo la OrderStateMachine lo toca.
}
