namespace Shop133.Web.Models;

/// <summary>
/// En que punto esta una de las dos ramas del pedido.
///
/// El orden importa: la vista usa el valor para elegir el color y el icono, y <c>Done</c> va
/// despues de <c>Running</c> porque una rama solo avanza hacia adelante.
/// </summary>
public enum TrackState
{
    /// <summary>Todavia no se sabe nada. Solo se da antes de que arranque la saga.</summary>
    Waiting,

    /// <summary>En curso. Es el unico estado en el que la pagina anima algo.</summary>
    Running,

    /// <summary>Terminado bien.</summary>
    Done,

    /// <summary>Terminado mal. Es lo que hace que el pedido acabe cancelandose.</summary>
    Failed,

    /// <summary>
    /// Deshaciendo lo que ya se habia hecho. **Es la compensacion**, y tiene estado propio en vez
    /// de contarse como <c>Failed</c> porque no es lo mismo "esto fallo" que "esto se esta
    /// devolviendo": lo segundo es trabajo que todavia esta ocurriendo, y es literalmente el
    /// nucleo pedagogico del proyecto.
    /// </summary>
    Compensating,
}

/// <summary>
/// Una de las dos ramas del pedido, ya traducida a algo que se puede pintar.
/// </summary>
/// <param name="Title">El encabezado de la pista.</param>
/// <param name="Detail">Lo que esta pasando ahora mismo en ella.</param>
/// <param name="State">Para el color y el icono.</param>
public sealed record Track(string Title, string Detail, TrackState State);

/// <summary>
/// Traduce el <c>Stage</c> que devuelve Orders —el nombre real del estado de la saga— a lo que ve
/// una persona.
///
/// ── Por que son DOS pistas y no una barra ──
///
/// **Una sola barra de progreso seria mentira desde 4.9.** Hasta aquel punto la saga era una fila
/// india: validar, reservar, cobrar. 4.9 metio la validacion de precio de Catalog EN PARALELO con
/// la reserva de stock —las dos salen del mismo <c>OrderCreated</c>, por el mismo fanout— y de ahi
/// que la maquina de estados tenga nombres compuestos como
/// <c>PricingPendingStockReserved</c>: no son ruido, dicen **que rama ya contesto y cual falta**.
/// Aplanar eso en un porcentaje escondería exactamente lo que el roadmap pide ensenar de este
/// punto ("que el frontend refleje la naturaleza asincrona del backend en vez de ocultarla").
///
/// *Descartada* la barra lineal de tres pasos que sugiere el titulo del roadmap
/// (Reservando stock -> Procesando pago -> Confirmado). Es mas bonita y es falsa: con ella, un
/// pedido en <c>PricingPendingPaymentCompleted</c> —cobrado pero esperando a que Catalog diga si el
/// precio era autentico— se pintaria como "confirmado" un instante antes de cancelarse.
///
/// ── Y por que el mapa vive AQUI ──
///
/// Son etiquetas de interfaz. Orders.API devuelve el identificador de C# sin traducir, y traducirlo
/// alli meteria el vocabulario de la interfaz dentro de la maquina de estados — ademas de obligar a
/// desplegar un servicio para cambiar una palabra. El precio es que este archivo tiene que
/// enterarse cuando la saga gane un estado; sin el, un estado nuevo cae en el <c>default</c> y se
/// pinta con su nombre crudo, que es feo pero no miente.
/// </summary>
public static class OrderProgress
{
    /// <summary>
    /// Los nombres exactos de los nueve estados de <c>OrderStateMachine</c>, mas el caso de que
    /// todavia no haya instancia de saga.
    ///
    /// Son literales y no constantes compartidas, y no hay alternativa: <c>Shop133.Web</c> tiene
    /// CERO <c>ProjectReference</c> —lo vigila <c>Frontend_DoesNotReference_ServicesOrGateway</c>
    /// desde 0.6— asi que importar <c>OrderStateMachine</c> romperia la regla 3 en tiempo de
    /// compilacion. Es el mismo caso que <c>OrderItem.ProductSkuMaxLength</c> duplicando las
    /// constantes de <c>Product</c>, y con la misma consecuencia: **pueden divergir sin que nada
    /// avise**, y si divergen se cae en el <c>default</c>.
    /// </summary>
    public static (Track Pricing, Track Fulfilment) Tracks(string? stage) => stage switch
    {
        // Sin fila de saga. Es el estado real de un pedido que acaba de salir del checkout: el
        // evento que arranca la saga viaja por el outbox de 4.5 y tarda un instante.
        null => (
            new Track(PricingTitle, "Esperando a que arranque el pedido.", TrackState.Waiting),
            new Track(FulfilmentTitle, "Esperando a que arranque el pedido.", TrackState.Waiting)),

        // ── Catalog todavia no ha contestado ──
        "PricingPending" => (
            new Track(PricingTitle, "Comprobando que el precio que viste sigue siendo el bueno.", TrackState.Running),
            new Track(FulfilmentTitle, "Apartando las unidades del almacén.", TrackState.Running)),

        "PricingPendingStockReserved" => (
            new Track(PricingTitle, "Comprobando que el precio que viste sigue siendo el bueno.", TrackState.Running),
            new Track(FulfilmentTitle, "Unidades apartadas. Cobrando.", TrackState.Running)),

        // Cobrado y aun asi sin terminar: si Catalog rechaza el precio ahora, el pedido se cancela
        // con el cobro hecho. Esa es la unica situacion del sistema en la que hay un cargo que
        // nadie devuelve — 4.9 lo dejo anotado y sin dueno.
        "PricingPendingPaymentCompleted" => (
            new Track(PricingTitle, "Comprobando que el precio que viste sigue siendo el bueno.", TrackState.Running),
            new Track(FulfilmentTitle, "Cobrado. Falta la última comprobación.", TrackState.Done)),

        // ── Catalog ya valido ──
        "StockPending" => (
            new Track(PricingTitle, "Precio confirmado.", TrackState.Done),
            new Track(FulfilmentTitle, "Apartando las unidades del almacén.", TrackState.Running)),

        "PaymentPending" => (
            new Track(PricingTitle, "Precio confirmado.", TrackState.Done),
            new Track(FulfilmentTitle, "Unidades apartadas. Cobrando.", TrackState.Running)),

        // ── Catalog rechazo ──
        //
        // Este estado es el que desmiente el titulo que el roadmap le puso a 4.9 ("con nada que
        // compensar"): Inventory sigue reservando en paralelo, asi que hay que esperar su respuesta
        // antes de saber si queda algo que devolver.
        "CancellingStockPending" => (
            new Track(PricingTitle, "El precio ya no es válido.", TrackState.Failed),
            new Track(FulfilmentTitle, "Esperando al almacén para saber si hay algo que devolver.", TrackState.Compensating)),

        // La compensacion: la regla 7 en marcha. Se llega aqui por cinco caminos distintos.
        "CompensatingStock" => (
            new Track(PricingTitle, "El pedido no salió adelante.", TrackState.Failed),
            new Track(FulfilmentTitle, "Devolviendo las unidades al almacén.", TrackState.Compensating)),

        // ── Terminales ──
        "Confirmed" => (
            new Track(PricingTitle, "Precio confirmado.", TrackState.Done),
            new Track(FulfilmentTitle, "Unidades apartadas y cobro aceptado.", TrackState.Done)),

        "Cancelled" => (
            new Track(PricingTitle, "El pedido no salió adelante.", TrackState.Failed),
            new Track(FulfilmentTitle, "No queda nada apartado ni cobrado.", TrackState.Failed)),

        // Un estado que la saga tiene y este archivo no conoce. Se pinta con su nombre crudo en vez
        // de inventarle un texto: feo, pero honesto, y deja ver que hay que actualizar este mapa.
        _ => (
            new Track(PricingTitle, $"Estado desconocido: {stage}.", TrackState.Running),
            new Track(FulfilmentTitle, $"Estado desconocido: {stage}.", TrackState.Running)),
    };

