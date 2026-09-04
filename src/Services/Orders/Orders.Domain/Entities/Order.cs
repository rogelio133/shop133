namespace Orders.Domain.Entities;

/// <summary>
/// Un pedido: quién compra, qué líneas lleva y en qué estado está.
///
/// Es la primera pieza de código de negocio de la Fase 2 y el agregado sobre el
/// que se monta el resto: 2.2 lo mapea a <c>OrdersDb</c>, 2.3 lo crea desde
/// <c>POST /orders</c>, 3.3 lo publica como <c>OrderCreated</c> y la saga de la
/// Fase 4 le mueve el <see cref="Status"/>.
///
/// Vive en Orders.Domain y no en Orders.Infrastructure, al revés que
/// <c>Product</c>. No es incoherencia: la decisión 1 de docs/fase_1_1.md explica
/// que Catalog no tiene proyecto de dominio porque es un CRUD — tres capas para
/// mover un nvarchar de la base al JSON. Orders sí lo tiene, porque aquí vive la
/// OrderStateMachine de la Fase 4, y un pedido con su máquina de estados al lado
/// es exactamente lo que ese proyecto existe para contener.
///
/// Sigue el mismo estilo que <c>Product</c>: clase mutable con setters privados,
/// no un record inmutable como los tipos de Shop133.Contracts. La distinción es
/// la de siempre — un mensaje es una foto que ya viajó; una entidad tiene
/// identidad y vida, y el estado de este pedido va a cambiar.
/// </summary>
public sealed class Order
{
    /// <summary>
    /// Máximo de RFC 5321 para una dirección de correo completa: 64 de parte
    /// local + 1 de arroba + 255 de dominio. Igual que las constantes de
    /// <c>Product</c>, es una sola fuente para tres sitios: la guarda de aquí,
    /// el <c>nvarchar(n)</c> de 2.2 y la validación del DTO de entrada de 2.3.
    /// </summary>
    public const int CustomerEmailMaxLength = 320;

    /// <summary>
    /// Las líneas del pedido. Campo de respaldo privado y no una propiedad con
    /// setter: es lo que impide que nadie de fuera reemplace la colección
    /// entera. EF Core lo descubrirá por convención en 2.2, por el nombre
    /// <c>_items</c> frente a la navegación <see cref="Items"/>.
    /// </summary>
    private readonly List<OrderItem> _items;

    public Order(string customerEmail, IEnumerable<OrderItem> items)
    {
        // El Id lo genera la entidad, no la base de datos — al revés que
        // Product, que lo recibe de un IDENTITY. Es la decisión 4 de
        // docs/fase_0_3.md y sigue viva: OrderId es la clave de correlación de
        // toda la saga, así que Orders.API tiene que poder publicar OrderCreated
        // sin haber esperado a un INSERT. Con IDENTITY habría que hacer
        // INSERT -> leer el id -> publicar, metiendo la base de datos en el
        // camino crítico de un flujo que existe justamente para ser asíncrono.
        //
        // Guid.NewGuid() y no Guid.CreateVersion7(): la defensa habitual de v7
        // es que el índice clustered deja de fragmentarse porque los ids salen
        // ordenados. Se midió en 1.1 y es falso en SQL Server, que compara
        // uniqueidentifier empezando por los ÚLTIMOS 6 bytes — justo donde v7
        // pone la parte aleatoria. Se pagaría la complejidad sin cobrar la
        // ventaja. Cómo se indexa esta columna se decide en 4.5, con la sonda de
        // docs/fase_1_1.md como material.
        Id = Guid.NewGuid();

        // Sin validación de formato: ni regex ni MailAddress.TryCreate. Mismo
        // criterio que la decisión 8 de 1.1 sobre ImageUrl — se valida lo que se
        // sabe (no vacío, longitud), no lo que se supone. El [EmailAddress] va
        // en el DTO de entrada de 2.3, que es donde viven las DataAnnotations.
        //
        // Tampoco se pasa a minúsculas, al contrario que Product.Sku. La parte
        // local de una dirección es sensible a mayúsculas según el RFC, así que
        // normalizarla es corregir un dato ajeno; y aquí no hay ninguna regla de
        // unicidad que sostener, que era el motivo entero de normalizar el Sku.
        CustomerEmail = Validated(customerEmail, CustomerEmailMaxLength, nameof(customerEmail));

        ArgumentNullException.ThrowIfNull(items);

        // ToList() antes de comprobar nada: 'items' es un IEnumerable y podría
        // ser una consulta perezosa que se recorre distinto cada vez. Se
        // materializa una sola vez y se valida esa copia.
        //
        // Y es a la vez la copia defensiva: la lista que pasó el llamante puede
        // seguir mutando después sin que el pedido se entere.
        var materialized = items.ToList();

        // Un pedido sin líneas no es un pedido. La guarda va aquí y no en un
        // AddItem() posterior porque un agregado se construye válido: si las
        // líneas se fueran añadiendo después, existiría una ventana en la que un
        // Order vacío está en el ChangeTracker y un SaveChanges lo escribiría.
        if (materialized.Count == 0)
        {
            throw new ArgumentException("Un pedido necesita al menos una línea.", nameof(items));
        }

        if (materialized.Any(item => item is null))
        {
            throw new ArgumentException("Ninguna línea del pedido puede ser null.", nameof(items));
        }

        // Dos líneas del mismo producto se rechazan en vez de sumarse. El motivo
        // no es de limpieza: en 3.4 estas líneas viajan dentro de ReserveStock,
        // y un Inventory.API que reciba dos entradas del mismo ProductId tiene
        // que decidir si reserva la suma o si la segunda es un duplicado — una
        // ambigüedad que no debería salir de aquí. Quien construye el pedido
        // (2.3) agrupa antes; el agregado solo afirma la invariante.
        var duplicated = materialized
            .GroupBy(item => item.ProductId)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicated is not null)
        {
            throw new ArgumentException(
                $"El producto {duplicated.Key} aparece en más de una línea; agrupa las cantidades antes de crear el pedido.",
                nameof(items));
        }

