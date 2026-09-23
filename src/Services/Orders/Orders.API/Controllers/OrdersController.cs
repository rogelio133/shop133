using MassTransit;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Orders.API.Models;
using Orders.Domain.Entities;
using Orders.Infrastructure.Persistence;

using Shop133.Contracts;
using Shop133.Contracts.Events;

namespace Orders.API.Controllers;

/// <summary>
/// Alta y consulta de pedidos (2.3, reescrito en 3.3).
///
/// Inyecta el <see cref="OrdersDbContext"/> directamente, igual que
/// <c>ProductsController</c> y por el mismo motivo: el DbContext ya es Unit of
/// Work + Repository, y las invariantes del pedido viven en el constructor de
/// <see cref="Order"/>, no aquí. Lo que este tipo hace —agrupar líneas, traducir
/// el cuerpo al agregado y el agregado al evento— es traducción entre HTTP, el
/// dominio y la mensajería, que es el trabajo de un controller.
///
/// ── Lo que cambió en 3.3 ──
///
/// Aquí había un <c>CatalogClient</c>. Orders no aceptaba un pedido si Catalog no
/// contestaba, porque no tenía precios que congelar y no puede leer
/// <c>CatalogDb</c> (regla 1). Ese dolor era el entregable de la Fase 2; 2.4 lo
/// hizo reproducible con WireMock y este punto lo borra.
///
/// Ahora el alta **no llama a nadie**: persiste el pedido y publica
/// <c>OrderCreated</c>. Con Catalog.API parado, este endpoint devuelve 201 — que
/// es la diferencia entera entre la Fase 2 y la Fase 3, y se comprueba parando el
/// contenedor.
///
/// Los desenlaces del alta se quedan en dos, y **haber perdido uno es el resultado
/// del punto, no una simplificación**:
/// - <b>201</b> — pedido creado y evento publicado.
/// - <b>400</b> — el cuerpo no vale. Ya no incluye "ese producto no existe":
///   Orders no lo sabe. Quien lo descubre es Inventory en 3.4, y lo dice con un
///   <c>StockRejected</c> que cancela el pedido en vez de con un código HTTP.
/// - <s>502</s> — desaparece con la dependencia que lo causaba.
/// </summary>
[ApiController]
[Route("[controller]")]
public sealed class OrdersController(
    OrdersDbContext db,
    IPublishEndpoint publisher,
    ILogger<OrdersController> logger) : ControllerBase
{
    [HttpPost]
    [EndpointSummary("Crea un pedido")]
    [EndpointDescription(
        "Persiste el pedido y publica OrderCreated en RabbitMQ. No llama a ningún otro servicio: " +
        "Catalog.API puede estar caído y el pedido se crea igual. En la Fase 2 esta misma petición " +
        "devolvía 502 en esas condiciones.\n\n" +
        "El cuerpo lleva la foto completa de cada línea —productId, sku, nombre, cantidad y precio— y " +
        "Orders la congela tal cual, sin contrastarla con nadie. Un producto borrado o con precio nuevo " +
        "no cambia lo que ya se compró; el precio de esa autonomía es que tampoco se comprueba que el " +
        "producto exista ni que el importe sea el del catálogo.\n\n" +
        "Las líneas repetidas se agrupan sumando cantidades, así que dos entradas de 2 y 3 unidades del " +
        "mismo producto salen como una sola línea de 5. Eso sí exige que coincidan en sku, nombre y " +
        "precio: dos fotos distintas del mismo producto en un mismo cuerpo son una contradicción y se " +
        "rechazan con 400.\n\n" +
        "No se comprueba el stock. El reservable pertenece a Inventory desde 3.4 y lo reserva la saga; " +
        "un pedido de un producto agotado se acepta aquí y se cancela después.\n\n" +
        "Errores:\n" +
        "- 400 — falla la validación del cuerpo, o dos líneas del mismo producto se contradicen.")]
    [ProducesResponseType<OrderResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<OrderResponse>> Create(
        CreateOrderRequest request,
        CancellationToken cancellationToken)
    {
        // Agrupar sigue siendo obligatorio, aunque ya no ahorre peticiones HTTP:
        // el constructor de Order rechaza un ProductId repetido, porque esas
        // líneas viajan dentro de ReserveStock en 3.4 y un Inventory que reciba
        // dos entradas del mismo producto tendría que adivinar si reserva la
        // suma. El agregado afirma la invariante; arreglarla es trabajo de aquí.
        var lines = Group(request.Items);

        // Lo que 2.3 no tenía que decidir: al venir la foto en el cuerpo, dos
        // entradas del mismo producto pueden discrepar en sku, nombre o precio.
        // Antes no podían — la foto la ponía Catalog, una sola vez por producto.
        AddInconsistentSnapshotErrors(lines);

        // Al llegar aquí ModelState solo puede estar sucio por la línea de arriba:
        // el filtro de [ApiController] ya devolvió 400 por su cuenta si fallaron
        // las DataAnnotations, así que la acción ni se habría ejecutado.
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        Order order;

        try
        {
            // Los cinco campos de cada línea salen ahora del propio cuerpo. La
            // regla que 0.3 dejó escrita —"quien construye una línea rellena los
            // cinco"— sobrevive intacta; lo único que cambió es de dónde salen
            // los tres congelados.
            var items = lines
                .Select(line => new OrderItem(
                    line.ProductId,
                    line.ProductSku,
                    line.ProductName,
                    line.Quantity,
                    line.UnitPrice))
                .ToList();

            order = new Order(request.CustomerEmail, items);
        }
        catch (ArgumentException exception)
        {
            return ToValidationProblem(exception);
        }

        db.Orders.Add(order);

        // ── 4.5: el Publish pasa a ir ANTES del SaveChanges ──
        //
        // Parece que esto revierte la decisión 3 de docs/fase_3_3.md, que eligió
        // expresamente "persistir primero, publicar después". No la revierte: la
        // cumple, porque lo que ha cambiado es qué hace esta línea.
        //
        // Aquel orden se eligió para que Inventory no pudiera reservar stock de un
        // pedido que nunca llegó a persistirse —stock reservado que nadie va a
        // liberar, justo lo que la regla 7 existe para impedir—. Y tenía un
        // precio, la doble escritura: muerto el proceso entre el COMMIT y el
        // Publish, el pedido se quedaba en Pending para siempre sin evento que
        // arrancara la saga. 3.6 agrandó ese agujero al quitar el reenvío que lo
        // tapaba por rebote.
        //
        // Con el AddEntityFrameworkOutbox de Program.cs, este Publish **ya no
        // habla con RabbitMQ**: escribe una fila en OutboxMessage dentro del
        // ChangeTracker de este mismo DbContext. Así que va antes del SaveChanges
        // porque tiene que ir DENTRO de él — es lo que hace que el pedido y su
        // evento entren en la misma transacción. El peligro que motivaba el orden
        // viejo desaparece por construcción: si el SaveChanges no confirma, no hay
        // pedido y tampoco hay mensaje que entregar.
        //
        // Si algún día alguien quita el outbox y deja este orden, vuelve el fallo
        // que 3.3 evitaba, y en su forma peor. Las dos cosas cambian juntas.
        //
        // Aquí se cobra la decisión 4 de 3.3: se inyecta IPublishEndpoint y NO
        // IBus. El outbox se engancha al primero, que es scoped y comparte ámbito
        // con el DbContext; IBus es singleton y publicaría directo al broker sin
        // ver ninguna transacción. Por eso esta línea solo ha habido que moverla.
        //
        // CancellationToken.None se queda, aunque ahora signifique otra cosa: ya
        // no protege de que un navegador cerrado deje un pedido huérfano —de eso
        // se encarga la transacción—, sino de que la escritura de la fila del
        // outbox no se cancele a mitad y haga fallar el SaveChanges entero.
        await publisher.Publish(ToOrderCreated(order), CancellationToken.None);

        // Un solo SaveChanges para el pedido, sus líneas y —desde 4.5— la fila del
        // outbox con el OrderCreated. OrderItem es un tipo owned (2.2), así que EF
        // inserta las filas de OrderItems sin que nadie las añada a un DbSet. No
        // hay transacción explícita porque SaveChanges ya envuelve todo el lote en
        // una, y ese "todo el lote" es exactamente lo que este punto amplía.
        await db.SaveChangesAsync(cancellationToken);

        // El primer rastro del proyecto en un log que sirva para seguir un mensaje
        // por el broker. En 3.4 y 3.5 esto es lo que se cruza con la UI de
        // RabbitMQ para saber si el problema está antes o después de la
        // publicación.
        // "publicado" desde 4.5 significa "escrito en el outbox y confirmado con
        // el pedido". Sale hacia RabbitMQ un instante después, por su cuenta.
        logger.LogInformation(
            "Pedido {OrderId} creado con {LineCount} línea(s) por un total de {Total}; OrderCreated publicado.",
            order.Id,
            order.Items.Count,
            order.Total);

        return CreatedAtAction(
            nameof(GetById),
            new { id = order.Id },
            OrderResponse.From(order));
    }

    /// <summary>
    /// La lectura del pedido. Existe en 2.3 —y no en 6.5, donde el roadmap la
    /// sitúa— por una razón concreta: <c>CreatedAtAction</c> necesita una acción
    /// destino para construir la cabecera <c>Location</c> del 201, y sin ella el
    /// alta devolvería un enlace a una ruta que da 404.
    ///
    /// *Descartado* <c>Created($"/orders/{id}", …)</c> con la ruta escrita a mano,
    /// que habría respetado el alcance del punto al precio de publicar un enlace
    /// roto y de duplicar la ruta en una cadena que nadie revisa cuando el
    /// <c>[Route]</c> cambia.
    ///
    /// Es el mínimo: sin listado, sin paginación y sin filtros. **6.5 no la amplió**
    /// — le puso al lado <see cref="GetStatus"/>, que es un recurso distinto y no
    /// más campos en éste. El motivo está allí: la página de estado se sondea cada
    /// dos segundos, y devolver las líneas y el total en cada vuelta sería pagar el
    /// cuerpo entero por el único campo que puede haber cambiado.
    /// </summary>
    [HttpGet("{id:guid}")]
    [EndpointSummary("Obtiene un pedido por su Id")]
    [EndpointDescription(
        "Devuelve el pedido con sus líneas y el total calculado. 404 si el Id no existe.\n\n" +
        "El Id es un Guid que acuña el propio servicio al crear el pedido, no la base de datos: desde " +
        "la Fase 4 es además la clave de correlación de la saga, así que este mismo valor es el que " +
        "sirve para seguir el pedido por RabbitMQ y por Jaeger.\n\n" +
        "El estado es uno de tres: Pending, Confirmed o Cancelled. Lo mueve la saga, a través de los " +
        "dos consumers que Orders.API tiene desde 4.3, así que un pedido recién creado responde " +
        "Pending y se resuelve solo unos cientos de milisegundos después.\n\n" +
        "Para SEGUIR ese avance está GET /orders/{id}/status, que además del estado del pedido " +
        "devuelve por dónde va el proceso y, si se canceló, por qué. Este endpoint devuelve el " +
        "detalle; aquél, el progreso.")]
    [ProducesResponseType<OrderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrderResponse>> GetById(Guid id, CancellationToken cancellationToken)
    {
        // Sin Include: las líneas son un tipo owned (decisión 1 de
        // docs/fase_2_2.md), así que EF las carga siempre con el pedido — de
        // hecho no hay forma de pedirlas por separado. Es un modo de fallo
        // silencioso que aquí no existe.
        //
        // AsNoTracking porque nada de lo que se lee se va a modificar.
        var order = await db.Orders
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        return order is null
            ? NotFound()
            : OrderResponse.From(order);
    }

    /// <summary>
    /// El endpoint que el roadmap nombra en 6.5, y el único del servicio que lee la
    /// tabla de la saga.
    ///
    /// ── Por qué no bastaba con <see cref="GetById"/> ──
    ///
    /// <c>Order.Status</c> son tres valores (2.1): un pedido está <c>Pending</c> y
    /// de pronto está resuelto. Todo lo que pasa en medio —validar el precio con
    /// Catalog, reservar stock, cobrar, y a veces deshacer la reserva— vive en
    /// <c>OrderStates.CurrentState</c>, que desde 4.9 son nueve estados y dos ramas
    /// que corren **en paralelo**. Sin esto, la página de 6.5 solo podría enseñar un
    /// salto, que es justo lo contrario de lo que el roadmap pide de ella.
    ///
    /// Esto **corrige por escrito** el comentario final de
    /// <c>OrderStateConfiguration</c>, que daba por hecho que 6.5 leería <c>Orders</c>
    /// y no la tabla de saga. El <c>///</c> de <c>OrderStateMachine.Confirmed</c>
    /// decía lo contrario, y es el que gana. Ver <see cref="OrderStatusResponse"/>.
    ///
    /// ── Un recurso nuevo, no campos nuevos en el de arriba ──
    ///
    /// *Descartado* añadirle <c>Stage</c> a <c>OrderResponse</c>, que habría sido
    /// una línea. Esto se sondea cada dos segundos desde un navegador: con los
    /// campos metidos allí, cada vuelta arrastraría las líneas del pedido y el total
    /// para mirar un <c>string</c>. Y habría cambiado el cuerpo del 201 del alta, que
    /// tiene consumidores (<c>PlacedOrder</c> en el frontend, <c>Orders.Tests</c>)
    /// para los que ese dato no significa nada.
    ///
    /// *Descartado* también traducir los nombres de estado a etiquetas de interfaz
    /// aquí dentro. Se devuelve el identificador de C# tal cual; las etiquetas las
    /// pone quien pinta. Un servicio que empieza a decidir qué texto ve el usuario
    /// acaba con el vocabulario de la interfaz metido en la máquina de estados.
    ///
    /// ── Una sola consulta ──
    ///
    /// <c>Orders</c> y <c>OrderStates</c> comparten el valor de la clave primaria
    /// —el <c>OrderId</c> *es* el <c>CorrelationId</c>— pero **no tienen clave
    /// foránea ni navegación entre ellas**, y eso es deliberado: son dos cosas con
    /// dos dueños, y el repositorio de saga es de MassTransit. Así que el
    /// <c>JOIN</c> se escribe a mano.
    ///
    /// Es un LEFT JOIN y no uno normal. Un pedido puede existir sin instancia de
    /// saga: desde 4.5 el <c>OrderCreated</c> se escribe en el outbox y sale hacia
    /// RabbitMQ un instante después, así que entre el 201 y el arranque de la saga
    /// hay una ventana real —y con el broker parado, larga—. Con un <c>JOIN</c>
    /// normal esos pedidos darían **404**, que es la respuesta más confusa posible:
    /// el pedido acaba de crearse y la página diría que no existe.
    /// </summary>
    [HttpGet("{id:guid}/status")]
    [EndpointSummary("Obtiene el progreso de un pedido")]
    [EndpointDescription(
        "Devuelve el estado del pedido, por dónde va su saga y, si se canceló, el motivo. " +
        "404 si el Id no existe.\n\n" +
        "Está pensado para sondearse: es mucho más ligero que GET /orders/{id} porque no trae ni " +
        "las líneas ni el total.\n\n" +
        "Dos campos con dos relojes distintos. 'status' es el desenlace del PEDIDO y son tres " +
        "valores: Pending, Confirmed, Cancelled. 'stage' es por dónde va el PROCESO y son los " +
        "nombres reales de los estados de la saga: PricingPending, StockPending, PaymentPending, " +
        "PricingPendingStockReserved, PricingPendingPaymentCompleted, CancellingStockPending, " +
        "CompensatingStock, Confirmed y Cancelled. Desde 4.9 la validación del precio corre EN " +
        "PARALELO con la reserva de stock, así que los estados con nombre compuesto no son ruido: " +
        "dicen qué rama ya contestó y cuál falta.\n\n" +
        "'stage' puede ser null, y no es un error: significa que la saga todavía no ha arrancado. " +
        "El evento que la arranca sale por el outbox (4.5), así que hay una ventana entre el 201 " +
        "del alta y la creación de la instancia.\n\n" +
        "'cancellationReason' puede ser null en un pedido cancelado. De los caminos que cancelan, " +
        "los que publican en la misma transición en la que reciben el motivo lo leen del mensaje y " +
        "no lo guardan; solo lo escriben los que tienen que recordarlo para una transición " +
        "posterior.\n\n" +
        "'isFinal' mira 'status', no 'stage': la saga llega a Confirmed un mensaje antes de que el " +
        "pedido lo haga. Es el campo con el que un sondeo decide parar.")]
    [ProducesResponseType<OrderStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrderStatusResponse>> GetStatus(
        Guid id,
        CancellationToken cancellationToken)
    {
        // La proyección va DENTRO de la consulta, no sobre entidades materializadas:
        // así EF emite un SELECT de seis columnas en lugar de traerse el pedido con
        // sus líneas (tipo owned, vienen siempre) para tirarlas después. Con un
        // sondeo cada dos segundos, esa diferencia se paga treinta veces por minuto.
        //
        // AsNoTracking porque nada de esto se modifica. En una proyección a un tipo
        // que no es una entidad EF tampoco rastrearía, pero se escribe igual: es lo
        // que hace GetById y la simetría entre los dos métodos vale más que la línea.
        var status = await db.Orders
            .AsNoTracking()
            .Where(order => order.Id == id)
            .GroupJoin(
                db.OrderStates.AsNoTracking(),
                order => order.Id,
                saga => saga.CorrelationId,
                (order, sagas) => new { order, sagas })
            .SelectMany(
                pair => pair.sagas.DefaultIfEmpty(),
                (pair, saga) => new OrderStatusResponse
                {
                    Id = pair.order.Id,

                    // OrderResponse.From puede escribir Status.ToString() porque
                    // corre sobre una entidad ya materializada. Aquí no: esto se
                    // traduce a SQL y ToString() sobre un enum no tiene traducción.
                    //
                    // Dejar que viajara el enum a secas publicaría el ORDINAL, que
                    // es justo lo que el /// de OrderResponse.Status prohíbe: el
                    // cliente se acoplaría al orden de declaración, y 2.1 puso los
                    // números explícitos precisamente para poder añadir estados sin
                    // renumerar nada. De ahí el case explícito, que SQL Server sabe
                    // evaluar y que mantiene las dos respuestas diciendo lo mismo.
                    Status = pair.order.Status == OrderStatus.Confirmed
                        ? nameof(OrderStatus.Confirmed)
                        : pair.order.Status == OrderStatus.Cancelled
                            ? nameof(OrderStatus.Cancelled)
                            : nameof(OrderStatus.Pending),

                    // saga es null cuando el LEFT JOIN no encontró fila. El operador
                    // condicional nulo se traduce a SQL sin problema.
                    Stage = saga == null ? null : saga.CurrentState,

                    // La columna es NOT NULL con cadena vacía por defecto (4.5), así
                    // que "vacío" y "no hay" son lo mismo para quien lee. Se
                    // normaliza a null aquí para que el cliente tenga una sola
                    // comprobación que hacer en vez de dos.
                    CancellationReason = saga == null || saga.CancellationReason == string.Empty
                        ? null
                        : saga.CancellationReason,

                    CreatedAt = pair.order.CreatedAt,

                    IsFinal = pair.order.Status != OrderStatus.Pending,
                })
            .FirstOrDefaultAsync(cancellationToken);

        return status is null
            ? NotFound()
            : status;
    }

    /// <summary>
    /// El agregado traducido al evento que arranca la saga.
    ///
    /// Vive aquí y no en un mapeador compartido siguiendo el precedente de 2.1: no
    /// se inventa dónde vive una abstracción antes de tener el segundo caso de uso.
    /// El siguiente será el <c>ReserveStock</c> de la saga en 4.1, y **no es el
    /// mismo mapeo** —parte del estado de la saga, no de un <c>Order</c> cargado—,
    /// así que hoy no hay nada que compartir.
    ///
    /// <c>Total</c> viaja explícito aunque sea derivable de las líneas: es la
    /// decisión 5 de docs/fase_3_2.md. El importe de un pedido es un hecho
    /// congelado con un solo dueño; si cada consumidor lo recalculase, cada uno
    /// podría redondear a su manera. De aquí sale, vía <c>StockReserved.Amount</c>,
    /// lo que Payments cobra en 3.5.
    ///
    /// El nombre del exchange que crea este mensaje sale del <c>FullName</c> del
    /// tipo: <c>Shop133.Contracts.Events:OrderCreated</c>. Mover el record de
    /// namespace no es un refactor, es renombrar un exchange — lo vigila
    /// <c>Contracts_PublicTypes_LiveInEventsOrCommandsNamespace</c> desde 3.2.
    /// </summary>
    private static OrderCreated ToOrderCreated(Order order) => new()
    {
        OrderId = order.Id,
        CustomerEmail = order.CustomerEmail,
        Total = order.Total,
        Lines = [.. order.Items.Select(item => new OrderLine
        {
            ProductId = item.ProductId,
            ProductSku = item.ProductSku,
            ProductName = item.ProductName,
            Quantity = item.Quantity,
            UnitPrice = item.UnitPrice,
        })],
    };

    /// <summary>
    /// Colapsa las líneas repetidas sumando cantidades y se queda con el índice
    /// de la primera aparición de cada producto, que es el que necesitan los
    /// mensajes de error para poder señalar una línea del cuerpo original.
    ///
    /// La foto (sku, nombre, precio) se toma de la **primera** aparición. Que las
    /// demás coincidan lo comprueba <see cref="AddInconsistentSnapshotErrors"/>,
    /// que corre justo después: agrupar y validar la coherencia son dos trabajos
    /// distintos y separarlos deja que el error nombre la línea culpable.
    ///
    /// <c>GroupBy</c> de LINQ to Objects preserva el orden de aparición de los
    /// grupos, así que el pedido resultante respeta el orden en que el cliente
    /// escribió las líneas.
    ///
    /// La suma no puede desbordar: como mucho 50 líneas (<c>[MaxLength]</c>) de
    /// 10.000 unidades (<c>[Range]</c>) son 500.000. Los dos topes del DTO están
    /// puestos para eso.
    /// </summary>
    private static List<RequestedLine> Group(IReadOnlyList<CreateOrderItemRequest> items) =>
        items
            .Select((item, index) => (item, index))
            .GroupBy(entry => entry.item.ProductId)
            .Select(group => new RequestedLine(
                group.Key,
                group.Sum(entry => entry.item.Quantity),
                group.First().index,
                group.First().item.ProductSku,
                group.First().item.ProductName,
                group.First().item.UnitPrice,
                group.Any(entry =>
                    entry.item.ProductSku != group.First().item.ProductSku
                    || entry.item.ProductName != group.First().item.ProductName
                    || entry.item.UnitPrice != group.First().item.UnitPrice)))
            .ToList();

    /// <summary>
    /// El 400 que 3.3 estrena, y que ocupa el hueco del "ese producto no existe"
    /// que este punto se llevó por delante.
    ///
    /// Dos líneas del mismo producto que discrepan en el precio son un cuerpo que
    /// se contradice. *Descartado* quedarse con la primera y seguir, que es lo que
    /// sale gratis: resolvería la ambigüedad por sorteo y el cliente cobraría un
    /// importe que no pidió, sin enterarse. *Descartado* también tratarlas como
    /// dos líneas distintas, que rompería la invariante de <c>Order</c> —un solo
    /// <c>ProductId</c> por pedido— y dejaría a Inventory adivinando en 3.4.
    ///
    /// La clave nombra la línea con la misma forma que genera la validación de
    /// MVC sobre una colección (<c>Items[0].ProductId</c>), heredada del error de
    /// producto desconocido de 2.3: el cliente no tiene que distinguir dos
    /// formatos de error de entrada. El índice es el de la **primera aparición**.
    /// </summary>
    private void AddInconsistentSnapshotErrors(IReadOnlyList<RequestedLine> lines)
    {
        foreach (var line in lines.Where(candidate => candidate.HasInconsistentSnapshot))
        {
            ModelState.AddModelError(
                $"{nameof(CreateOrderRequest.Items)}[{line.FirstIndex}].{nameof(CreateOrderItemRequest.ProductId)}",
                $"El producto {line.ProductId} aparece en varias líneas con sku, nombre o precio distintos. " +
                "Las líneas repetidas se agrupan, así que las tres deben coincidir.");
        }
    }

    /// <summary>
    /// Segunda línea de defensa del 400, igual que en Catalog (1.3).
    ///
    /// **Desde 3.3 es defensa en profundidad y ya no la primera línea.** Cuando la
    /// foto la traía Catalog, este catch era el único que separaba un 400 de un
    /// 500 ante un sku más largo de la cuenta: ninguna DataAnnotation podía ver un
    /// valor que no venía en el cuerpo. Ahora el cuerpo lo trae y el DTO lo valida
    /// antes, así que el filtro de <c>[ApiController]</c> corta primero.
    ///
    /// Se mantiene igualmente, y no por costumbre: los constructores de
    /// <see cref="Order"/> y <see cref="OrderItem"/> validan cosas que el DTO no
    /// puede —la invariante de productos repetidos, por ejemplo— y tienen que
    /// sostenerlas venga la llamada de donde venga. Sin este catch, un camino que
    /// se escape de las DataAnnotations sería un 500.
    ///
    /// Un solo catch de <see cref="ArgumentException"/> cubre también
    /// <see cref="ArgumentOutOfRangeException"/>, que hereda de él.
    /// </summary>
    private ActionResult ToValidationProblem(ArgumentException exception)
    {
        ModelState.AddModelError(exception.ParamName ?? string.Empty, exception.Message);

        return ValidationProblem(ModelState);
    }

    /// <summary>
    /// Una línea del cuerpo ya agrupada: qué producto, cuántas unidades en total,
    /// en qué posición del cuerpo original apareció por primera vez, la foto que
    /// dio esa primera aparición, y si el resto de sus apariciones la contradicen.
    /// </summary>
    private sealed record RequestedLine(
        int ProductId,
        int Quantity,
        int FirstIndex,
        string ProductSku,
        string ProductName,
        decimal UnitPrice,
        bool HasInconsistentSnapshot);
}
