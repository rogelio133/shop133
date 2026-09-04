namespace Shop133.Contracts.Events;

/// <summary>
/// Publicado por Inventory.API cuando ha devuelto las unidades que tenía
/// comprometidas para un pedido. Es la respuesta al comando ReleaseStock, y con
/// él la compensación deja de ser un disparo al aire.
///
/// Lo consume la saga, que estaba esperándolo en CompensatingStock: al recibirlo
/// pasa a Cancelled y publica OrderCancelled.
///
/// ── El décimo mensaje, y por qué se añadió en 4.4 ──
///
/// La decisión 1 de docs/fase_0_3.md fijó nueve mensajes de golpe para que la
/// saga no tuviera que tocar Contracts, y 4.3 se negó explícitamente a añadir
/// éste — "es una decisión de 4.4, con el consumer de la compensación delante".
/// Con el consumer delante, la respuesta es que sí, y el argumento que la decide
/// estaba escrito en el /// de OrderCancelled desde 0.3: allí se afirma que en
/// el camino de PaymentFailed "el stock ya se soltó con ReleaseStock". Sin este
/// evento esa frase es una promesa que la saga no puede cumplir — publicaría
/// OrderCancelled sin saber si la compensación llegó a ocurrir.
///
/// Su segundo efecto es que **CompensatingStock nace por fin como estado real**.
/// La regla que 4.2 dejó escrita y 4.3 aplicó por segunda vez es que hay un
/// estado por cada RESPUESTA que se espera, no por cada hecho que ocurre; este
/// evento es esa respuesta, así que el estado ya no se entraría y se saldría en
/// la misma transición.
///
/// ── Por qué solo lleva el OrderId ──
///
/// No lleva las líneas soltadas ni las cantidades. Su único consumidor es la
/// saga, a la que solo le hace falta saber *que* terminó para poder cerrar el
/// pedido; quien quiera el detalle lo tiene en InventoryDb, que es su dueño.
/// Mismo criterio que OrderConfirmed, y el contrario del de StockReserved.Amount
/// — que carga un dato ajeno solo porque su consumidor no tiene a quién
/// preguntárselo.
///
/// Tampoco lleva un motivo ni un resultado: no hay una versión "fallida" de este
/// evento. Si Inventory no puede soltar el stock, el mensaje va a
/// release-stock_error y la saga se queda esperando — visible, en vez de un
/// desenlace falso.
/// </summary>
public sealed record StockReleased
{
    public required Guid OrderId { get; init; }
}
