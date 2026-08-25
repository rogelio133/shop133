using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Orders.API.Models;
using Orders.Domain.Entities;
using Orders.Infrastructure.Catalog;
using Orders.Infrastructure.Persistence;

namespace Orders.API.Controllers;

/// <summary>
/// Alta y consulta de pedidos (2.3).
///
/// Inyecta el <see cref="OrdersDbContext"/> directamente, igual que
/// <c>ProductsController</c> y por el mismo motivo: el DbContext ya es Unit of
/// Work + Repository, y las invariantes del pedido viven en el constructor de
/// <see cref="Order"/>, no aquí. Lo que este tipo hace —agrupar líneas, pedir los
/// datos a Catalog, traducir tres modos de fallo a tres códigos HTTP— es
/// precisamente traducción entre HTTP y el dominio, que es el trabajo de un
/// controller.
///
/// ── El punto entero es la llamada síncrona ──
///
/// <see cref="CatalogClient"/> es deuda deliberada (regla 2 de CLAUDE.md). Orders
/// no puede aceptar un pedido si Catalog no contesta, porque no tiene precios que
/// congelar y no puede leer <c>CatalogDb</c> (regla 1). Ese dolor es el
/// entregable: 2.4 lo hace reproducible con WireMock y 3.3 lo borra publicando
/// <c>OrderCreated</c> en su lugar.
///
/// Los tres desenlaces del alta son el contenido real del punto:
/// - <b>201</b> — pedido creado, con las líneas congeladas.
/// - <b>400</b> — el cuerpo no vale, o alguno de sus productos no existe en
///   Catalog. Que un producto no exista es un valor malo del cuerpo, no un
///   recurso ausente en la URL: mismo criterio que el <c>categoryId</c>
///   desconocido de <c>POST /products</c>.
/// - <b>502</b> — Catalog no contestó. Orders está vivo; quien falló es la
///   dependencia. *Descartado* 503, que diría "este servicio no está disponible"
///   y sería mentira: el 502 apunta a la dependencia, que es exactamente la
///   lección de la fase.
/// </summary>
[ApiController]
[Route("[controller]")]
public sealed class OrdersController(
    OrdersDbContext db,
    CatalogClient catalog,
    ILogger<OrdersController> logger) : ControllerBase
{
    [HttpPost]
    [EndpointSummary("Crea un pedido")]
    [EndpointDescription(
        "El cuerpo lleva solo el correo y una lista de productId + quantity. El sku, el nombre y el " +
        "precio de cada línea NO se piden al cliente: los consulta este endpoint contra Catalog.API y " +
        "los congela en el pedido, de modo que un producto borrado o con precio nuevo no cambia lo que " +
        "ya se compró.\n\n" +
        "Las líneas repetidas se agrupan sumando cantidades, así que dos entradas de 2 y 3 unidades del " +
        "mismo producto salen como una sola línea de 5.\n\n" +
        "No se comprueba el stock. El stock que publica Catalog es el que muestra el catálogo; el " +
        "reservable pertenece a Inventory desde la Fase 3 y lo reserva la saga.\n\n" +
        "Errores:\n" +
        "- 400 — falla la validación del cuerpo, o alguno de los productId no existe en Catalog. Es " +
        "400 y no 404 porque lo que falta es un valor del cuerpo, no el recurso de la URL; el error " +
        "nombra la línea concreta y se listan todos los productos desconocidos de una vez.\n" +
        "- 502 — Catalog.API no respondió. El pedido NO se ha creado: nada se escribe en la base hasta " +
        "que todas las líneas están resueltas. Es el acoplamiento síncrono de la Fase 2 en estado " +
        "puro, y desaparece en la Fase 3.")]
    [ProducesResponseType<OrderResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<OrderResponse>> Create(
        CreateOrderRequest request,
        CancellationToken cancellationToken)
    {
        // Agrupar ANTES de llamar a Catalog, no después: así dos líneas del mismo
        // producto cuestan una sola petición HTTP en vez de dos. Y agrupar es
        // obligatorio de todas formas — el constructor de Order rechaza un
        // ProductId repetido, porque esas líneas viajan dentro de ReserveStock en
        // 3.4 y un Inventory que reciba dos entradas del mismo producto tendría
        // que adivinar si reserva la suma. El agregado afirma la invariante;
        // arreglarla es trabajo de aquí.
        var lines = Group(request.Items);

        var resolved = new List<(CatalogProduct Product, int Quantity)>(lines.Count);

        try
        {
            // Una petición por línea, y en secuencia. *Descartado* un único
            // GET /products filtrando en cliente: traería el catálogo entero
            // —50 filas del seed de 1.4— para usar dos, y escondería el coste
            // real detrás de una sola llamada. *Descartado* también paralelizar
            // con Task.WhenAll: iría más rápido y haría el acoplamiento menos
            // visible, que es lo contrario de lo que este punto existe para
            // enseñar. Catalog no tiene endpoint batch, y eso también es parte de
            // la lección: nadie diseñó este consumo, salió de que Orders necesita
            // datos que no son suyos.
            foreach (var line in lines)
            {
                var product = await catalog.FindProductOrNullAsync(line.ProductId, cancellationToken);

                if (product is null)
                {
                    // Se anota y se sigue, en vez de cortar en el primer
                    // desconocido: un cuerpo con tres productos mal puestos
                    // devuelve los tres de una vez, igual que hacen las
                    // DataAnnotations con los campos. Cortar antes ahorraría
                    // llamadas, pero obligaría al cliente a arreglar de uno en
                    // uno.
                    AddUnknownProductError(line.FirstIndex, line.ProductId);
                    continue;
                }

                resolved.Add((product, line.Quantity));
            }
        }
        catch (CatalogUnavailableException exception)
        {
            return CatalogUnavailable(exception);
        }

        // Al llegar aquí, ModelState solo puede estar sucio por lo de arriba: el
        // filtro de [ApiController] ya devolvió 400 por su cuenta si fallaron las
        // DataAnnotations, así que la acción ni se habría ejecutado.
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        Order order;

        try
        {
            // Los cinco campos de cada línea: tres los trae Catalog (sku, nombre,
            // precio), la cantidad viene del cuerpo y el ProductId es el puntero
            // débil. Ese "quien construye una línea rellena los cinco" es la
            // regla que sobrevive a la Fase 3 — lo que cambia entonces es de
            // dónde salen los tres congelados, no que haya que rellenarlos.
            var items = resolved
                .Select(entry => new OrderItem(
                    entry.Product.Id,
                    entry.Product.Sku,
                    entry.Product.Name,
                    entry.Quantity,
                    entry.Product.Price))
                .ToList();

            order = new Order(request.CustomerEmail, items);
        }
        catch (ArgumentException exception)
        {
            return ToValidationProblem(exception);
        }

        db.Orders.Add(order);

        // Un solo SaveChanges para el pedido y sus líneas: OrderItem es un tipo
        // owned (2.2), así que EF inserta las filas de OrderItems sin que nadie
        // las añada a un DbSet. No hay transacción explícita porque SaveChanges
        // ya envuelve todo el lote en una.
        await db.SaveChangesAsync(cancellationToken);

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
    /// Es el mínimo: sin listado, sin paginación y sin filtros. 6.5 la amplía con
    /// lo que necesite la página de estado del pedido.
    /// </summary>
    [HttpGet("{id:guid}")]
    [EndpointSummary("Obtiene un pedido por su Id")]
    [EndpointDescription(
        "Devuelve el pedido con sus líneas y el total calculado. 404 si el Id no existe.\n\n" +
        "El Id es un Guid que acuña el propio servicio al crear el pedido, no la base de datos: desde " +
        "la Fase 4 es además la clave de correlación de la saga, así que este mismo valor es el que " +
        "sirve para seguir el pedido por RabbitMQ y por Jaeger.\n\n" +
        "En la Fase 2 el estado siempre es Pending: quien lo mueve es la máquina de estados de la " +
        "Fase 4.")]
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
    /// Colapsa las líneas repetidas sumando cantidades y se queda con el índice
    /// de la primera aparición de cada producto, que es el que necesitan los
    /// mensajes de error para poder señalar una línea del cuerpo original.
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
                group.First().index))
            .ToList();

    /// <summary>
    /// 400 y no 404, mismo criterio que el <c>UnknownCategory</c> de Catalog: el
    /// que no existe es un valor del **cuerpo**, no el recurso al que apunta la
    /// URL. Un 404 aquí diría que no existe el pedido, que es justo lo que se
    /// está intentando crear.
    ///
    /// La clave nombra la línea con la misma forma que genera la validación de
    /// MVC sobre una colección (<c>Items[0].ProductId</c>), para que el cliente no
    /// tenga que distinguir dos formatos de error de entrada. El índice es el de
    /// la **primera aparición** en el cuerpo original: si el producto venía
    /// repetido, la agrupación ya juntó sus líneas.
    /// </summary>
    private void AddUnknownProductError(int index, int productId)
    {
        ModelState.AddModelError(
            $"{nameof(CreateOrderRequest.Items)}[{index}].{nameof(CreateOrderItemRequest.ProductId)}",
            $"No existe el producto {productId} en el catálogo.");
    }

    /// <summary>
    /// Segunda línea de defensa del 400, igual que en Catalog (1.3). Las
    /// DataAnnotations cubren la forma del cuerpo, pero los constructores de
    /// <see cref="Order"/> y <see cref="OrderItem"/> validan cosas que el DTO no
    /// puede ver — un <c>customerEmail</c> de solo espacios, o un sku que Catalog
    /// devolvió más largo que <c>OrderItem.ProductSkuMaxLength</c>, que es
    /// precisamente el fallo que debe salir cuando los dos servicios dejan de
    /// encajar. Sin este catch, ese cuerpo sería un 500.
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
    /// El 502 del acoplamiento. Usa <c>Problem(...)</c> y no
    /// <c>StatusCode(502, new ProblemDetails { … })</c> como hace Catalog con su
    /// 409: <c>Problem</c> pasa por el <c>ProblemDetailsFactory</c>, que pone el
    /// content-type <c>application/problem+json</c> y añade el <c>traceId</c> —
    /// que es exactamente lo que la Fase 7 querrá para cruzar este fallo con la
    /// traza de Jaeger.
    ///
    /// El mensaje de la excepción **no** se copia al detalle: puede llevar la URL
    /// interna del servicio. Lo que sí se dice, y es lo importante para el
    /// cliente, es que el pedido no se ha creado.
    /// </summary>
    private ObjectResult CatalogUnavailable(CatalogUnavailableException exception)
    {
        // Se registra con el detalle completo, que es donde sí interesa verlo: el
        // log es de quien opera el servicio, la respuesta es de quien lo consume.
        logger.LogError(exception, "No se pudo validar el pedido contra Catalog.API.");

        return Problem(
            statusCode: StatusCodes.Status502BadGateway,
            title: "Catalog no disponible",
            detail:
                "No se pudieron consultar los productos en Catalog.API, así que el pedido no se ha " +
                "creado. Vuelve a intentarlo cuando el servicio de catálogo responda.");
    }

    /// <summary>
    /// Una línea del cuerpo ya agrupada: qué producto, cuántas unidades en total
    /// y en qué posición del cuerpo original apareció por primera vez.
    /// </summary>
    private sealed record RequestedLine(int ProductId, int Quantity, int FirstIndex);
}
