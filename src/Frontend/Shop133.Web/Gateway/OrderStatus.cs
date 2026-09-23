namespace Shop133.Web.Gateway;

/// <summary>
/// Lo que contesta <c>GET /api/orders/{id}/status</c>, el endpoint que 6.5 estrena en Orders.API.
///
/// **Declara los seis campos y no un subconjunto**, al reves que <see cref="PlacedOrder"/> y
/// <see cref="CatalogProduct"/>. No es una excepcion a aquel criterio, es el mismo criterio dando
/// otro resultado: aquellos re-declaran una parte porque el DTO de origen trae cosas que este
/// frontend no pinta, y este DTO de origen se diseno ya recortado —sin lineas y sin total—
/// precisamente porque se sondea cada dos segundos. Aqui no sobra nada.
///
/// ── Los dos campos que parecen lo mismo y no lo son ──
///
/// <see cref="Status"/> es el desenlace del PEDIDO y son tres valores. <see cref="Stage"/> es por
/// donde va el PROCESO y son nueve desde 4.9. Un pedido pasa de <c>Pending</c> a <c>Confirmed</c>
/// de un salto, asi que una pagina que solo leyera el primero no podria ensenar ninguna etapa
/// intermedia — que es justo lo que la sugerencia de UX del roadmap pide para este punto.
///
/// La traduccion de <see cref="Stage"/> a algo legible vive en
/// <see cref="Shop133.Web.Models.OrderProgress"/>, en este proyecto y no en el servicio: son
/// etiquetas de interfaz. Orders.API devuelve el identificador de C# tal cual.
/// </summary>
public sealed record OrderStatus
{
    public required Guid Id { get; init; }

    /// <summary><c>"Pending"</c>, <c>"Confirmed"</c> o <c>"Cancelled"</c>.</summary>
    public required string Status { get; init; }

    /// <summary>
    /// El nombre real del estado de la saga, o <c>null</c> si todavia no ha arrancado.
    ///
    /// **Ese null hay que pintarlo, no tratarlo como un fallo.** Desde 4.5 el evento que arranca
    /// la saga se escribe en el outbox y sale hacia RabbitMQ un instante despues, asi que entre el
    /// 201 del alta y la creacion de la instancia hay una ventana real — y con RabbitMQ caido,
    /// larga. Es exactamente el estado en el que esta un pedido cuando alguien llega a esta pagina
    /// recien salido del checkout.
    /// </summary>
    public string? Stage { get; init; }

    /// <summary>
    /// Por que se cancelo. **Es la primera vez que este texto llega al navegador**: hasta 6.5 solo
    /// salia en el correo de Notifications (4.6), porque el pedido no lo guarda —vive en la fila
    /// de la saga— y ningun endpoint lo exponia.
    ///
    /// Puede ser <c>null</c> en un pedido cancelado, y no es un fallo del servicio: de los caminos
    /// que cancelan, los que publican en la misma transicion en la que reciben el motivo lo leen
    /// del mensaje y nunca lo guardan. La pagina tiene que poder decir "cancelado" sin motivo.
    /// </summary>
    public string? CancellationReason { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Si ya no va a cambiar. **Es lo unico sobre lo que el sondeo actua**, y lo calcula el
    /// servicio a proposito: la regla de cuando un pedido esta resuelto pertenece a quien es dueno
    /// del estado, no a cada cliente.
    ///
    /// Mira <see cref="Status"/> y no <see cref="Stage"/>: la saga llega a <c>Confirmed</c> un
    /// mensaje antes de que el pedido lo haga, asi que pararse con la etapa dejaria la pagina
    /// ensenando "pendiente" sobre un pedido que se confirmo un instante despues.
    /// </summary>
    public required bool IsFinal { get; init; }
}
