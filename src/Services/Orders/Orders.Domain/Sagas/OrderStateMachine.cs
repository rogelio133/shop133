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
/// único estado que existe porque la saga mandó algo.
///
/// **Qué añade 4.5, sin tocar ni una línea de esta clase.** La instancia deja de
/// vivir en memoria y pasa a <c>OrdersDb.OrderStates</c>, con token de concurrencia
/// optimista, y lo que esta máquina publica o envía se escribe en el outbox dentro
/// de la misma transacción que su cambio de estado. Que no haya habido que cambiar
/// nada aquí es el resultado, no la casualidad: la persistencia y la entrega
/// fiable son decisiones de la composición (el <c>AddMassTransit</c> de
/// Orders.API), no del diseño del proceso. Lo único que se reescribió fueron tres
/// comentarios que prometían este punto — el de <c>OnMissingInstance</c>, el del
/// <c>Publish</c> de <c>OrderConfirmed</c> y el <c>Finalize()</c> de
/// <c>Confirmed</c>. Lo que sigue fuera: la validación de precios de Catalog,
/// 4.8/4.9.
///
/// **Qué añade 4.9, y es la primera rama PARALELA de la saga.** Los dos eventos
/// que Catalog.API publica desde 4.8 —<c>OrderPricingValidated</c> y
/// <c>OrderPricingRejected</c>, que hasta hoy se publicaban al vacío— pasan a
/// consumirse aquí, y con ellos entra <c>PricingPending</c> **delante** de
/// <c>StockPending</c>. Hasta 4.8 esta máquina era una cadena: un evento, una
/// respuesta, un estado. Ahora, desde el <c>Initially</c>, hay **dos respuestas
/// pendientes a la vez** —la de Catalog y la de Inventory— porque Inventory sigue
/// consumiendo <c>OrderCreated</c> del mismo exchange fanout (decisión 2 de 4.1,
/// sin cambios). Eso es una unión (*join*), y es lo que obliga a los tres estados
/// nuevos: <c>PricingPending</c>, <c>PricingPendingStockReserved</c> y
/// <c>CancellingStockPending</c>.
///
/// **El título de 4.9 en el roadmap es falso, y este punto lo desmiente.** Dice
/// "<c>OrderPricingRejected → Cancelled</c> sin nada que compensar". El <c>///</c>
/// de <c>OrderPricingRejected</c> ya avisó en 4.8 de que podía serlo y encargó
/// releerlo con la máquina de estados delante; releído: **un rechazo de precio
/// puede llegar con el stock ya reservado**, y entonces sí hay algo que compensar.
/// Es el mismo movimiento con el que 4.4 corrigió la nota de 4.3 sobre
/// <c>CompensatingStock</c>. El resultado es que <c>OrderPricingRejected</c> **no
/// lleva nunca a <c>Cancelled</c> directamente**: o manda la compensación, o espera
/// a saber si hay algo que compensar.
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
    // es otro sitio donde la saga espera de verdad, esta vez la respuesta de
    // Catalog. La regla se cumple; lo que 4.2 no podía prever es que esa espera
    // fuese SIMULTÁNEA a la de Inventory (ver el bloque del join, más abajo).
    //
    // ── 4.9 y la regla, cuando hay DOS esperas a la vez ──
    //
    // "Un estado por cada respuesta que se espera" describe una cadena, y desde
    // 4.9 esto ya no lo es. Inventory consume OrderCreated del mismo fanout que
    // Catalog, así que en cuanto arranca la saga hay dos respuestas pendientes en
    // paralelo y ninguna causa a la otra. La regla generalizada es la que se
    // aplica aquí: **un estado por cada CONJUNTO de respuestas que siguen
    // pendientes**, más lo que ya se sabe del desenlace. De ahí salen cuatro estados
    // nuevos y ni uno más:
    //
    //   PricingPending                 falta Catalog; Inventory sin contestar
    //   PricingPendingStockReserved    falta Catalog; Inventory reservó
    //   PricingPendingPaymentCompleted falta Catalog; reservado Y COBRADO
    //   StockPending                   falta Inventory; Catalog validó
    //   CancellingStockPending         falta Inventory; Catalog RECHAZÓ
    //
    // El tercero es el que no estaba previsto y lo impuso una medición: la rama de
    // Inventory no acaba en el StockReserved, porque Payments consume ese mismo
    // evento del fanout y cobra sin esperar a nadie. Contra el compose real esa
    // rama entera se adelantó a Catalog en la mitad de los pedidos. Ver su ///.
    //
    // Los cruces que NO necesitan estado, y conviene ver por qué:
    //
    // - Inventory rechaza (esté el precio como esté): el pedido está perdido y no
    //   hay nada reservado que soltar, así que se cancela sin esperar a Catalog. La
    //   respuesta que llegue tarde cae en Cancelled, donde se ignora. Es seguro
    //   porque validar precios **no escribe nada de negocio** —lo dice el /// de
    //   OrderCreatedPricingConsumer, y es la razón de que su idempotencia saliera
    //   distinta en 4.8—, así que descartarla no deja rastro que limpiar.
    //
    // - El cobro se rechaza con el precio sin saber: condena el pedido diga lo que
    //   diga Catalog, y hay stock que devolver, así que se va directo a
    //   CompensatingStock. Otra respuesta que llega tarde y se ignora allí.
    //
    // *Descartado* modelar el join con un `bool StockIsReserved` en OrderState y
    // ramificar con .IfElse(...): son 7 estados en vez de 8, pero cuesta una
    // migración de OrdersDb (columna bit) y estrena los primeros condicionales de
    // esta máquina. Con estados no hay nada que decidir en tiempo de ejecución y,
    // sobre todo, **la fila de OrderStates dice por sí sola dónde está un pedido en
    // paralelo** — que es exactamente por lo que CurrentState es string y no int
    // (2.1) y por lo que los estados terminales no son Finalize() (4.5). Un bit en
    // otra columna habría que cruzarlo a mano para leer lo mismo.
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
    /// **El destino de la primera transición desde 4.9**, en el sitio que ocupaba
    /// <c>StockPending</c> desde 4.1. No ha contestado nadie: ni Catalog con su
    /// validación de precios (4.8) ni Inventory con su reserva (3.4).
    ///
    /// El nombre dice solo la mitad y es deliberado: la espera que este punto
    /// *añade* es la de Catalog, y la de Inventory ya existía. Lo que hace que las
    /// dos quepan en un estado es que aquí todavía no se sabe nada de ninguna —
    /// en cuanto una contesta, el estado cambia para decir cuál falta.
    ///
    /// **Es donde espera un pedido con Catalog caído**, y eso es lo que 4.8 compró:
    /// el <c>POST /orders</c> sigue devolviendo <c>201</c> y el pedido se queda
    /// aquí hasta que Catalog vuelva. Un retraso, no un <c>502</c> — lo que ganó la
    /// Fase 3 no se devuelve. El precio, y es el tercer hueco sin dueño de esta
    /// clase junto al de <c>CompensatingStock</c> y el de <c>OnMissingInstance</c>:
    /// **no hay plazo**. Si Catalog no contesta nunca, el pedido se queda aquí para
    /// siempre.
    /// </summary>
    public State PricingPending { get; private set; } = null!;

    /// <summary>
    /// Inventory ya reservó; falta la respuesta de Catalog. La otra mitad del join,
    /// y el estado que hace visible que las dos validaciones corren en paralelo.
    ///
    /// **No se puede pasar de aquí a <c>PaymentPending</c> sin la respuesta de
    /// Catalog**, aunque el stock ya esté apartado: hacerlo sería dar por bueno el
    /// precio que 4.8 existe para comprobar. Y **no se puede cancelar sin más** si
    /// esa respuesta es un rechazo: desde aquí hay unidades reservadas, así que se
    /// va a <c>CompensatingStock</c> como desde <c>PaymentPending</c>.
    ///
    /// Ese segundo camino es el que desmiente el título de 4.9 en el roadmap.
    ///
    /// **Y este estado tiene que aceptar además los dos eventos de cobro**, porque
    /// Payments consume <c>StockReserved</c> del mismo fanout y cobra sin esperar a
    /// nadie: la rama de Inventory puede completarse entera antes de que Catalog
    /// conteste. Medido, no supuesto — ver el <c>///</c> de
    /// <see cref="PricingPendingPaymentCompleted"/>.
    /// </summary>
    public State PricingPendingStockReserved { get; private set; } = null!;

    /// <summary>
    /// El stock está reservado, **el cobro ya se aceptó** y Catalog todavía no ha
    /// contestado. La rama de Inventory terminó entera antes que la de Catalog.
    ///
    /// ── Este estado existe por una medición, y la medición sorprendió ──
    ///
    /// Al planificar 4.9 se dio por hecho que esta inversión sería rara: exige que
    /// una lectura de Catalog tarde más que dos escrituras encadenadas
    /// (Inventory + Payments). Contra el compose real resultó ser **la mitad de los
    /// pedidos**: de siete legítimos, cuatro los ganó Inventory y tres Catalog. Sin
    /// este estado, esos pedidos dependían de que el <c>UseMessageRetry</c> del
    /// <c>Program.cs</c> reordenara la entrega dentro de su ventana de ~500 ms — o
    /// sea de que Catalog no se retrasara, que es exactamente lo que no se puede
    /// suponer de un servicio que puede caerse.
    ///
    /// **La regla que separa este caso del que sí se deja faultear**: una inversión
    /// *estructural* —dos ramas paralelas que pueden terminar en cualquier orden—
    /// se modela con un estado; un reordenamiento de *entrega* dentro de una misma
    /// rama causal se deja al reintento. Ver la nota de <c>PricingPending</c> sobre
    /// <c>PaymentCompleted</c>.
    ///
    /// **El agujero que este estado hace visible, y que no tiene dueño**: si Catalog
    /// rechaza desde aquí, el pedido ya está cobrado. La saga suelta el stock, pero
    /// **no existe ningún contrato de devolución** en el proyecto — el
    /// <c>TransactionId</c> que <c>PaymentCompleted</c> lleva desde 0.3 "para poder
    /// emitir el reembolso" sigue sin nadie que lo use. Se registra en el log con
    /// toda la letra en vez de esconderlo.
    /// </summary>
    public State PricingPendingPaymentCompleted { get; private set; } = null!;

    /// <summary>
    /// Catalog rechazó el precio y **todavía no se sabe si hay algo que soltar**:
    /// Inventory no ha contestado. El pedido está perdido de hecho, pero la saga no
    /// puede terminarlo hasta saber qué hizo Inventory con él.
    ///
    /// Es el estado que el título de 4.9 da por innecesario, y las dos alternativas
    /// están descartadas por motivos concretos:
    ///
    /// *Descartado* cancelar aquí mismo e ignorar el <c>StockReserved</c> que
    /// llegue después: la reserva de Inventory no se soltaría nunca. Es la regla 7
    /// de CLAUDE.md rota, y rota **en silencio** — el pedido quedaría
    /// perfectamente <c>Cancelled</c> con sus unidades apartadas para siempre, que
    /// es exactamente el agujero que 4.4 cerró por el otro camino.
    ///
    /// *Descartado* mandar <c>ReleaseStock</c> a ciegas sin esperar: el
    /// <c>ReleaseStockConsumer</c> de Inventory **lanza si no encuentra fila de
    /// reserva**, y eso es una decisión deliberada de 4.4 (soltar lo que nunca se
    /// apartó crea unidades de la nada). El comando acabaría en
    /// <c>release-stock_error</c> y la saga esperaría en <c>CompensatingStock</c>
    /// un <c>StockReleased</c> que no va a llegar — cambiar una espera por otra
    /// peor.
    ///
    /// El mismo hueco que <c>PricingPending</c> y por la misma razón: no hay plazo.
    /// </summary>
    public State CancellingStockPending { get; private set; } = null!;

    /// <summary>
    /// El stock está pedido y todavía no hay respuesta. Destino de la primera
    /// transición entre 4.1 y 4.8, y sitio donde la saga espera el
    /// <c>StockReserved</c> de 4.2 — o el <c>StockRejected</c> de 4.3.
    ///
    /// **Desde 4.9 se llega aquí desde <c>PricingPending</c>**, al validar Catalog
    /// el precio, y no desde el <c>Initially</c>. Lo que el estado significa no
    /// cambió; lo que cambió es que llegar a él ya afirma algo más: que la foto de
    /// precios del pedido es auténtica.
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
    ///
    /// **Y desde 4.9 eso tiene una consecuencia incómoda que conviene decir en voz
    /// alta: la validación de precios NO llega a ser una puerta.** Payments cobra
    /// en cuanto ve el <c>StockReserved</c> del fanout, sin esperar a esta saga ni
    /// a Catalog, así que un pedido con precio falso puede quedar cobrado antes de
    /// que el rechazo de Catalog llegue. Lo que 4.9 garantiza no es que no se cobre
    /// —eso exigiría que Payments consumiera otra cosa, o sea cambiar el consumer
    /// de otro servicio, que es justo lo que la decisión 2 de 4.1 descartó— sino
    /// que **el pedido acaba cancelado y el stock devuelto**. Un veto con
    /// compensación, no una autorización previa.
    ///
    /// Se llega aquí por dos caminos desde 4.9, según quién contestara primero:
    /// desde <c>StockPending</c> con el <c>StockReserved</c>, o desde
    /// <c>PricingPendingStockReserved</c> con el <c>OrderPricingValidated</c>. Los
    /// dos significan lo mismo —precio auténtico y stock apartado— y por eso
    /// desembocan en el mismo sitio.
    /// </summary>
    public State PaymentPending { get; private set; } = null!;

    /// <summary>
    /// Hay stock reservado que hay que devolver: se ha mandado <c>ReleaseStock</c> a
    /// Inventory y la saga espera su <c>StockReleased</c>. **Es el único estado del
    /// proyecto que existe porque la saga mandó algo**, y por tanto el único que se
    /// parece a los de una saga de orquestación.
    ///
    /// **Desde 4.9 se llega por TRES caminos con dos historias distintas**, y eso
    /// tiene consecuencias en sus guardas de idempotencia (ver el <c>During</c>
    /// correspondiente). Hasta 4.8 solo se venía de <c>PaymentPending</c> con un
    /// <c>PaymentFailed</c>: el cobro se rechazó. Ahora también se viene de
    /// <c>PricingPendingStockReserved</c> y de <c>CancellingStockPending</c>, las
    /// dos con un <c>OrderPricingRejected</c> y **sin que se haya cobrado nada** —
    /// el precio era falso. Lo que comparten los tres, que es lo único que este
    /// estado necesita saber: el pedido está perdido y hay unidades apartadas.
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
    /// **Es un estado normal, no <c>Finalize()</c>, y 4.5 lo confirma con la tabla
    /// delante.** Finalizar sacaría la instancia del repositorio y, con
    /// <c>SetCompletedWhenFinalized()</c>, borraría su fila de
    /// <c>OrdersDb.OrderStates</c>.
    ///
    /// En 4.2 el argumento fue que no había fila que borrar y la decisión se
    /// aplazó a este punto. Releída ahora que sí la hay, la respuesta es la misma
    /// y el motivo es mejor: **el desenlace de un pedido tiene que poder
    /// consultarse después**. Es lo que hace verificable esta fase (mirar
    /// <c>CurrentState</c> es cómo se comprueba que la compensación terminó), lo
    /// que 4.7 necesita para afirmar el estado final, y lo que 6.5 querrá para la
    /// página de estado del pedido. Con <c>Finalize()</c>, de un pedido cerrado no
    /// queda más rastro que <c>Order.Status</c>, que dice *qué* pasó pero no *por
    /// dónde* se pasó.
    ///
    /// El precio, dicho en voz alta: la tabla crece sin techo, una fila por pedido
    /// para siempre, y nadie la purga. Es exactamente la misma renuncia consciente
    /// que <c>ProcessedMessages</c> (3.6), y con la misma condición: el día que
    /// aparezca una purga, aparece con su índice sobre <c>CreatedAt</c>.
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
    /// La foto de precios del pedido es auténtica. Lo publica Catalog.API desde 4.8
    /// y **hasta hoy no lo consumía nadie**: su exchange existía con cero colas
    /// ligadas, o sea publicándose al vacío, igual que les pasó a
    /// <c>StockRejected</c> y <c>PaymentFailed</c> entre 3.4 y 4.3. Éste es el
    /// binding que lo cierra.
    ///
    /// "Auténtica" no significa "igual al precio de hoy" — ver su <c>///</c>, donde
    /// 4.8 explica por qué comparar contra el precio actual rechazaría un pedido
    /// legítimo cuyo precio cambió a mitad del checkout. Significa que es un precio
    /// que Catalog llegó a ofrecer dentro de una ventana, y que el <c>Total</c>
    /// cuadra con las líneas.
    ///
    /// Solo lleva el <c>OrderId</c>, y a la saga le basta: lo único que necesita
    /// saber es *que* la espera terminó bien.
    /// </summary>
    public Event<OrderPricingValidated> OrderPricingValidated { get; private set; } = null!;

    /// <summary>
    /// La foto de precios no es auténtica: algún producto no existe, algún
    /// <c>UnitPrice</c> no es un precio que Catalog ofreciera, o el <c>Total</c> no
    /// cuadra con las líneas. Publicado por Catalog.API desde 4.8.
    ///
    /// **Es el evento que cierra el agujero medido en la corrección 2b de
    /// docs/fase_3_3.md**: desde que 3.3 dejó que el cuerpo del <c>POST</c> traiga
    /// el precio, un pedido de un producto real a <c>0.01</c> atravesaba la saga
    /// entera y se cobraba un céntimo. Inventory no podía verlo —guarda cantidades,
    /// no importes— y el importe se había quedado sin dueño. Ahora lo tiene.
    ///
    /// Su <c>Reason</c> viaja tal cual dentro de <c>OrderCancelled</c>, como el de
    /// <c>StockRejected</c>: diagnóstico y material para el email de 4.6, nunca un
    /// código que nadie deba parsear.
    ///
    /// **Y aquí NO se puede afirmar lo que afirma <c>StockRejected</c>.** Aquel
    /// puede decir que no hay nada que compensar porque la reserva de Inventory es
    /// atómica. Éste no: Inventory consume <c>OrderCreated</c> del mismo fanout que
    /// Catalog, así que cuando este rechazo llega el stock puede estar ya
    /// reservado. Los dos <c>During</c> que lo atienden son los que resuelven eso,
    /// y ninguno de los dos va a <c>Cancelled</c> directo.
    /// </summary>
    public Event<OrderPricingRejected> OrderPricingRejected { get; private set; } = null!;

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
        // Con InMemoryRepository() un reinicio borraba todas las instancias
        // —medido en la verificación 7 de docs/fase_4_1.md—, así que un pedido que
        // estaba esperando su cobro se quedaba huérfano. En 4.1 eso era inocuo: el
        // único evento declarado era el que ARRANCA la saga, que simplemente
        // empezaba de cero. Desde 4.2 tenía consecuencia — el StockReserved o el
        // PaymentCompleted de ese pedido no tenían dónde caer.
        //
        // Con el descarte por defecto, ese pedido desaparecía sin dejar rastro.
        // Esta línea lo pone en order-state_error, donde se ve y se puede contar.
        // Es la misma lección que la guarda Ignore de 4.1: lo que hay que dejar
        // escrito es lo que el valor por defecto no hace.
        //
        // ── Releído en 4.5, como 4.2 pidió, y se QUEDA cambiando de significado ──
        //
        // Con la saga persistida en OrdersDb.OrderStates, un reinicio de
        // Orders.API ya no pierde nada: la instancia se lee de la tabla y el
        // evento la encuentra. Así que estas dos líneas dejan de dispararse por la
        // causa que las trajo. No sobran: pasan a señalar lo único que queda, que
        // es un evento correlacionado con un pedido que **nunca existió** — un
        // mensaje reacuñado a mano, o un contrato con un OrderId inventado. Eso ya
        // no es un accidente de infraestructura, es una incoherencia, y merece la
        // cola de error todavía más que antes.
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

        // Los dos de 4.9, séptima y octava repetición del mismo par de líneas. Los
        // ocho contratos que consume esta saga llevan OrderId y ninguno lleva
        // CorrelationId, así que no hay nada que decidir aquí.
        //
        // Declararlos es lo que liga sus exchanges a la cola order-state, o sea lo
        // que hace que OrderPricingValidated y OrderPricingRejected dejen de
        // publicarse al vacío desde 4.8. Que sea aquí y no en 4.8 es el mismo
        // patrón de siempre: un Event<T> declarado sin un During que lo atienda
        // liga la cola igual y manda cada mensaje a order-state_error.
        Event(() => OrderPricingValidated, e =>
        {
            e.CorrelateById(message => message.Message.OrderId);
            e.OnMissingInstance(missing => missing.Fault());
        });

        Event(() => OrderPricingRejected, e =>
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
                        "Saga arrancada para el pedido {OrderId} de {CustomerEmail}; pasa a " +
                        "PricingPending. Catalog e Inventory están procesando este mismo evento " +
                        "en paralelo.",
                        context.Saga.CorrelationId,
                        context.Saga.CustomerEmail);
                })
                // Desde 4.9 el destino es PricingPending y no StockPending. No es que
                // se haya insertado un paso en una cadena: es que a partir de aquí hay
                // DOS respuestas pendientes a la vez, porque el mismo OrderCreated que
                // arranca esta saga lo están consumiendo Catalog (order-created-pricing)
                // e Inventory (order-created) del mismo exchange fanout.
                .TransitionTo(PricingPending));

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

        // ── El join de 4.9: las dos ramas paralelas ─────────────────────────────
        //
        // Los tres During que siguen son la parte nueva de este punto. Se leen mejor
        // como una tabla de "quién ha contestado ya":
        //
        //                                  Catalog     rama Inventory→Payments
        //   PricingPending                 falta       sin contestar
        //   PricingPendingStockReserved    falta       reservó
        //   PricingPendingPaymentCompleted falta       reservó y cobró
        //   StockPending                   validó      sin contestar
        //   CancellingStockPending         rechazó     sin contestar
        //
        // Y los cruces que no necesitan estado, porque el desenlace ya no depende de
        // Catalog: si Inventory rechaza se cancela (nada apartado), y si el cobro se
        // rechaza se compensa (hay algo apartado). En los dos casos la respuesta de
        // Catalog llega tarde y se ignora donde caiga. Es seguro porque validar
        // precios no escribe nada de negocio (4.8), así que descartarla no deja
        // rastro.

        During(PricingPending,
            // ── Rama de Catalog: el precio es auténtico ──
            //
            // Inventory sigue sin contestar, así que la espera que queda es la suya:
            // exactamente el StockPending que esta saga tuvo desde 4.1.
            When(OrderPricingValidated)
                .Then(context => logger.LogInformation(
                    "Pedido {OrderId}: Catalog validó la foto de precios; pasa a StockPending. " +
                    "Inventory todavía no ha contestado.",
                    context.Saga.CorrelationId))
                .TransitionTo(StockPending),

            // ── Rama de Catalog: el precio es falso, y NO se puede cancelar ──
            //
            // Aquí es donde el título de 4.9 en el roadmap ("sin nada que
            // compensar") se cae. No es que haya algo que compensar: es que **no se
            // sabe todavía**. Inventory está procesando el mismo OrderCreated en
            // paralelo y puede haber reservado ya. Cancelar ahora e ignorar el
            // StockReserved que llegue después dejaría las unidades apartadas para
            // siempre — la regla 7 rota en silencio, con el pedido perfectamente
            // Cancelled.
            //
            // Así que se espera, en un estado que dice exactamente eso. Ver el ///
            // de CancellingStockPending, donde están las dos alternativas
            // descartadas.
            When(OrderPricingRejected)
                .Then(context =>
                {
                    // Se guarda porque después ya no vuelve a pasar por delante:
                    // OrderCancelled sale una transición más tarde —al contestar
                    // Inventory— y ni StockReserved ni StockRejected llevan este
                    // texto. Es el mismo razonamiento que el del PaymentFailed de
                    // 4.4, y el motivo de que ahora sean TRES los caminos que
                    // escriben este campo.
                    context.Saga.CancellationReason = context.Message.Reason;

                    logger.LogWarning(
                        "Pedido {OrderId}: Catalog rechazó la foto de precios ({Reason}); pasa a " +
                        "CancellingStockPending. El pedido NO se cancela hasta saber si Inventory " +
                        "llegó a reservar algo que haya que devolver.",
                        context.Saga.CorrelationId,
                        context.Message.Reason);
                })
                .TransitionTo(CancellingStockPending),

            // ── Rama de Inventory: reservó, y hay que seguir esperando a Catalog ──
            //
            // Lo que NO se hace aquí es pasar a PaymentPending. El stock está
            // apartado, pero dar el pedido por bueno sin la respuesta de Catalog
            // sería aceptar el precio que 4.8 existe para comprobar — o sea tener
            // toda la validación y no usarla cuando Inventory gana la carrera.
            When(StockReserved)
                .Then(context => logger.LogInformation(
                    "Pedido {OrderId}: Inventory reservó el stock por {Amount} antes de que " +
                    "Catalog contestara; pasa a PricingPendingStockReserved.",
                    context.Saga.CorrelationId,
                    context.Message.Amount))
                .TransitionTo(PricingPendingStockReserved),

            // ── Rama de Inventory: rechazó, y aquí sí se termina sin esperar ──
            //
            // El pedido está perdido pase lo que pase con el precio, y la reserva de
            // Inventory es atómica: un rechazo significa que ninguna unidad se movió.
            // No hay nada que compensar y no hay nada que la respuesta de Catalog
            // pueda cambiar, así que se cancela ya. La que llegue tarde cae en
            // Cancelled, donde se ignora.
            //
            // Este es el ÚNICO camino en el que el "sin nada que compensar" del
            // título de 4.9 acierta, y ni siquiera es el evento que el título nombra.
            When(StockRejected)
                .Then(context => logger.LogInformation(
                    "Pedido {OrderId}: stock rechazado ({Reason}) con el precio todavía sin " +
                    "validar; pasa a Cancelled y se publica OrderCancelled. No hay nada que " +
                    "compensar y la respuesta de Catalog ya no cambia nada.",
                    context.Saga.CorrelationId,
                    context.Message.Reason))
                .TransitionTo(Cancelled)
                .Publish(context => new OrderCancelled
                {
                    OrderId = context.Saga.CorrelationId,
                    CustomerEmail = context.Saga.CustomerEmail,
                    Reason = context.Message.Reason,
                }),

            // Duplicado de OrderCreated: la guarda que estrenó 4.1, que se muda aquí
            // con el Initially.
            Ignore(OrderCreated));

        // ── Lo que NO se atiende en PricingPending, y la medición que lo justifica ──
        //
        // Ni PaymentCompleted, ni PaymentFailed, ni StockReleased. La saga no ha visto
        // todavía el StockReserved de este pedido, así que un cobro resuelto aquí no
        // es un duplicado: es una entrega REORDENADA. Mismo criterio literal que el de
        // StockPending desde 4.2, y misma consecuencia — va a order-state_error, donde
        // se ve.
        //
        // **Y ocurre de verdad: medido contra el compose real, dos de siete pedidos.**
        // La causa no es que Payments se adelante a Inventory —no puede, consume el
        // StockReserved que Inventory publica— sino que la cola order-state se
        // consume con concurrencia > 1, así que dos mensajes que llegaron en orden se
        // procesan a la vez y terminan al revés. Eso NO lo estrena 4.9: existe desde
        // 4.2 con PaymentCompleted en StockPending, y lo que ha hecho este punto es
        // destaparlo al añadir un salto más al recorrido.
        //
        // Se deja faultear, y funciona, porque el UseMessageRetry del Program.cs de
        // Orders.API (5 × 100 ms) reentrega el mensaje y la segunda vez la instancia
        // ya está donde tiene que estar: las tres inversiones medidas se absorbieron
        // sin que order-state_error creciera. **Ésa es la diferencia con la inversión
        // que sí se modela con un estado** (PricingPendingPaymentCompleted): aquélla
        // es estructural —dos ramas paralelas que pueden acabar en cualquier orden, y
        // ningún reintento arregla que Catalog tarde— y ésta es un reordenamiento de
        // entrega dentro de una misma cadena causal, que es exactamente lo que un
        // reintento sí arregla.
        //
        // Bajar la concurrencia de order-state a 1 lo quitaría del todo, y se
        // *descarta*: serializa la saga entera del servicio para tapar una carrera
        // que el reintento ya cubre, y es la misma línea que 4.7 dejó anotada por
        // esconder carreras en los tests en vez de mostrarlas.

        During(PricingPendingStockReserved,
            // El precio era auténtico y el stock ya está apartado: es exactamente lo
            // que significa PaymentPending, así que se salta StockPending. No es un
            // atajo — StockPending es "falta Inventory", e Inventory ya contestó.
            When(OrderPricingValidated)
                .Then(context => logger.LogInformation(
                    "Pedido {OrderId}: Catalog validó la foto de precios y el stock ya estaba " +
                    "reservado; pasa directo a PaymentPending.",
                    context.Saga.CorrelationId))
                .TransitionTo(PaymentPending),

            // ── **La transición que desmiente el título de 4.9** ──
            //
            // Precio falso y stock ya reservado: hay algo que compensar, y es el
            // mismo algo que el camino de PaymentFailed de 4.4. Se reutiliza su
            // maquinaria entera —CompensatingStock, el Send a queue:release-stock, y
            // el OrderCancelled que sale al recibir StockReleased— sin una línea
            // nueva de diseño.
            //
            // Nótese lo que NO ha pasado por aquí: nadie ha cobrado nada... o eso
            // parece. Payments consume StockReserved del fanout por su cuenta (3.5),
            // así que puede estar cobrando ahora mismo. Ver el /// de PaymentPending:
            // 4.9 es un veto con compensación, no una autorización previa.
            //
            // El orden importa, igual que en el camino de 4.4: primero TransitionTo,
            // después Send. Con el Send delante, la respuesta de Inventory podría
            // llegar con la instancia todavía aquí, donde StockReleased no está
            // aceptado.
            When(OrderPricingRejected)
                .Then(context =>
                {
                    context.Saga.CancellationReason = context.Message.Reason;

                    logger.LogWarning(
                        "Pedido {OrderId}: Catalog rechazó la foto de precios ({Reason}) con el " +
                        "stock YA reservado; pasa a CompensatingStock y se envía ReleaseStock a " +
                        "{Endpoint}. El título de 4.9 decía que aquí no había nada que compensar.",
                        context.Saga.CorrelationId,
                        context.Message.Reason,
                        InventoryReleaseStockEndpoint);
                })
                .TransitionTo(CompensatingStock)
                .Send(
                    InventoryReleaseStockEndpoint,
                    context => new ReleaseStock { OrderId = context.Saga.CorrelationId }),

            // ── La rama de Inventory sigue corriendo, y puede terminarse entera ──
            //
            // Payments consume StockReserved del mismo fanout, así que está cobrando
            // mientras la saga espera aquí. Las dos transiciones que siguen no son
            // defensa contra un caso raro: medido contra el compose real, esta
            // inversión pasa en la MITAD de los pedidos.
            When(PaymentCompleted)
                .Then(context => logger.LogInformation(
                    "Pedido {OrderId}: Payments aceptó el cobro por {Amount} (transacción " +
                    "{TransactionId}) antes de que Catalog contestara; pasa a " +
                    "PricingPendingPaymentCompleted. El pedido NO se confirma hasta saber si la " +
                    "foto de precios era auténtica.",
                    context.Saga.CorrelationId,
                    context.Message.Amount,
                    context.Message.TransactionId))
                .TransitionTo(PricingPendingPaymentCompleted),

            // Un cobro rechazado condena el pedido diga lo que diga Catalog, y hay
            // stock que devolver: se entra en la compensación de 4.4 sin esperar la
            // respuesta de precios, que caerá en CompensatingStock y se ignorará.
            // Por eso este cruce no necesita estado propio.
            When(PaymentFailed)
                .Then(context =>
                {
                    context.Saga.CancellationReason = context.Message.Reason;

                    logger.LogWarning(
                        "Pedido {OrderId}: cobro rechazado ({Reason}) con el precio todavía sin " +
                        "validar; pasa a CompensatingStock y se envía ReleaseStock a {Endpoint}. " +
                        "La respuesta de Catalog ya no cambia el desenlace.",
                        context.Saga.CorrelationId,
                        context.Message.Reason,
                        InventoryReleaseStockEndpoint);
                })
                .TransitionTo(CompensatingStock)
                .Send(
                    InventoryReleaseStockEndpoint,
                    context => new ReleaseStock { OrderId = context.Saga.CorrelationId }),

            Ignore(OrderCreated),
            Ignore(StockReserved));

        // Y no se ignora StockRejected: llegar aquí significa que Inventory ya
        // contestó StockReserved, y publica uno o el otro, nunca los dos. Sería
        // Inventory contradiciéndose. Mismo criterio que el de PaymentPending.

        During(PricingPendingPaymentCompleted,
            // La rama de Inventory terminó bien y ahora Catalog confirma que el
            // precio era auténtico: el pedido está completo. Se salta PaymentPending
            // por el mismo motivo por el que PricingPendingStockReserved se salta
            // StockPending — esos estados son esperas que aquí ya terminaron.
            When(OrderPricingValidated)
                .Then(context => logger.LogInformation(
                    "Pedido {OrderId}: Catalog validó la foto de precios y el cobro ya estaba " +
                    "aceptado; pasa directo a Confirmed y se publica OrderConfirmed.",
                    context.Saga.CorrelationId))
                .TransitionTo(Confirmed)
                .Publish(context => new OrderConfirmed
                {
                    OrderId = context.Saga.CorrelationId,
                    CustomerEmail = context.Saga.CustomerEmail,
                }),

            // ── El caso peor del proyecto, y se registra en vez de esconderse ──
            //
            // El precio era falso y **ya se ha cobrado**. La saga suelta el stock y
            // cancela el pedido, que es todo lo que puede hacer: no existe ningún
            // contrato de devolución en Shop133.Contracts. El TransactionId que
            // PaymentCompleted lleva desde 0.3 "para poder emitir el reembolso"
            // sigue sin nadie que lo use, y aquí es donde por fin se nota.
            //
            // Se deja como hueco anotado y no se inventa un RefundPayment: sería el
            // decimotercer contrato, un consumer nuevo en Payments y una decisión de
            // negocio (¿se reembolsa entero? ¿queda registro del intento?) que este
            // punto no tiene por qué tomar. Lo que sí hace es que el caso salga por
            // el log con todas las letras en vez de desaparecer.
            When(OrderPricingRejected)
                .Then(context =>
                {
                    context.Saga.CancellationReason = context.Message.Reason;

                    logger.LogError(
                        "Pedido {OrderId}: Catalog rechazó la foto de precios ({Reason}) DESPUÉS " +
                        "de que el cobro se aceptara. Se suelta el stock y se cancela, pero **el " +
                        "cobro NO se devuelve**: no existe contrato de reembolso. Pasa a " +
                        "CompensatingStock y se envía ReleaseStock a {Endpoint}.",
                        context.Saga.CorrelationId,
                        context.Message.Reason,
                        InventoryReleaseStockEndpoint);
                })
                .TransitionTo(CompensatingStock)
                .Send(
                    InventoryReleaseStockEndpoint,
                    context => new ReleaseStock { OrderId = context.Saga.CorrelationId }),

            Ignore(OrderCreated),
            Ignore(StockReserved),
            Ignore(PaymentCompleted));

        // No se ignoran StockRejected ni PaymentFailed: llegar aquí implica que
        // Inventory contestó reservando y que Payments contestó cobrando, y cada uno
        // publica un evento o el otro, nunca los dos.

        During(CancellingStockPending,
            // Inventory había reservado: hay que devolverlo. Se entra en la
            // compensación de 4.4 por el tercero de sus caminos.
            When(StockReserved)
                .Then(context => logger.LogWarning(
                    "Pedido {OrderId}: Inventory había reservado stock para un pedido cuyo precio " +
                    "Catalog ya rechazó; pasa a CompensatingStock y se envía ReleaseStock a " +
                    "{Endpoint}.",
                    context.Saga.CorrelationId,
                    InventoryReleaseStockEndpoint))
                .TransitionTo(CompensatingStock)
                .Send(
                    InventoryReleaseStockEndpoint,
                    context => new ReleaseStock { OrderId = context.Saga.CorrelationId }),

            // Inventory tampoco pudo reservar: no hay nada que devolver y el pedido
            // se termina aquí.
            //
            // **Los dos motivos son ciertos a la vez** —precio falso y sin stock— y
            // se publica el de precio, que es el que está guardado en la instancia.
            // No se concatenan: el que llevó a la saga a dar el pedido por perdido
            // fue el de Catalog, y el /// de OrderCancelled promete un texto para que
            // el cliente entienda qué pasó, no un inventario de todo lo que falló.
            When(StockRejected)
                .Then(context => logger.LogInformation(
                    "Pedido {OrderId}: Inventory tampoco pudo reservar ({StockReason}); pasa a " +
                    "Cancelled y se publica OrderCancelled con el motivo de precio " +
                    "({PricingReason}). No hay nada que compensar.",
                    context.Saga.CorrelationId,
                    context.Message.Reason,
                    context.Saga.CancellationReason))
                .TransitionTo(Cancelled)
                .Publish(context => new OrderCancelled
                {
                    OrderId = context.Saga.CorrelationId,
                    CustomerEmail = context.Saga.CustomerEmail,
                    Reason = context.Saga.CancellationReason,
                }),

            Ignore(OrderCreated),
            Ignore(OrderPricingRejected));

        // Y no se ignora OrderPricingValidated: llegar aquí significa que Catalog ya
        // contestó rechazando, y publica uno o el otro, nunca los dos. Sería Catalog
        // contradiciéndose — la simetría exacta del StockRejected en PaymentPending.

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
            Ignore(OrderCreated),

            // Y el de OrderPricingValidated, que 4.9 añade: es el evento que trajo la
            // saga hasta aquí desde PricingPending.
            Ignore(OrderPricingValidated));

        // Lo que 4.9 deliberadamente NO ignora en StockPending: OrderPricingRejected.
        // Llegar aquí implica que Catalog ya contestó validando, y Catalog publica uno
        // o el otro, nunca los dos — sería Catalog contradiciéndose. Es la simetría
        // exacta del StockRejected en PaymentPending, tres bloques más abajo.
        //
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
                // Y se publica DENTRO de la transición, no en un consumer aparte.
                // **Desde 4.5 eso además es atómico**: el UseEntityFrameworkOutbox
                // del Program.cs hace que este Publish escriba una fila de
                // OutboxMessage en la misma transacción que guarda el nuevo estado
                // de la instancia. Hasta 4.4 no lo era, y era el mismo agujero de
                // doble escritura que 3.3 anotó en OrdersController: la saga podía
                // pasar a Confirmed y morir antes de que el evento saliera, con lo
                // que el pedido se quedaba en Pending para siempre. Ni una línea de
                // esta clase cambió para cerrarlo — se cerró en la composición.
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
            Ignore(StockReserved),

            // El de 4.9: a PaymentPending se llega por dos caminos y los dos pasan
            // por el OrderPricingValidated, así que en los dos es un evento ya
            // atendido.
            Ignore(OrderPricingValidated));

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

            // ── Las guardas de este estado, y desde 4.9 es el único donde no salen
            //    de un solo recorrido ──
            //
            // La regla de siempre —se ignora lo que ya se atendió ANTES de llegar
            // aquí— se aplica a los CINCO caminos que desembocan en este estado desde
            // 4.9, y hay que unirlos:
            //
            //   PaymentPending              --PaymentFailed-->         precio VALIDADO, cobro NO
            //   PricingPendingStockReserved --OrderPricingRejected-->  precio RECHAZADO, sin cobrar
            //   PricingPendingStockReserved --PaymentFailed-->         precio SIN SABER, cobro NO
            //   PricingPendingPaymentCompleted --OrderPricingRejected--> precio RECHAZADO, COBRADO
            //   CancellingStockPending      --StockReserved-->         precio RECHAZADO, sin cobrar
            //
            // Los cinco pasaron por OrderCreated y por StockReserved; en todo lo demás
            // difieren. Como el estado no recuerda por dónde vino, **las guardas son
            // la unión**, y eso incluye PaymentCompleted.
            //
            // **Y ahí se pierde algo, dicho en voz alta**: hasta 4.8 este estado NO
            // ignoraba PaymentCompleted a propósito, porque solo se llegaba con un
            // PaymentFailed y un cobro aceptado aquí era Payments contradiciéndose.
            // Desde 4.9 hay un camino legítimo que sí pasó por el cobro, así que esa
            // contradicción deja de detectarse. Es el precio de que un estado tenga
            // varias historias; la alternativa —partirlo en dos por procedencia— no
            // ganaría ninguna transición distinta.
            Ignore(OrderCreated),
            Ignore(StockReserved),
            Ignore(PaymentFailed),
            Ignore(PaymentCompleted),
            Ignore(OrderPricingValidated),
            Ignore(OrderPricingRejected));

        // Y lo único que sigue sin ignorarse en CompensatingStock desde 4.9:
        // StockRejected. Llegar aquí implica que Inventory ya contestó StockReserved,
        // e Inventory publica uno o el otro, nunca los dos — sería Inventory diciendo
        // que no reservó lo que la saga le está pidiendo que devuelva. Mismo criterio
        // literal que el de PaymentPending.

        During(Confirmed,
            // Los estados terminales también necesitan sus guardas, y son los que
            // más fácil se olvidan: aquí no queda ninguna transición que escribir,
            // así que un During(Confirmed, ...) parece código muerto. No lo es — sin
            // él, una reentrega tardía manda a la cola de error un pedido que
            // terminó perfectamente.
            //
            // Van los OCHO eventos, no solo los del camino recorrido: llegar a
            // Confirmed descarta que StockRejected, PaymentFailed o
            // OrderPricingRejected sean parte de la historia de este pedido, pero no
            // impide que uno se reentregue tarde —o que llegue reacuñado a mano, como
            // en las pruebas de 3.6—, y el resultado sería el mismo pedido perfecto en
            // order-state_error.
            //
            // El Ignore(StockReleased) que añadió 4.4 y el Ignore(OrderPricingRejected)
            // que añade 4.9 son literalmente inalcanzables: al camino feliz no se le
            // manda nunca ReleaseStock, y un pedido confirmado tiene por fuerza su
            // precio validado. Se ponen igual porque la disciplina de los estados
            // terminales es deliberadamente roma —los ignoran TODOS— y una excepción
            // obligaría a razonar caso por caso cada vez que se añade un evento, que
            // es como se olvida uno. 4.9 acaba de añadir dos.
            Ignore(OrderCreated),
            Ignore(OrderPricingValidated),
            Ignore(OrderPricingRejected),
            Ignore(StockReserved),
            Ignore(PaymentCompleted),
            Ignore(StockRejected),
            Ignore(PaymentFailed),
            Ignore(StockReleased));

        During(Cancelled,
            // El segundo estado terminal, con las mismas ocho guardas y por el mismo
            // motivo. Este es más fácil de olvidar todavía, porque **desde 4.9 se
            // llega a él por CUATRO caminos distintos** —StockRejected desde
            // PricingPending y desde StockPending, StockReleased desde
            // CompensatingStock, y StockRejected desde CancellingStockPending— y
            // ninguno de los cuatro pasa por aquí al escribirlo.
            //
            // Aquí el Ignore(StockReleased) sí es imprescindible, y es el más obvio
            // de los ocho en cuanto se ve de dónde se viene: CompensatingStock
            // desemboca justo aquí, así que una reentrega tardía del evento que
            // acaba de mover la saga cae exactamente en este estado. Es el caso
            // normal, no el raro.
            //
            // Y los dos de precio también son del caso normal, no del raro: el
            // camino de PricingPending --StockRejected--> Cancelled termina el pedido
            // **sin esperar a Catalog**, así que la respuesta de Catalog llega
            // siempre después y siempre cae aquí. Sin estas dos líneas, todo pedido
            // sin stock acabaría con un mensaje en order-state_error.
            Ignore(OrderCreated),
            Ignore(OrderPricingValidated),
            Ignore(OrderPricingRejected),
            Ignore(StockReserved),
            Ignore(PaymentCompleted),
            Ignore(StockRejected),
            Ignore(PaymentFailed),
            Ignore(StockReleased));
    }
}