        _items = materialized;

        // Pending es el único estado alcanzable en la Fase 2, y no por
        // limitación: aceptar el pedido es lo único que Orders.API sabe hacer
        // hoy. Quien lo mueve es la saga de la Fase 4.
        Status = OrderStatus.Pending;

        // DateTimeOffset y no DateTime: mapea a datetimeoffset en 2.2 sin
        // ambigüedad de Kind, que es el bug clásico de guardar un DateTime local
        // y leerlo como Unspecified.
        //
        // UtcNow directo y no un TimeProvider inyectado (la respuesta "correcta"
        // desde .NET 8): añadiría un parámetro que todos los llamantes tienen
        // que arrastrar para que un test pueda afirmar un sello de tiempo que
        // ningún test afirma. Si 6.5 necesita ordenar por fecha con precisión de
        // test, se revisa entonces.
        CreatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Constructor que usa EF Core al materializar filas (2.2). Mismo motivo que
    /// el de <c>Product</c>: una fila ya persistida no se vuelve a validar.
    ///
    /// La lista se inicializa vacía en vez de con <c>null!</c> porque EF **la
    /// rellena**, no la reemplaza: al materializar las líneas hace Add sobre la
    /// colección que encuentre. Con null aquí, cargar un pedido reventaría con
    /// NullReferenceException.
    /// </summary>
    private Order()
    {
        CustomerEmail = null!;
        _items = [];
    }

    /// <summary>
    /// Clave de correlación de la saga y clave primaria del pedido. La genera la
    /// entidad al construirse — ver el comentario del constructor.
    /// </summary>
    public Guid Id { get; private set; }

    /// <summary>
    /// A quién se le notifica el desenlace. Viaja dentro de OrderConfirmed y
    /// OrderCancelled porque Notifications.API no puede leer OrdersDb (regla 1),
    /// que es la decisión 3 de docs/fase_0_3.md.
    /// </summary>
    public string CustomerEmail { get; private set; }

    /// <summary>
    /// En qué punto está el pedido. Se mueve con <see cref="Confirm"/> y
    /// <see cref="Cancel"/> desde 4.3; hasta entonces no había vía de mutación a
    /// propósito, porque no había caso de uso — el mismo criterio que dejó a
    /// <c>Product</c> sin <c>Update()</c> hasta que 1.3 lo necesitó.
    /// </summary>
    public OrderStatus Status { get; private set; }

    /// <summary>Cuándo se registró la intención de comprar. Siempre en UTC.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Las líneas, de solo lectura desde fuera. El tipo es
    /// <c>IReadOnlyList</c> y no <c>List</c> para que nadie añada ni quite una
    /// línea sin pasar por el agregado — la invariante de "sin ProductId
    /// repetido" no se puede sostener si la colección es pública y mutable.
    ///
    /// **Ningún <c>OrderItem</c> tiene Id propio, ni una referencia de vuelta al
    /// pedido.** Es una decisión de este punto: una línea de pedido no tiene
    /// identidad fuera de su pedido — nadie la pide por id y ningún mensaje de
    /// Shop133.Contracts la referencia (los contratos llevan ProductId, nunca un
    /// id de línea). Si en la base se mapea con clave sombra sobre una entidad
    /// normal o como <c>OwnsMany</c> lo decide 2.2, que es donde vive la
    /// persistencia; igual que 1.1 dejó el índice único del Sku para 1.2.
    ///
    /// **<c>AsReadOnly()</c> y no <c>=> _items</c> a secas.** Devolver el List
    /// directamente tipado como IReadOnlyList *parece* que protege, y no
    /// protege: medido, <c>(List&lt;OrderItem&gt;)order.Items</c> castea sin
    /// error y permite añadir una línea duplicada por la espalda, saltándose la
    /// invariante del constructor. IReadOnlyList declara lo que el llamante
    /// puede hacer, no lo que el objeto es. AsReadOnly() envuelve la lista en un
    /// ReadOnlyCollection, cuyo Add lanza NotSupportedException. Cuesta una
    /// asignación por lectura, que a esta escala no se nota.
    /// </summary>
    public IReadOnlyList<OrderItem> Items => _items.AsReadOnly();

