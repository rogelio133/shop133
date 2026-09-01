using MassTransit;

using Microsoft.Extensions.Logging;

using Shop133.Contracts.Events;

namespace Orders.Domain.Sagas;

/// <summary>
/// La máquina de estados del pedido: el núcleo del proyecto y la razón de que
/// Orders tenga capa de dominio cuando Catalog, Inventory y Payments no la tienen
/// (decisión 1 de docs/fase_1_1.md, repetida en 3.4 y 3.5).
///
/// **Qué entrega 4.2 y qué no.** La cadena feliz entera, de punta a punta:
/// <c>OrderCreated → StockPending → PaymentPending → Confirmed</c>, y al llegar al
/// final la saga publica <c>OrderConfirmed</c> — el primer mensaje que emite en
/// todo el proyecto. Lo que sigue fuera: los caminos de error son 4.3, la
/// compensación <c>ReleaseStock</c> 4.4 y la persistencia 4.5.
///
/// **El pedido sigue naciendo <c>Pending</c> y nada lo mueve.** <c>Order.Status</c>
/// no se toca aquí: hacerlo necesita un consumer en Orders.API —el primero del
/// servicio— y, con él, la tabla <c>ProcessedMessages</c> de 3.6 en <c>OrdersDb</c>,
/// que es una migración entera. Va junto en 4.3, con los dos desenlaces. Mientras
/// tanto hay una inconsistencia temporal real y medible: la saga puede estar en
/// <c>Confirmed</c> mientras <c>GET /orders/{id}</c> contesta <c>"Pending"</c>.
///
/// **La saga observa la coreografía; no la orquesta.** Consume los mismos eventos
/// que ya vuelan desde la Fase 3 y solo emite lo que nadie más puede saber: desde
/// 4.2 el <c>OrderConfirmed</c> del final feliz, y más adelante el comando de
/// compensación (4.4) y el <c>OrderCancelled</c> de los caminos de error (4.3).
/// Ninguno de esos tres tiene otro autor posible, porque nadie más ve las dos
/// mitades del proceso a la vez. Inventory sigue
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
    // ── Los estados: tres, no los cinco que enumera el roadmap ──
    //
    // El punto 4.2 se titula "Submitted → StockPending → StockReserved →
    // PaymentPending → Confirmed", y dos de esos cinco no llegan a existir. No es
    // un recorte: es la consecuencia directa de la decisión 2 de 4.1 — la saga
    // OBSERVA la coreografía, no la orquesta, así que no manda ningún comando y no
    // hay nada que esperar entre "llegó OrderCreated" y "el stock está pedido", ni
    // entre "el stock está reservado" y "el pago está en curso". Esos dos serían
    // estados que se entran y se salen en la misma transición, que ninguna
    // instancia puede tener al consultarla.
    //
    // En una saga de orquestación sí existirían: Submitted sería "aceptado, aún no
    // he mandado ReserveStock" y StockReserved "reservado, aún no he mandado el
    // cobro". El hueco entre mandar el comando y recibir la respuesta es lo que da
    // sentido a un estado, y aquí ese hueco lo tiene otro servicio.
    //
    // 4.9 mete PricingPending DELANTE de StockPending sin que Submitted reaparezca:
    // será otro sitio donde la saga espera de verdad, esta vez la respuesta de
    // Catalog.

    /// <summary>
    /// El stock está pedido y todavía no hay respuesta. Destino de la primera
    /// transición desde 4.1 y sitio donde la saga espera el <c>StockReserved</c>
    /// que atiende este punto — o el <c>StockRejected</c> de 4.3.
    /// </summary>
    public State StockPending { get; private set; } = null!;

    /// <summary>
    /// El stock está reservado y el cobro en curso. **A partir de aquí existe
    /// estado que compensar**: es el punto donde la saga deja de ser reversible por
    /// sí sola, y por eso 4.4 tiene que publicar <c>ReleaseStock</c> si el pago se
    /// cae desde este estado.
    ///
    /// Se entra al recibir <c>StockReserved</c> y no al mandar nada: quien está
    /// cobrando es Payments.API, que consume ese mismo evento desde 3.5. La saga se
    /// entera a la vez que él, no antes.
    /// </summary>
    public State PaymentPending { get; private set; } = null!;

    /// <summary>
    /// Final feliz: stock reservado y cobro aceptado. Es donde se publica
    /// <c>OrderConfirmed</c>.
    ///
    /// **Es un estado normal, no <c>Finalize()</c>.** Finalizar sacaría la
    /// instancia del repositorio y, con <c>SetCompletedWhenFinalized()</c>,
    /// borraría su fila. Hoy no hay fila —el repositorio es en memoria— así que no
    /// se ahorra nada y se pierde poder inspeccionar el desenlace, que es
    /// justamente lo que hace verificable este punto. Cuando 4.5 cree la tabla, se
    /// decide con la tabla delante y con el coste real de acumular filas.
    /// </summary>
    public State Confirmed { get; private set; } = null!;

    /// <summary>
    /// El evento que arranca la saga. Es el mismo <c>OrderCreated</c> que
    /// <c>OrdersController</c> publica desde 3.3 e Inventory consume desde 3.4 —
    /// el contrato no cambia, que era el objetivo de la decisión 1 de
    /// docs/fase_0_3.md al fijar los 9 mensajes: "la saga de la Fase 4 no tendrá
    /// que tocar Contracts para existir".
    ///
    /// <c>StockRejected</c> y <c>PaymentFailed</c> siguen sin declararse, por lo
    /// mismo que 4.1 no declaraba ninguno: declarar un <c>Event&lt;T&gt;</c> hace
    /// que <c>ConfigureEndpoints</c> enlace su exchange a la cola de la saga, y sin
    /// un <c>During(...)</c> que lo atienda cada mensaje acabaría en
    /// <c>order-state_error</c>. Entran en 4.3, con el estado que los recibe.
    /// </summary>
    public Event<OrderCreated> OrderCreated { get; private set; } = null!;

    /// <summary>
    /// Lo publica Inventory.API desde 3.4 y lo consume Payments.API desde 3.5.
    /// Declararlo aquí añade un **segundo binding** a su exchange, igual que 4.1
    /// hizo con <c>OrderCreated</c>: dos consumidores del mismo fanout, no un
    /// relevo. Es la decisión 2 de 4.1 hecha visible por segunda vez.
    ///
    /// Su campo <c>Amount</c> no se mira. La saga podría usarlo para no depender de
    /// lo que traiga <c>PaymentCompleted</c>, pero guardarlo sería un segundo sitio
    /// con el mismo número — el motivo por el que <c>OrderState</c> no tiene
    /// importe y por el que <c>Order.Total</c> se calcula en vez de persistirse.
    /// </summary>
    public Event<StockReserved> StockReserved { get; private set; } = null!;

    /// <summary>
    /// Lo publica Payments.API desde 3.5 y **hasta hoy no lo consumía nadie**: su
    /// exchange existía con cero colas enlazadas, o sea publicándose al vacío, sin
    /// fallo y sin aviso. La verificación 6 de docs/fase_3_5.md lo dejó medido y
    /// era medio incumplimiento de la regla 7. Éste es el binding que lo cierra por
    /// el lado feliz; el de <c>PaymentFailed</c> lo pone 4.3.
    /// </summary>
    public Event<PaymentCompleted> PaymentCompleted { get; private set; } = null!;

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

        // Los dos eventos de 4.2, correlacionados igual: los tres contratos llevan
        // OrderId y ninguno lleva CorrelationId, así que la línea se repite tal cual.
        //
        // ── OnMissingInstance(m => m.Fault()), y NO es el comportamiento por
        //    defecto: se midió creyendo que sí ──
        //
        // Cuando llega un evento de este lado (no Initially) y no hay instancia
        // viva, MassTransit 8 lo **descarta en silencio**. No hay excepción, no hay
        // cola de error y no hay ni una línea de log: se comprobó reenviando un
        // PaymentCompleted después de reiniciar Orders.API, y el mensaje se
        // desvaneció (verificación 7 de docs/fase_4_2.md). Es lo contrario de lo
        // que se había supuesto al planificar el punto.
        //
        // Con InMemoryRepository() un reinicio borra todas las instancias —medido
        // en la verificación 7 de docs/fase_4_1.md—, así que un pedido que estaba
        // esperando su cobro se queda huérfano. En 4.1 eso era inocuo: el único
        // evento declarado era el que ARRANCA la saga, que simplemente empezaba de
        // cero. Desde 4.2 tiene consecuencia — el StockReserved o el
        // PaymentCompleted de ese pedido no tienen dónde caer.
        //
        // Con el descarte por defecto, ese pedido desaparece sin dejar rastro, que
        // es exactamente el agujero que 4.5 existe para cerrar. Esta línea lo pone
        // en order-state_error, donde se ve y se puede contar. Es la misma lección
        // que la guarda Ignore de 4.1: lo que hay que dejar escrito es lo que el
        // valor por defecto no hace.
        //
        // Cuando 4.5 persista la saga, esta línea deja de dispararse por reinicios
        // y pasa a señalar lo único que quedará: un evento de un pedido que nunca
        // existió. Merece la pena releerla entonces, no quitarla.
        Event(() => StockReserved, e =>
        {
            e.CorrelateById(message => message.Message.OrderId);
            e.OnMissingInstance(missing => missing.Fault());
        });

        Event(() => PaymentCompleted, e =>
        {
            e.CorrelateById(message => message.Message.OrderId);
            e.OnMissingInstance(missing => missing.Fault());
        });

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
        // Los Ignore(...) repartidos por los tres During de abajo son la guarda de
        // este consumer, y hacen el papel que en Inventory y Payments hace la tabla
        // ProcessedMessages de 3.6. Aquí esa tabla no aplica —la saga no tiene
        // DbContext hasta 4.5— y hay algo mejor: el propio estado ya distingue el
        // duplicado, porque un evento repetido llega a una instancia que ya pasó de
        // ese punto.
        //
        // Pero **explícitos, no por defecto**: el comportamiento de MassTransit ante
        // un evento no aceptado en el estado actual es faultear
        // (NotAcceptedStateMachineException → order-state_error), y un consumer que
        // revienta ante un duplicado no es idempotente. Se verificó quitándolos.
        //
        // Ojo al alcance: esto reconoce el mismo *pedido*, no la misma *entrega*.
        // Es la mitad de negocio de la guarda de 3.6, no la de transporte — que
        // aquí coincide, porque una reentrega trae el mismo OrderId.
        //
        // **La regla para no equivocarse al añadir estados**: en cada estado se
        // ignoran los eventos que ya se atendieron ANTES de llegar a él. Un evento
        // que se atiende DESPUÉS no se ignora nunca — ver la nota de PaymentPending
        // más abajo.

        During(StockPending,
            When(StockReserved)
                .Then(context => logger.LogInformation(
                    "Pedido {OrderId}: stock reservado por {Amount}; pasa a PaymentPending.",
                    context.Saga.CorrelationId,
                    context.Message.Amount))
                .TransitionTo(PaymentPending),

            // Duplicado de OrderCreated (la guarda que estrenó 4.1).
            Ignore(OrderCreated));

        // Nótese lo que **no** hay aquí: un Ignore(PaymentCompleted) en
        // StockPending. Sería fácil añadirlo "por simetría" y estaría mal: un cobro
        // aceptado sin haber visto la reserva no es un duplicado, es una entrega
        // fuera de orden, y RabbitMQ no ordena entre colas ni garantiza el orden con
        // entrega concurrente. Ignorarlo dejaría el pedido esperando para siempre un
        // PaymentCompleted que ya pasó; faultear lo pone en order-state_error, donde
        // se ve. Mismo criterio que el de OnMissingInstance de arriba: los agujeros
        // se miden, no se tapan.

        During(PaymentPending,
            When(PaymentCompleted)
                .Then(context => logger.LogInformation(
                    "Pedido {OrderId}: cobro aceptado por {Amount} (transacción {TransactionId}); " +
                    "pasa a Confirmed y se publica OrderConfirmed.",
                    context.Saga.CorrelationId,
                    context.Message.Amount,
                    context.Message.TransactionId))
                .TransitionTo(Confirmed)

                // **El primer mensaje que emite la saga en todo el proyecto.** Hasta
                // aquí solo observaba; los cinco eventos de la Fase 3 los publican
                // los servicios. Éste no tiene otro autor posible: nadie más sabe
                // que el pedido terminó bien, porque nadie más ve las dos mitades.
                //
                // El CustomerEmail sale de la instancia, y es donde se cobra la
                // decisión 6 de 4.1: PaymentCompleted no lo lleva, así que sin
                // aquella copia en el Initially no habría a quién avisar. Viaja
                // dentro del evento porque Notifications.API (4.6) no puede leer
                // OrdersDb — regla 1.
                //
                // .Publish(context => new T{...}) y no el .PublishAsync(context =>
                // context.Init<T>(...)) que sale en la mayoría de los ejemplos: la
                // sobrecarga simple existe y hace lo mismo. Init<T> solo hace falta
                // cuando hay que tocar el sobre (cabeceras propias, un TTL), y aquí
                // no hay nada que tocar — MassTransit acuña el MessageId y hereda el
                // ConversationId igual por los dos caminos, que es lo que permitirá
                // a Notifications deduplicar con la guarda de 3.6.
                //
                // Y se publica DENTRO de la transición, no en un consumer aparte: el
                // outbox de 4.5 es quien hará que este Publish y el cambio de estado
                // sean atómicos. Hoy no lo son, y es el mismo agujero de doble
                // escritura que 3.3 anotó en OrdersController.
                .Publish(context => new OrderConfirmed
                {
                    OrderId = context.Saga.CorrelationId,
                    CustomerEmail = context.Saga.CustomerEmail,
                }),

            Ignore(OrderCreated),
            Ignore(StockReserved));

        During(Confirmed,
            // El estado terminal también necesita sus guardas, y es el que más fácil
            // se olvida: aquí no queda ninguna transición que escribir, así que un
            // During(Confirmed, ...) parece código muerto. No lo es — sin él, una
            // reentrega tardía de cualquiera de los tres eventos manda a la cola de
            // error un pedido que terminó perfectamente.
            Ignore(OrderCreated),
            Ignore(StockReserved),
            Ignore(PaymentCompleted));
    }
}
