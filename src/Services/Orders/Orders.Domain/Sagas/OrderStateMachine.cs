using MassTransit;

using Microsoft.Extensions.Logging;

using Shop133.Contracts.Events;

namespace Orders.Domain.Sagas;

/// <summary>
/// La máquina de estados del pedido: el núcleo del proyecto y la razón de que
/// Orders tenga capa de dominio cuando Catalog, Inventory y Payments no la tienen
/// (decisión 1 de docs/fase_1_1.md, repetida en 3.4 y 3.5).
///
/// **Qué entrega 4.1 y qué no.** Solo el esqueleto: la instancia correlacionada y
/// la primera transición. El pedido sigue naciendo <c>Pending</c> y nada lo mueve;
/// <c>Order.Status</c> no se toca aquí todavía. La cadena feliz completa
/// (<c>StockReserved → PaymentPending → Confirmed</c>) es 4.2, los caminos de
/// error 4.3, la compensación <c>ReleaseStock</c> 4.4 y la persistencia 4.5.
///
/// **La saga observa la coreografía; no la orquesta.** Consume los mismos eventos
/// que ya vuelan desde la Fase 3 y solo emitirá el comando de compensación (4.4) y
/// los eventos terminales que consumirá Notifications.API (4.6). Inventory sigue
/// consumiendo <c>OrderCreated</c> por su cuenta y Payments <c>StockReserved</c>:
/// dos consumidores del mismo exchange fanout, no un relevo. Es lo que ya estaba
/// comprometido por escrito —la decisión 2 de docs/fase_3_2.md ("Payments consume
/// <c>StockReserved</c> en ambas fases") y la decisión 6 de docs/fase_3_4.md— y
/// tiene un precio que conviene decir en voz alta: **el comando
/// <c>ReserveStock</c> de 0.3 se queda sin usar.**
///
/// Descartada la orquestación pura (la saga manda <c>ReserveStock</c> e Inventory
/// deja de consumir <c>OrderCreated</c>): usaría ese noveno mensaje, pero obliga a
/// cambiar el consumer de Inventory —un cambio en otro servicio que la Fase 4 del
/// roadmap no contempla— y contradice dos decisiones ya escritas. La diferencia
/// pedagógica es pequeña; el coste, no.
/// </summary>
public sealed class OrderStateMachine : MassTransitStateMachine<OrderState>
{
    /// <summary>
    /// El stock está pedido y todavía no hay respuesta. Único estado de 4.1: es el
    /// destino de la primera transición y el sitio donde la saga espera el
    /// <c>StockReserved</c>/<c>StockRejected</c> que atenderán 4.2 y 4.3.
    /// </summary>
    public State StockPending { get; private set; } = null!;

    /// <summary>
    /// El evento que arranca la saga. Es el mismo <c>OrderCreated</c> que
    /// <c>OrdersController</c> publica desde 3.3 e Inventory consume desde 3.4 —
    /// el contrato no cambia, que era el objetivo de la decisión 1 de
    /// docs/fase_0_3.md al fijar los 9 mensajes: "la saga de la Fase 4 no tendrá
    /// que tocar Contracts para existir".
    ///
    /// Los otros cuatro eventos de la coreografía **no se declaran todavía**, y no
    /// es olvido: declarar un <c>Event&lt;T&gt;</c> hace que
    /// <c>ConfigureEndpoints</c> enlace su exchange a la cola de la saga, y sin un
    /// <c>During(...)</c> que lo atienda cada mensaje acabaría en
    /// <c>order-state_error</c>. Se declaran cuando haya un estado que los reciba.
    /// </summary>
    public Event<OrderCreated> OrderCreated { get; private set; } = null!;

    /// <summary>
    /// El logger llega por constructor: MassTransit resuelve la máquina de estados
    /// del contenedor (<c>AddSagaStateMachine</c> la registra como singleton), así
    /// que la inyección funciona igual que en los consumers de Inventory y
    /// Payments.
    ///
    /// Descartado <c>LogContext.Info?.Log(...)</c>, que es el modismo de MassTransit
    /// y no necesitaría constructor. Se prefiere el <c>ILogger&lt;T&gt;</c> porque
    /// es lo que ya hacen los dos consumers del proyecto, y tener dos formas de
    /// registrar la misma clase de suceso obliga a explicar la diferencia cada vez.
    /// </summary>
    public OrderStateMachine(ILogger<OrderStateMachine> logger)
    {
        // Dónde se guarda el nombre del estado. Sin esta línea la máquina
        // funciona pero la instancia no recuerda en qué estado está, que es
        // precisamente lo único que una instancia de saga existe para recordar.
        InstanceState(saga => saga.CurrentState);

        // **La línea que 0.3 prometió en su decisión 5 y sin la cual no hay saga.**
        // Ningún mensaje de Shop133.Contracts lleva CorrelationId: se descartó a
        // propósito para no tener dos fuentes de verdad al lado de un OrderId que
        // siempre valdría lo mismo. El precio era esta línea de configuración, y
        // aquí se paga. Medido en 3.3 y 3.4 contra el broker real: el sobre viaja
        // con correlationId null.
        //
        // CorrelateById iguala OrderState.CorrelationId con OrderCreated.OrderId,
        // así que la clave primaria de la instancia *es* el id del pedido.
        Event(() => OrderCreated, e => e.CorrelateById(message => message.Message.OrderId));

        Initially(
            When(OrderCreated)
                .Then(context =>
                {
                    // Se copia el email porque después ya no vuelve a pasar por
                    // delante: StockRejected y PaymentFailed no lo llevan, y
                    // OrderCancelled sí tiene que llevarlo (Notifications.API no
                    // puede leer OrdersDb — regla 1).
                    context.Saga.CustomerEmail = context.Message.CustomerEmail;
                    context.Saga.CreatedAt = DateTimeOffset.UtcNow;

                    logger.LogInformation(
                        "Saga arrancada para el pedido {OrderId} de {CustomerEmail}; pasa a StockPending.",
                        context.Saga.CorrelationId,
                        context.Saga.CustomerEmail);
                })
                .TransitionTo(StockPending));

        // ── Idempotencia (regla 6 de CLAUDE.md) ──
        //
        // Esta línea es la guarda de este consumer, y hace el papel que en
        // Inventory y Payments hace la tabla ProcessedMessages de 3.6. Aquí esa
        // tabla no aplica —la saga no tiene DbContext hasta 4.5— y hay algo mejor:
        // el propio estado ya distingue el duplicado, porque un segundo
        // OrderCreated del mismo pedido llega a una instancia que ya está en
        // StockPending.
        //
        // Pero **explícita, no por defecto**: sin esta línea el comportamiento de
        // MassTransit ante un evento no aceptado en el estado actual es faultear
        // (NotAcceptedStateMachineException → order-state_error), y un consumer que
        // revienta ante un duplicado no es idempotente. Se verificó quitándola.
        //
        // Ojo al alcance: esto reconoce el mismo *pedido*, no la misma *entrega*.
        // Es la mitad de negocio de la guarda de 3.6, no la de transporte — que
        // aquí coincide, porque una redelivery trae el mismo OrderId.
        During(StockPending, Ignore(OrderCreated));
    }
}