    /// <summary>
    /// Lo que cuesta el pedido. **Calculado, no persistido**: una sola fuente de
    /// verdad, imposible de desincronizar de las líneas. Es el mismo criterio
    /// por el que nadie descuenta de <c>Product.Stock</c> al crear un pedido —
    /// dos sitios con el mismo número acaban discrepando.
    ///
    /// Descartado guardarlo en columna, que permitiría listar pedidos sin cargar
    /// las líneas y congelaría el total aunque la fórmula cambiara (impuestos,
    /// envío). Si eso llega, el total deja de ser "la suma de las líneas" y
    /// entonces sí merece columna propia; hoy no lo es.
    ///
    /// 2.2 tendrá que mapearlo con <c>Ignore()</c>: EF vería una propiedad
    /// decimal de solo lectura e intentaría crearle una columna.
    /// </summary>
    public decimal Total => _items.Sum(item => item.Subtotal);

    /// <summary>
    /// Da el pedido por bueno: stock reservado y cobro aceptado.
    ///
    /// Lo llama <c>OrderConfirmedConsumer</c> (Orders.API, 4.3) al recibir el
    /// <c>OrderConfirmed</c> que publica la saga. **No lo llama la saga**: vive en
    /// este mismo proyecto pero no puede tocar <c>OrdersDbContext</c> —la flecha va
    /// .API → .Infrastructure → .Domain, regla 5—, así que el camino entre "la saga
    /// terminó" y "la fila cambió" pasa obligatoriamente por un mensaje y un
    /// consumer. Es el precio de la regla, y hace visible que hay una ventana entre
    /// las dos cosas.
    /// </summary>
    public void Confirm() => TransitionTo(OrderStatus.Confirmed);

    /// <summary>
    /// Cierra el pedido sin completarlo. Lo llama <c>OrderCancelledConsumer</c>.
    ///
    /// **No recibe el motivo**, aunque <c>OrderCancelled</c> lo traiga: el pedido
    /// no distingue por qué se canceló —lo dice el <c>///</c> de
    /// <see cref="OrderStatus.Cancelled"/> desde 2.1— y guardarlo aquí sería una
    /// columna nueva, una migración y un texto duplicado del que ya viaja en el
    /// evento hacia Notifications (4.6). Si algún día la interfaz tiene que
    /// enseñarle al cliente por qué se canceló su pedido, entra entonces con su
    /// caso de uso delante.
    /// </summary>
    public void Cancel() => TransitionTo(OrderStatus.Cancelled);

    /// <summary>
    /// La única puerta por la que cambia <see cref="Status"/>.
    ///
    /// **Solo se sale de <see cref="OrderStatus.Pending"/>**: los otros dos estados
    /// son finales, así que cualquier otra transición —incluida la que va a donde
    /// ya se está— lanza. No es rigidez: un <c>Confirm()</c> sobre un pedido ya
    /// cancelado significa que la saga y la base no cuentan la misma historia, y
    /// eso hay que verlo, no absorberlo.
    ///
    /// Y por eso **el duplicado no se distingue aquí**. Recibir dos veces el mismo
    /// <c>OrderConfirmed</c> es normal —RabbitMQ entrega al menos una vez— y no es
    /// una incoherencia; quien lo reconoce es el consumer, que comprueba el estado
    /// y sale en silencio antes de llegar a esta línea. Si esta excepción salta, es
    /// que la guarda del consumer falló. Mezclar las dos cosas dejando pasar la
    /// transición a sí mismo haría que el fallo real se colara con el duplicado
    /// legítimo.
    /// </summary>
    private void TransitionTo(OrderStatus target)
    {
        if (Status != OrderStatus.Pending)
        {
            throw new InvalidOperationException(
                $"El pedido {Id} está en {Status} y ese estado es final: no puede pasar a {target}. " +
                "Un duplicado del mensaje lo tiene que reconocer el consumer antes de llegar aquí; " +
                "si esto salta, la saga y OrdersDb no cuentan la misma historia.");
        }

        Status = target;
    }

    private static string Validated(string value, int maxLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        var trimmed = value.Trim();

        if (trimmed.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                trimmed.Length,
                $"El valor supera el máximo de {maxLength} caracteres.");
        }

        return trimmed;
    }
}
