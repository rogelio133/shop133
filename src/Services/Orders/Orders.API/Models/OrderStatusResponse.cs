using Orders.Domain.Sagas;

namespace Orders.API.Models;

/// <summary>
/// Lo que devuelve <c>GET /orders/{id}/status</c>, el endpoint que el roadmap
/// nombra en 6.5 y que el <c>///</c> de <c>GetById</c> lleva prometiendo desde 2.3
/// (<i>"6.5 la amplía con lo que necesite la página de estado del pedido"</i>).
///
/// ── Por qué no vale <see cref="OrderResponse"/> ──
///
/// Aquel publica <c>Order.Status</c>, que son **tres** valores (2.1). Un pedido
/// pasa de <c>Pending</c> a <c>Confirmed</c> o <c>Cancelled</c> de un salto, así
/// que una página que solo lea eso no puede enseñar ninguna etapa intermedia: el
/// pedido está pendiente y de pronto está resuelto. Lo que ocurre en medio —validar
/// el precio, reservar el stock, cobrar, y a veces deshacer la reserva— vive en
/// <see cref="OrderState.CurrentState"/>, que son **nueve** desde 4.9.
///
/// ── La contradicción que este punto resuelve ──
///
/// El repositorio decía dos cosas distintas sobre de dónde saca 6.5 el estado:
///
/// - El <c>///</c> de <c>OrderStateMachine.Confirmed</c> justifica que los estados
///   terminales **no** sean <c>Finalize()</c> diciendo que la fila tiene que poder
///   consultarse después, y nombra expresamente <i>"lo que 6.5 querrá para la
///   página de estado del pedido"</i>.
/// - <c>OrderStateConfiguration</c>, en cambio, afirmaba que <i>"la página de
///   estado del pedido de 6.5 leerá Orders, no esto"</i>.
///
/// Gana el primero, y el segundo se corrige en su archivo. El motivo no es de
/// gusto: sin <c>CurrentState</c> la sugerencia de UX del roadmap —enseñar el
/// pedido avanzando por etapas <i>"para que el frontend refleje la naturaleza
/// asíncrona del backend en vez de ocultarla"</i>— es literalmente imposible de
/// entregar. Es la misma relectura con la que 4.4 corrigió la nota de 4.3.
///
/// ── Deliberadamente más ligero que OrderResponse ──
///
/// Sin líneas y sin total. Esto se sondea cada 2 s desde el navegador: devolver el
/// pedido entero en cada vuelta sería pagar el cuerpo completo por el único campo
/// que puede haber cambiado. Quien quiera el detalle tiene <c>GET /orders/{id}</c>,
/// que sigue existiendo y sin tocar.
/// </summary>
public sealed record OrderStatusResponse
{
    /// <summary>
    /// El mismo <c>Guid</c> de <c>OrderResponse.Id</c>, y también la clave de
    /// correlación de la saga: <c>Orders.Id</c> y <c>OrderStates.CorrelationId</c>
    /// son el mismo valor (decisión 5 de docs/fase_0_3.md).
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// El desenlace del **pedido**: <c>"Pending"</c>, <c>"Confirmed"</c> o
    /// <c>"Cancelled"</c>. Como texto y no como el número de la columna, por el
    /// mismo motivo que <c>OrderResponse.Status</c>.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// En qué punto va el **proceso**, por el nombre real del estado de la saga
    /// (<c>"PricingPending"</c>, <c>"CompensatingStock"</c>, …).
    ///
    /// **<c>null</c> es un valor legítimo y hay que pintarlo, no tratarlo como un
    /// error.** Significa que la fila de saga todavía no existe, y eso tiene una
    /// causa concreta: desde 4.5 el <c>OrderCreated</c> no sale hacia RabbitMQ en
    /// el <c>Publish</c> sino que se escribe en el outbox y se entrega un instante
    /// después, así que entre el 201 del alta y el arranque de la saga hay una
    /// ventana real. Con el broker parado esa ventana dura lo que dure la avería
    /// (medido en 4.5: el 201 llega igual, en 130 ms).
    ///
    /// Se devuelve el identificador de C# tal cual y **no** una etiqueta traducida:
    /// las etiquetas de interfaz las pone el frontend, y enseñar el nombre real es
    /// lo que hace que la página muestre la máquina de estados en vez de taparla.
    /// </summary>
    public string? Stage { get; init; }

    /// <summary>
    /// Por qué se canceló, cuando se canceló.
    ///
    /// Sale de <see cref="OrderState.CancellationReason"/> y no del pedido, porque
    /// <c>Order</c> no lo guarda: <c>Order.Cancel()</c> no recibe motivo desde 4.3,
    /// y su <c>///</c> dijo que una columna propia entraría <i>"si algún día la
    /// interfaz tiene que enseñarle al cliente por qué se canceló su pedido …
    /// entonces con su caso de uso delante"</i>. El caso de uso está delante y la
    /// respuesta es que **no hace falta la columna**: el texto ya está persistido a
    /// un <c>JOIN</c> de distancia, en la misma base y en la misma fila lógica.
    ///
    /// Hasta hoy ese motivo solo llegaba al correo de Notifications (4.6). Esta es
    /// la primera vez que se le puede enseñar a quien compró sin abrir el buzón.
    ///
    /// Puede ser <c>null</c> en un pedido cancelado, y no es un fallo: de los
    /// cuatro caminos que cancelan, los <c>StockRejected</c> que cancelan **en la
    /// misma transición** leen el motivo del mensaje que entra y nunca lo escriben
    /// en la instancia (lo explica el <c>///</c> de <c>CancellationReason</c>). La
    /// regla que separa los dos grupos es si la publicación ocurre en esa misma
    /// transición o en una posterior.
    /// </summary>
    public string? CancellationReason { get; init; }

    /// <summary>Cuándo se aceptó el pedido. De <c>Order</c>, no de la saga.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Si ya no va a cambiar. **Es lo único sobre lo que el sondeo actúa**, y por
    /// eso se calcula aquí y no en el cliente: la regla de cuándo un pedido está
    /// resuelto pertenece a quien es dueño de <c>OrderStatus</c>, y repartirla
    /// entre el servicio y cada cliente es cómo se acaba teniendo dos versiones.
    ///
    /// Mira <c>Status</c> y no <c>Stage</c> a propósito. Son dos relojes distintos:
    /// la saga llega a <c>Confirmed</c> y el pedido solo se mueve cuando el consumer
    /// de 4.3 procesa el <c>OrderConfirmed</c> que la saga publicó, un mensaje
    /// después. Quedarse con el de la saga daría por terminado un pedido que
    /// <c>GET /orders/{id}</c> todavía contesta como <c>Pending</c>.
    /// </summary>
    public required bool IsFinal { get; init; }

    // Sin método From, al contrario que OrderResponse y OrderItemResponse. No es
    // una omisión: este DTO no se construye desde una entidad, sino desde una
    // proyección que junta dos tablas que no tienen navegación entre ellas. El
    // mapeo vive dentro de la consulta de OrdersController.GetStatus, que es el
    // único sitio donde esas dos filas se encuentran — y tiene que vivir ahí para
    // que EF lo traduzca a SQL en vez de traerse las dos entidades enteras.
}
