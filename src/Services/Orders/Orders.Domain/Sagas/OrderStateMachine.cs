using MassTransit;

using Microsoft.Extensions.Logging;

// El primer using de Commands en todo el proyecto, y llega en 4.4 con el único
// comando que la saga llega a mandar. ReserveStock sigue sin llamante — decisión
// 2 de docs/fase_4_1.md.
using Shop133.Contracts.Commands;
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
/// todo el proyecto.
///
/// **Qué añade 4.3.** Los dos caminos de error, con los que la saga pasa a tener
/// tres desenlaces posibles en vez de uno: <c>StockPending → Cancelled</c> al
/// recibir <c>StockRejected</c>, y <c>PaymentPending → Cancelled</c> al recibir
/// <c>PaymentFailed</c>. Los dos publican <c>OrderCancelled</c> arrastrando el
/// motivo. Con eso, <c>StockRejected</c> y <c>PaymentFailed</c> dejan de publicarse
/// al vacío —sus exchanges tenían cero colas ligadas desde 3.4 y 3.5— y un pedido
/// sin stock o con el cobro rechazado por fin termina en vez de quedarse esperando
/// para siempre.
///
/// **Qué añade 4.4, y es el punto por el que existe el proyecto.** La saga deja de
/// solo observar: al recibir <c>PaymentFailed</c> **envía** <c>ReleaseStock</c> a
/// Inventory y espera en <c>CompensatingStock</c> hasta que llegue el
/// <c>StockReleased</c> —contrato nuevo, el décimo— con el que Inventory confirma
/// que devolvió las unidades. Solo entonces publica <c>OrderCancelled</c>. Con eso
/// se cumple la regla 7 de CLAUDE.md: no queda ningún camino en el que el stock
/// reservado se filtre. Es también el primer y único <c>Send</c> del proyecto, y el
/// único estado que existe porque la saga mandó algo. Lo que sigue fuera: la
/// persistencia de la instancia es 4.5 y la validación de precios de Catalog,
/// 4.8/4.9.
///
/// **Y el pedido por fin se mueve.** Hasta 4.2 <c>Order.Status</c> se quedaba en
/// <c>Pending</c> aunque la saga llegara a <c>Confirmed</c>: la saga vive en
/// Orders.Domain y no puede tocar <c>OrdersDbContext</c> (regla 5), así que mover
/// el estado del pedido necesitaba un consumer en Orders.API —los dos primeros del
/// servicio— y, con ellos, la tabla <c>ProcessedMessages</c> de 3.6 en
/// <c>OrdersDb</c>. Ese bloque entra en 4.3 con estos dos caminos:
/// <c>OrderConfirmedConsumer</c> y <c>OrderCancelledConsumer</c> escuchan lo que
/// esta máquina publica y llaman a <c>Order.Confirm()</c>/<c>Order.Cancel()</c>.
/// La inconsistencia temporal no desaparece —sigue habiendo una ventana entre el
/// <c>TransitionTo</c> y el <c>UPDATE</c>—, pero deja de ser permanente.
///
/// **La saga observa la coreografía; no la orquesta.** Consume los mismos eventos
/// que ya vuelan desde la Fase 3 y solo emite lo que nadie más puede saber: desde
/// 4.2 el <c>OrderConfirmed</c> del final feliz, desde 4.3 el <c>OrderCancelled</c>
/// de los caminos de error y desde 4.4 el comando de compensación.
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
    // ── Los estados: cuatro, y ni el título de 4.2 ni el de 4.3 se cumplen al pie
    //    de la letra. Es la misma regla en los dos casos ──
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
    //
    // ── Y CompensatingStock, que 4.3 descartó y 4.4 resucita ──
    //
    // El punto 4.3 se titula "StockRejected → Cancelled / PaymentFailed →
    // CompensatingStock → Cancelled" y entregó solo Cancelled, por la misma regla:
    // no había ninguna respuesta que esperar. La saga todavía no mandaba
    // ReleaseStock y, aunque lo mandara, Shop133.Contracts no tenía ningún
    // StockReleased con el que Inventory pudiera contestar. El estado se habría
    // entrado y salido en la misma transición.
    //
    // 4.4 quita las dos condiciones a la vez: manda el comando **y** añade el
    // evento de respuesta, así que la espera es real y el estado se gana su sitio
    // por la misma regla que se lo negaba. Nótese que la regla no cambió — cambió
    // el mundo que describe. Ese es el motivo de haberla escrito.
    //
    // Lo que decidió que Inventory contestara, y no fue el gusto por la simetría:
    // el /// de OrderCancelled afirma desde 0.3 que en el camino de PaymentFailed
    // "el stock ya se soltó con ReleaseStock". Sin respuesta de Inventory esa
    // frase es una promesa que la saga no puede cumplir — publicaría la
    // cancelación sin saber si la compensación llegó a ocurrir. Ver el /// de
    // StockReleased.
    //
    // Lo que este estado NO trae, y hay que decirlo: un plazo. Si Inventory nunca
    // contesta, el pedido se queda aquí para siempre — no hay Schedule ni Request
    // con timeout. El agujero no tiene dueño en el roadmap y se anota en vez de
    // taparse, igual que los de OnMissingInstance y la doble escritura.

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
    /// El cobro se rechazó, se ha mandado <c>ReleaseStock</c> a Inventory y la saga
    /// espera su <c>StockReleased</c>. **Es el único estado del proyecto que existe
    /// porque la saga mandó algo**, y por tanto el único que se parece a los de una
    /// saga de orquestación.
    ///
    /// Es también el único sitio donde el pedido está a la vez cancelado de hecho y
    /// sin cancelar de derecho: <c>Order.Status</c> sigue en <c>Pending</c> porque
    /// <c>OrderCancelled</c> —lo que mueve la fila, vía el consumer de 4.3— no sale
    /// hasta salir de aquí. Esa ventana es la inconsistencia temporal del proyecto
    /// en su forma más larga, y es correcta: mientras el stock no esté suelto, el
    /// proceso no ha terminado.
    /// </summary>
    public State CompensatingStock { get; private set; } = null!;

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
    /// Final infeliz, y **el mismo para los dos caminos de error**: no había stock,
    /// o el cobro se rechazó. Es donde se publica <c>OrderCancelled</c>.
    ///
    /// Un solo estado para las dos causas y no un <c>StockRejected</c>/
    /// <c>PaymentDeclined</c> por separado: el desenlace del pedido es el mismo
    /// —terminó sin completarse— y quien quiera saber por qué lo lee en el
    /// <c>Reason</c> que viaja dentro del evento. Es la misma decisión que ya tomó
    /// 2.1 con <c>OrderStatus</c>, que tiene un único <c>Cancelled</c>. Dos estados
    /// obligarían a duplicar todas las guardas de idempotencia de abajo para no
    /// ganar ninguna transición distinta.
    ///
    /// **Lo que sí distingue a los dos caminos es lo que queda por deshacer**, y
    /// eso es lo que resolvió 4.4: desde <c>StockPending</c> no hay nada reservado
    /// que soltar y se llega aquí directo, desde <c>PaymentPending</c> sí lo hay y
    /// se llega **pasando por <c>CompensatingStock</c>**. Un solo estado final, dos
    /// rutas de distinta longitud. Hasta 4.3 las dos eran directas y el stock del
    /// segundo camino se quedaba reservado para siempre — el agujero de la regla 7,
    /// medido en la verificación de docs/fase_3_5.md.
    ///
    /// Estado plano y no <c>Finalize()</c>, por lo mismo que <c>Confirmed</c>.
    /// </summary>
    public State Cancelled { get; private set; } = null!;

    /// <summary>
    /// El evento que arranca la saga. Es el mismo <c>OrderCreated</c> que
    /// <c>OrdersController</c> publica desde 3.3 e Inventory consume desde 3.4 —
    /// el contrato no cambia, que era el objetivo de la decisión 1 de
    /// docs/fase_0_3.md al fijar los 9 mensajes: "la saga de la Fase 4 no tendrá
    /// que tocar Contracts para existir".
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
    /// El rechazo de la reserva, publicado por Inventory.API desde 3.4 cuando
    /// alguna línea no tiene unidades suficientes o el producto no existe en
    /// <c>InventoryDb</c>.
    ///
    /// **Aquí no hay nada que compensar** y por eso este camino es el corto: la
    /// reserva de Inventory es atómica —entra entera o no entra nada, verificado en
    /// la verificación 5 de docs/fase_3_4.md—, así que un <c>StockRejected</c>
    /// significa que ninguna unidad se movió. Se cancela y se acabó.
    ///
    /// Su <c>Reason</c> es el texto que Inventory compone con todas las líneas que
    /// fallaron, y viaja tal cual dentro de <c>OrderCancelled</c>: diagnóstico y
    /// material para el email de 4.6, nunca un código que nadie deba parsear.
    /// </summary>
    public Event<StockRejected> StockRejected { get; private set; } = null!;

    /// <summary>
    /// El rechazo del cobro, publicado por Payments.API desde 3.5.
    ///
    /// **Es el evento que justifica el proyecto entero**: llega cuando el stock ya
    /// está reservado, así que es el único de los seis que deja estado ajeno que
    /// deshacer. Desde 4.4 la saga no se limita a cancelar — manda
    /// <c>ReleaseStock</c> y espera en <c>CompensatingStock</c>. En 4.3 lo atendía
    /// sin soltar nada y las unidades se quedaban reservadas para un pedido ya
    /// cancelado; ése era el agujero de la regla 7, medido y no supuesto.
    /// </summary>
    public Event<PaymentFailed> PaymentFailed { get; private set; } = null!;

    /// <summary>
    /// La respuesta de Inventory.API al comando <c>ReleaseStock</c>: las unidades
    /// están devueltas. Contrato nuevo de 4.4 — el décimo, y el primero que se
    /// añade desde los nueve que fijó 0.3.
    ///
    /// **Es lo que convierte la compensación en un ida y vuelta.** Sin él la saga
    /// mandaría el comando y pasaría a <c>Cancelled</c> sin saber si llegó a
    /// ocurrir; con él, <c>OrderCancelled</c> solo sale cuando el stock está
    /// realmente suelto, que es lo que su propio <c>///</c> lleva afirmando desde
    /// 0.3.
    ///
    /// No hay un <c>StockReleaseFailed</c> que le haga pareja, al contrario que en
    /// los otros dos pares de este flujo. Si Inventory no puede soltar el stock,
    /// el mensaje se queda en <c>release-stock_error</c> y la saga espera aquí: un
    /// fallo de la compensación no es un desenlace del pedido, es una incoherencia
    /// que alguien tiene que mirar. Inventarle un evento de fracaso sería darle a
    /// la saga una forma de terminar fingiendo que soltó lo que no soltó.
    /// </summary>
    public Event<StockReleased> StockReleased { get; private set; } = null!;

    /// <summary>
    /// A dónde va <c>ReleaseStock</c>. Es el único destino escrito a mano en todo
    /// el proyecto y el precio de mandarlo con <c>Send</c> en vez de publicarlo:
    /// **Orders conoce el nombre de una cola de Inventory**.
    ///
    /// El nombre no es arbitrario — sale de <c>SetKebabCaseEndpointNameFormatter()</c>
    /// aplicado a <c>ReleaseStockConsumer</c> en Inventory.API, igual que
    /// <c>order-created</c> o <c>stock-reserved</c>. Y ahí está el riesgo, que
    /// conviene tener escrito: si alguien cambia el formateador allí, **esto no
    /// falla**. MassTransit crea la cola que se le nombre, así que los comandos se
    /// apilarían en una cola que nadie lee, sin error y sin aviso. Es el mismo modo
    /// de fallo silencioso que <c>ConfigureEndpoints</c> — y el motivo de que la
    /// verificación de 4.4 mire el broker y no solo los logs.
    ///
    /// *Descartado* <c>EndpointConvention.Map&lt;ReleaseStock&gt;(...)</c> en el
    /// Program.cs de Orders.API, que sacaría la dirección del dominio y la dejaría
    /// en la raíz de composición, que es donde conceptualmente pertenece. Es estado
    /// estático global de proceso: cada host de test tendría que acordarse de
    /// mapearlo o el <c>Send</c> sin URI lanza, y el fallo sería de configuración
    /// del test, no del código. Una constante con nombre se lee entera aquí.
    /// </summary>
    private static readonly Uri InventoryReleaseStockEndpoint = new("queue:release-stock");

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

        // Los dos de 4.3, con exactamente la misma configuración: los cinco
        // contratos que consume esta saga llevan OrderId y ninguno lleva
        // CorrelationId, así que el par de líneas se repite tal cual por quinta vez.
        //
        // Declararlos es lo que enlaza sus exchanges a la cola order-state, o sea
        // lo que hace que StockRejected y PaymentFailed dejen de publicarse al
        // vacío. Por eso 4.1 y 4.2 no los declararon: un Event<T> declarado sin un
        // During que lo atienda enlaza la cola igual y manda cada mensaje a
        // order-state_error. Ahora tienen quien los atienda.
        Event(() => StockRejected, e =>
        {
            e.CorrelateById(message => message.Message.OrderId);
            e.OnMissingInstance(missing => missing.Fault());
        });

        Event(() => PaymentFailed, e =>
        {
            e.CorrelateById(message => message.Message.OrderId);
            e.OnMissingInstance(missing => missing.Fault());
        });

        // El de 4.4, sexta y última repetición del mismo par de líneas. Que
        // StockReleased sea un contrato nuevo no cambia nada aquí: lleva OrderId y
        // no lleva CorrelationId, como los otros cinco.
        //
        // Su OnMissingInstance tiene un significado peor que el de los demás y vale
        // la pena verlo antes de que pase: si Orders.API se reinicia mientras un
        // pedido está en CompensatingStock, el InMemoryRepository pierde la
        // instancia y este evento va a order-state_error. **El stock sí se soltó**
        // —Inventory ya recibió el comando y trabajó— pero el pedido se queda en
        // Pending en OrdersDb para siempre, con su reserva marcada como liberada.
        // Es el mismo agujero que 4.5 cierra, ahora con una consecuencia visible en
        // dos bases de datos en vez de una.
        Event(() => StockReleased, e =>
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

            // ── El camino de error corto (4.3) ──
            //
            // Sin nada que compensar: la reserva de Inventory es atómica, así que
            // un rechazo significa que ninguna unidad se movió. De StockPending a
            // Cancelled directo.
            When(StockRejected)
                .Then(context => logger.LogInformation(
                    "Pedido {OrderId}: stock rechazado ({Reason}); pasa a Cancelled " +
                    "y se publica OrderCancelled. No hay nada que compensar.",
                    context.Saga.CorrelationId,
                    context.Message.Reason))
                .TransitionTo(Cancelled)

                // El Reason se arrastra tal cual del evento que originó la
                // cancelación. La saga no lo reescribe ni lo traduce a un código:
                // el /// de OrderCancelled dice que es texto de diagnóstico y
                // material para el email de 4.6, y quien mejor sabe por qué falló
                // es quien falló.
                //
                // Nótese que no se guarda en OrderState: se lee del mensaje que
                // está entrando, dentro de la misma transición. Guardarlo sería un
                // campo más en la instancia para un dato que solo se usa aquí.
                .Publish(context => new OrderCancelled
                {
                    OrderId = context.Saga.CorrelationId,
                    CustomerEmail = context.Saga.CustomerEmail,
                    Reason = context.Message.Reason,
                }),

            // Duplicado de OrderCreated (la guarda que estrenó 4.1).
            Ignore(OrderCreated));

        // Nótese lo que **no** hay aquí: ni Ignore(PaymentCompleted) ni
        // Ignore(PaymentFailed) en StockPending. Sería fácil añadirlos "por
        // simetría" y estaría mal: un cobro resuelto sin haber visto la reserva no
        // es un duplicado, es una entrega fuera de orden, y RabbitMQ no ordena
        // entre colas ni garantiza el orden con entrega concurrente. Ignorarlo
        // dejaría el pedido esperando para siempre una respuesta que ya pasó;
        // faultear lo pone en order-state_error, donde se ve. Mismo criterio que el
        // de OnMissingInstance de arriba: los agujeros se miden, no se tapan.

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

            // ── El camino de error largo (4.4), y el que da nombre a la fase ──
            //
            // Aquí sí hay estado ajeno que deshacer: el stock lleva reservado desde
            // que se entró en este estado. En 4.3 esta transición cancelaba y
            // avisaba sin soltar nada; ahora manda la compensación y **no cancela
            // todavía**. El pedido no termina hasta que Inventory conteste.
            //
            // El orden importa y no es el que parece: primero TransitionTo, después
            // Send. Las actividades se ejecutan en el orden en que se encadenan, así
            // que si el Send fuera antes, la respuesta de Inventory podría llegar
            // —el transporte en memoria de los tests entrega rapidísimo— con la
            // instancia todavía en PaymentPending, donde StockReleased no está
            // aceptado. Iría a order-state_error una de cada tantas veces, que es la
            // peor clase de fallo.
            When(PaymentFailed)
                .Then(context =>
                {
                    // Se guarda porque después ya no vuelve a pasar por delante:
                    // OrderCancelled sale una transición más tarde, al recibir
                    // StockReleased, y ese evento no lleva texto. Es el mismo
                    // razonamiento que el del CustomerEmail en Initially, y el
                    // motivo de que OrderState tenga un campo nuevo en 4.4.
                    context.Saga.CancellationReason = context.Message.Reason;

                    logger.LogWarning(
                        "Pedido {OrderId}: cobro rechazado ({Reason}); pasa a CompensatingStock " +
                        "y se envía ReleaseStock a {Endpoint}. El pedido NO se cancela hasta que " +
                        "Inventory conteste StockReleased.",
                        context.Saga.CorrelationId,
                        context.Message.Reason,
                        InventoryReleaseStockEndpoint);
                })
                .TransitionTo(CompensatingStock)

                // ── Send y no Publish, y es la única vez en todo el proyecto ──
                //
                // ReleaseStock es un comando: va dirigido a un destinatario concreto
                // y le pide que haga algo. Publicarlo funcionaría —Inventory se
                // ligaría al exchange por convención y Orders no sabría nombres de
                // colas ajenas— pero dejaría la puerta abierta a que un segundo
                // consumidor se ligase al mismo fanout y **soltara el stock dos
                // veces**, que es exactamente lo que el /// de ReleaseStock avisa
                // que es peor que un duplicado de ReserveStock. Con un exchange
                // fanout, añadir ese segundo consumidor no requiere tocar nada de
                // aquí ni de Inventory.
                //
                // El segundo motivo es que si no, la carpeta Commands/ no tendría
                // ninguna consecuencia observable: ReserveStock se quedó sin llamante
                // en 4.1, así que éste es el único comando que el proyecto llega a
                // mandar. La distinción evento/comando o se ve en el código o es
                // decoración.
                //
                // Solo lleva el OrderId: la PK de StockReservations *es* el OrderId,
                // así que Inventory lee de su propia tabla qué soltar. Ver el /// de
                // ReleaseStock, donde 4.4 cierra la pregunta que 3.2 y 3.4 dejaron
                // abierta.
                .Send(
                    InventoryReleaseStockEndpoint,
                    context => new ReleaseStock { OrderId = context.Saga.CorrelationId }),

            Ignore(OrderCreated),
            Ignore(StockReserved));

        // Y tampoco hay un Ignore(StockRejected) en PaymentPending. Llegar aquí
        // significa haber recibido StockReserved, e Inventory publica uno de los dos
        // eventos, nunca los dos: un StockRejected en este estado no es un duplicado
        // de nada, es Inventory contradiciéndose. Ignorarlo escondería un fallo
        // real del otro servicio.

        // ── La segunda mitad de la compensación (4.4) ──
        //
        // El único estado del proyecto al que se llega habiendo mandado algo, y por
        // tanto el único que espera de verdad. Todo lo demás de esta máquina observa
        // eventos que habrían volado igual sin ella.
        During(CompensatingStock,
            When(StockReleased)
                .Then(context => logger.LogInformation(
                    "Pedido {OrderId}: stock liberado por Inventory; pasa a Cancelled y se " +
                    "publica OrderCancelled ({Reason}). La compensación está completa.",
                    context.Saga.CorrelationId,
                    context.Saga.CancellationReason))
                .TransitionTo(Cancelled)

                // Aquí el Reason sale de la instancia y no del mensaje que entra,
                // al revés que en el camino de StockRejected — y esa asimetría es
                // justamente lo que cuesta el estado intermedio. StockReleased no
                // lleva texto porque no tiene ninguno que dar: quien sabe por qué se
                // canceló el pedido es Payments, y eso pasó una transición antes.
                .Publish(context => new OrderCancelled
                {
                    OrderId = context.Saga.CorrelationId,
                    CustomerEmail = context.Saga.CustomerEmail,
                    Reason = context.Saga.CancellationReason,
                }),

            // Las guardas de este estado, por la regla de siempre: se ignora lo que
            // ya se atendió ANTES de llegar aquí. El camino es OrderCreated →
            // StockReserved → PaymentFailed, así que son esos tres.
            Ignore(OrderCreated),
            Ignore(StockReserved),
            Ignore(PaymentFailed));

        // Y lo que deliberadamente NO se ignora en CompensatingStock, que es la
        // parte que hay que leer: ni PaymentCompleted ni StockRejected. Llegar aquí
        // implica que Payments ya contestó PaymentFailed y que Inventory ya contestó
        // StockReserved, y cada uno de los dos publica un evento o el otro, nunca
        // los dos. Un PaymentCompleted en este estado no es un duplicado — es
        // Payments diciendo que cobró un pedido que acaba de rechazar. Mismo
        // criterio literal que el de PaymentPending, tres líneas más arriba.

        During(Confirmed,
            // Los estados terminales también necesitan sus guardas, y son los que
            // más fácil se olvidan: aquí no queda ninguna transición que escribir,
            // así que un During(Confirmed, ...) parece código muerto. No lo es — sin
            // él, una reentrega tardía manda a la cola de error un pedido que
            // terminó perfectamente.
            //
            // Van los SEIS eventos, no solo los tres del camino recorrido: llegar a
            // Confirmed descarta que StockRejected o PaymentFailed sean parte de la
            // historia de este pedido, pero no impide que uno se reentregue tarde
            // —o que llegue reacuñado a mano, como en las pruebas de 3.6—, y el
            // resultado sería el mismo pedido perfecto en order-state_error.
            //
            // El Ignore(StockReleased) que añade 4.4 es, de los seis, el único
            // literalmente inalcanzable: al camino feliz no se le manda nunca
            // ReleaseStock, así que nadie puede contestarlo. Se pone igual porque la
            // disciplina de los estados terminales es deliberadamente roma —los
            // ignoran TODOS— y una excepción obligaría a razonar caso por caso cada
            // vez que se añade un evento, que es como se olvida uno.
            Ignore(OrderCreated),
            Ignore(StockReserved),
            Ignore(PaymentCompleted),
            Ignore(StockRejected),
            Ignore(PaymentFailed),
            Ignore(StockReleased));

        During(Cancelled,
            // El segundo estado terminal, con las mismas seis guardas y por el mismo
            // motivo. Este es más fácil de olvidar todavía, porque se llega a él por
            // dos caminos distintos y ninguno de los dos pasa por aquí al escribirlo.
            //
            // Aquí el Ignore(StockReleased) sí es imprescindible, y es el más obvio
            // de los seis en cuanto se ve de dónde se viene: CompensatingStock
            // desemboca justo aquí, así que una reentrega tardía del evento que
            // acaba de mover la saga cae exactamente en este estado. Es el caso
            // normal, no el raro.
            Ignore(OrderCreated),
            Ignore(StockReserved),
            Ignore(PaymentCompleted),
            Ignore(StockRejected),
            Ignore(PaymentFailed),
            Ignore(StockReleased));
    }
}
