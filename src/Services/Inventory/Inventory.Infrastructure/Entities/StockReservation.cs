namespace Inventory.Infrastructure.Entities;

/// <summary>
/// El registro de que un pedido concreto tiene stock comprometido, y de cuánto
/// de cada producto.
///
/// **Es la pieza que existe para que 4.4 pueda decidir.** La sección Pendiente
/// de docs/fase_3_2.md dejó abierto si <c>ReleaseStock</c> puede prescindir de
/// <c>Lines</c> y soltar solo con el <c>OrderId</c>, y dijo por escrito que no
/// se decidía "antes de saber cómo quedó la tabla de reservas". Quedó así: la
/// clave primaria **es** el <c>OrderId</c>, y las líneas cuelgan de ella. Con
/// esto, un ReleaseStock que solo lleve OrderId tiene toda la información que
/// necesita — menos datos en la compensación es menos superficie para el
/// duplicado que el propio <c>///</c> de ReleaseStock advierte.
///
/// Que la PK sea el OrderId tiene un segundo efecto, buscado: dos
/// <c>OrderCreated</c> del mismo pedido no pueden crear dos reservas. Eso es
/// idempotencia **de negocio**, por clave de pedido, y no sustituye a la de
/// transporte por <c>MessageId</c> del sobre, que sigue siendo 3.6.
/// </summary>
public sealed class StockReservation
{
    /// <summary>
    /// Campo de respaldo privado, no una propiedad con setter: es lo que impide
    /// que nadie de fuera reemplace la colección entera. EF lo lee y lo escribe
    /// por el campo, declarado explícitamente en la configuración de 3.4.
    /// </summary>
    private readonly List<StockReservationLine> _lines;

    public StockReservation(Guid orderId, IEnumerable<StockReservationLine> lines)
    {
        // El Guid no se acuña aquí, al revés que en Order: lo acuñó Orders.API y
        // llegó dentro de OrderCreated. Es la clave de correlación de la saga y
        // Inventory solo la copia — inventar una identidad propia obligaría a
        // mantener un índice para volver a encontrar la reserva por pedido, que
        // es la única forma en que alguien la va a buscar.
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("El OrderId de una reserva no puede ser Guid.Empty.", nameof(orderId));
        }

        ArgumentNullException.ThrowIfNull(lines);

        // ToList() antes de comprobar nada, por el mismo motivo que en Order:
        // 'lines' es un IEnumerable que podría ser una consulta perezosa y
        // recorrerse distinto cada vez. Se materializa una vez y se valida esa
        // copia — que es además la copia defensiva.
        var materialized = lines.ToList();

        if (materialized.Count == 0)
        {
            throw new ArgumentException("Una reserva necesita al menos una línea.", nameof(lines));
        }

        if (materialized.Any(line => line is null))
        {
            throw new ArgumentException("Ninguna línea de la reserva puede ser null.", nameof(lines));
        }

        // Misma invariante que Order: sin ProductId repetido. Aquí no es una
        // ambigüedad heredada sino aritmética — dos líneas del mismo producto
        // harían que 4.4 devolviera unidades en dos pasos sobre el mismo
        // StockItem, y la segunda no sabría si es un duplicado.
        var duplicated = materialized
            .GroupBy(line => line.ProductId)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicated is not null)
        {
            throw new ArgumentException(
                $"El producto {duplicated.Key} aparece en más de una línea de la reserva.",
                nameof(lines));
        }

        OrderId = orderId;
        _lines = materialized;

        // DateTimeOffset y no DateTime: mapea a datetimeoffset sin ambigüedad de
        // Kind. UtcNow directo y no un TimeProvider inyectado, igual que en
        // Order — ningún test afirma este sello.
        CreatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Constructor que usa EF Core al materializar filas.
    ///
    /// La lista se inicializa vacía y **nunca con <c>null!</c>**: EF *rellena*
    /// la colección que encuentra en el campo, no la reemplaza. Con null aquí,
    /// leer una reserva reventaría con NullReferenceException — medido en 2.1.
    /// </summary>
    private StockReservation()
    {
        _lines = [];
    }

    /// <summary>
    /// Clave primaria, y es el id del pedido. Se mapea con
    /// <c>ValueGeneratedNever()</c>: el valor lo puso Orders.
    /// </summary>
    public Guid OrderId { get; private set; }

    /// <summary>Cuándo se comprometió el stock. Siempre en UTC.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Cuándo se devolvieron las unidades, o <c>null</c> si la reserva sigue viva.
    /// Lo sella <see cref="Release"/> desde 4.4.
    ///
    /// **Nullable a propósito: <c>null</c> significa reserva viva.** No hay un enum
    /// de estado con dos valores porque la fecha ya dice las dos cosas —si hay
    /// sello, se liberó— y un enum al lado obligaría a mantenerlos de acuerdo.
    /// Mismo criterio que <c>Payment</c>, que no tiene un bool además de su Status.
    /// </summary>
    public DateTimeOffset? ReleasedAt { get; private set; }

    /// <summary>
    /// Las líneas, de solo lectura desde fuera.
    ///
    /// <c>AsReadOnly()</c> y no <c>=&gt; _lines</c> a secas: devolver el List
    /// tipado como IReadOnlyList *parece* que protege y no protege — medido en
    /// 2.1, el cast a <c>ICollection&lt;T&gt;</c> compila y permite añadir una
    /// línea por la espalda saltándose la invariante del constructor.
    /// AsReadOnly() envuelve en un ReadOnlyCollection cuyo Add lanza.
    /// </summary>
    public IReadOnlyList<StockReservationLine> Lines => _lines.AsReadOnly();

    /// <summary>
    /// Marca la reserva como liberada. No toca los <c>StockItem</c> — de devolver
    /// las unidades se encarga el consumer, llamando a <c>StockItem.Release(...)</c>
    /// por cada línea; esta entidad solo sabe de su propia fila.
    ///
    /// **Lanza si ya estaba liberada, y eso es intencionadamente hostil.** Es la
    /// misma disciplina que <c>Order.Confirm()</c>/<c>Cancel()</c> de 4.3:
    /// reconocer un duplicado es trabajo del *consumer*, que mira
    /// <see cref="ReleasedAt"/> y vuelve antes de tocar la entidad. Si esta
    /// excepción salta, o falló aquella guarda o InventoryDb y la saga discrepan —
    /// las dos cosas son incoherencias que hay que ver, no absorber. Dejar pasar la
    /// segunda llamada mezclaría el duplicado legítimo con el fallo real, y aquí el
    /// fallo real significa haber soltado el stock dos veces.
    ///
    /// *Descartado* borrar la fila en vez de marcarla, que sería más simple y
    /// daría idempotencia "por ausencia". Tres motivos: destruye la evidencia de
    /// que la compensación ocurrió —que es justo lo que la Fase 4 existe para
    /// demostrar—; rompe la guarda de negocio de OrderCreatedConsumer, porque un
    /// OrderCreated reentregado con MessageId nuevo no encontraría reserva y
    /// **volvería a reservar stock para un pedido cancelado**; y con la fila viva,
    /// el camino de liberación tiene su propia guarda de negocio por el mismo
    /// mecanismo que el de reserva, en vez de por uno distinto.
    /// </summary>
    public void Release()
    {
        if (ReleasedAt is not null)
        {
            throw new InvalidOperationException(
                $"La reserva del pedido {OrderId} ya se liberó el {ReleasedAt:O}.");
        }

        ReleasedAt = DateTimeOffset.UtcNow;
    }
}