    /// <summary>
    /// La clase de Bootstrap con la que se pinta cada estado. Vive aqui y no en la vista para que
    /// la vista no tenga un <c>switch</c> dentro de un bucle, y para que el JavaScript del sondeo y
    /// el render del servidor no puedan elegir colores distintos para lo mismo.
    /// </summary>
    public static string BadgeClass(TrackState state) => state switch
    {
        TrackState.Done => "text-bg-success",
        TrackState.Failed => "text-bg-danger",
        TrackState.Compensating => "text-bg-warning",
        TrackState.Running => "text-bg-primary",
        _ => "text-bg-secondary",
    };

    /// <summary>El nombre corto del estado, para la etiqueta.</summary>
    public static string Label(TrackState state) => state switch
    {
        TrackState.Done => "Listo",
        TrackState.Failed => "Fallido",
        TrackState.Compensating => "Deshaciendo",
        TrackState.Running => "En curso",
        _ => "Esperando",
    };

    /// <summary>
    /// El desenlace del PEDIDO —los tres valores de <c>OrderStatus</c>— traducido al titular y al
    /// color del aviso de arriba de la pagina.
    ///
    /// Esta separado de <see cref="Tracks"/> porque son dos cosas distintas que la pagina ensena a
    /// la vez: el desenlace es lo que le importa a quien compro, y las pistas son por donde ha
    /// pasado. Un pedido puede estar <c>Pending</c> con las dos pistas ya en verde —la saga llego a
    /// <c>Confirmed</c> y el consumer de 4.3 todavia no ha movido la fila—, y esa disonancia de
    /// unos milisegundos es real, no un fallo de esta traduccion.
    /// </summary>
    public static (string Title, string AlertClass) Outcome(string status) => status switch
    {
        "Confirmed" => ("Pedido confirmado", "alert-success"),
        "Cancelled" => ("Pedido cancelado", "alert-danger"),
        _ => ("Pedido en curso", "alert-info"),
    };

    /// <summary>
    /// Que servicios hay detras de cada pista. Se ensena en la pagina a proposito: es un proyecto
    /// para entender microservicios, y ver que "comprobando el precio" significa "Catalog contesto"
    /// es la mitad de lo que esta pagina existe para mostrar.
    /// </summary>
    private const string PricingTitle = "Precio (Catalog)";

    private const string FulfilmentTitle = "Stock y cobro (Inventory → Payments)";
}
