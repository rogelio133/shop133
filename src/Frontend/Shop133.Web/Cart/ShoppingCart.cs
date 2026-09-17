using System.Text.Json.Serialization;

using Shop133.Web.Models;

namespace Shop133.Web.Cart;

/// <summary>
/// El carrito de un visitante, tal y como vive en la SESION DE SERVIDOR.
///
/// Se llama <c>ShoppingCart</c> y no <c>Cart</c> por una razon prosaica y que cuesta una tarde:
/// un tipo <c>Cart</c> dentro de un namespace acabado en <c>.Cart</c> convierte cada
/// <c>@model Cart</c> de una vista en una ambiguedad entre el tipo y el namespace, y el error de
/// Razor no dice eso. <c>CartLine</c>, <c>CartStore</c> y <c>CartController</c> no colisionan y se
/// quedan con su nombre corto.
///
/// **Es mutable, al reves que casi todo en este proyecto**, y esa es la decision: un carrito es
/// estado vivo de una sesion, no un hecho historico. Las LINEAS si son inmutables
/// (<see cref="CartLine"/> es un <c>record</c> con <c>init</c>), asi que el precio congelado no se
/// puede tocar cambiando la cantidad: cambiar una cantidad construye otra linea.
///
/// **Sostiene las mismas invariantes que el cuerpo de <c>POST /orders</c>, DUPLICADAS a mano.**
/// Importarlas exigiria un <c>ProjectReference</c> a <c>Orders.API</c>: la regla 3 de CLAUDE.md
/// rota de frente, con <c>Frontend_DoesNotReference_ServicesOrGateway</c> en rojo desde 0.6. Es el
/// precedente literal de <c>OrderItem.ProductSkuMaxLength</c>, que repite el numero de
/// <c>Product.SkuMaxLength</c> en vez de importarlo por la regla hermana entre servicios — y como
/// alli, pueden divergir: un carrito solo tiene que producir un cuerpo que la API de HOY acepte.
///
/// El motivo de sostenerlas AQUI y no dejar que la API las rechace en 6.4: un carrito que puede
/// construir un cuerpo invalido se lo cuenta al usuario al tramitar el pedido, tres pantallas
/// despues de donde se equivoco.
/// </summary>
public sealed class ShoppingCart
{
    /// <summary>
    /// Cuantos productos DISTINTOS caben. Copia de <c>[MaxLength(50)]</c> sobre
    /// <c>CreateOrderRequest.Items</c>, cuyo motivo desde 3.3 es que ese cuerpo se convierte en un
    /// mensaje de RabbitMQ y cada linea lleva ademas sku y nombre.
    /// </summary>
    public const int MaxLines = 50;

    /// <summary>
    /// Cuantas unidades caben en UNA linea. Copia de <c>[Range(1, 10_000)]</c> sobre
    /// <c>CreateOrderItemRequest.Quantity</c>, que alli no es una regla de negocio sino una guarda
    /// de forma: con 50 lineas al maximo, el peor caso son 500.000 unidades, lejos de
    /// <c>int.MaxValue</c>.
    /// </summary>
    public const int MaxQuantityPerLine = 10_000;

    /// <summary>
    /// El tope de arriba ya formateado para enseñarlo.
    ///
    /// **Con la cultura de <see cref="Money"/> y no con un <c>:N0</c> pelado**, que es la misma
    /// trampa que aquel tipo documenta: este proceso no configura localizacion en ninguna parte,
    /// asi que la cultura ambiente es la que diga el Windows de quien ejecute — y el separador de
    /// millares con ella. El mismo mensaje diria "10,000" o "10.000" segun la maquina.
    /// </summary>
    private static string MaxQuantityText => MaxQuantityPerLine.ToString("N0", Money.Culture);

    /// <summary>
    /// Las lineas, en el orden en que se anadieron.
    ///
    /// **Es una <c>List&lt;T&gt;</c> desnuda y NO un <c>AsReadOnly()</c> como el <c>Order.Items</c>
    /// de 2.1**, y la diferencia esta en la premisa, no en el descuido. Alli la proteccion existe
    /// porque <c>Order</c> es un agregado cuyas invariantes tienen que aguantar venga de donde
    /// venga la llamada — y 2.1 midio que un <c>IReadOnlyList</c> no protege nada, porque el
    /// objeto sigue siendo una <c>List</c> por debajo. Aqui el tipo entero vive dentro de una
    /// peticion de este proceso, y una coleccion de solo lectura ademas impediria que
    /// <c>System.Text.Json</c> lo reconstruyera al leerlo de la sesion.
    ///
    /// Lo que si se conserva de 2.1 es la proteccion que importa: las lineas son inmutables una a
    /// una, asi que el precio congelado no se toca aunque esta lista este abierta.
    /// </summary>
    public List<CartLine> Lines { get; init; } = [];

    /// <summary>
    /// El total del carrito. CALCULADO, nunca almacenado — precedente <c>Order.Total</c> (2.1), y
    /// con el mismo <c>[JsonIgnore]</c> y el mismo motivo que <see cref="CartLine.Subtotal"/>.
    /// </summary>
    [JsonIgnore]
    public decimal Total => Lines.Sum(line => line.Subtotal);

    /// <summary>
    /// Unidades totales, que es lo que cuenta el badge del navbar. **No es
    /// <c>Lines.Count</c>**: quien lleva tres tazas iguales espera ver un 3, no un 1.
    /// </summary>
    [JsonIgnore]
    public int UnitCount => Lines.Sum(line => line.Quantity);

    [JsonIgnore]
    public bool IsEmpty => Lines.Count == 0;

    public CartLine? Find(int productId) => Lines.FirstOrDefault(line => line.ProductId == productId);

    /// <summary>
    /// Anade una foto al carrito, SUMANDO la cantidad si ese producto ya estaba.
    ///
    /// Sumar —y no anadir una segunda linea del mismo producto— es lo mismo que hace
    /// <c>OrdersController</c> antes de construir el <c>Order</c>, y no es una comodidad: el
    /// constructor de <c>Order</c> PROHIBE dos lineas con el mismo <c>ProductId</c> desde 2.1,
    /// porque esas lineas viajan dentro de <c>ReserveStock</c> y un Inventory que recibe dos
    /// entradas del mismo producto tiene que adivinar si reserva la suma o si la segunda es un
    /// duplicado. Un carrito que las dejara separadas produciria un 400 en 6.4.
    ///
    /// **Al sumar se conserva el precio de la linea que ya estaba**, no el de la foto nueva. Es lo
    /// contrario de lo que parece razonable y es deliberado: el precio de una linea se congela la
    /// primera vez y no se mueve, que es lo que hace del carrito una foto. Si Catalog cambio el
    /// precio entre el primer "anadir" y el segundo, la que caduca es esa foto, y quien lo dice es
    /// 4.8 al validarla — no este metodo por su cuenta.
    ///
    /// Devuelve el motivo del rechazo, o <c>null</c> si entro. **No lanza**: superar un tope es
    /// algo que hace un usuario con un formulario, no una incoherencia del programa. Es la misma
    /// forma que <c>StockItem.CanReserve</c> (3.4), un predicado que no revienta, y el texto es
    /// para leer, igual que el <c>Reason</c> de <c>StockRejected</c>.
    /// </summary>
    public string? Add(CartLine line)
    {
        if (line.Quantity < 1)
        {
            return "La cantidad tiene que ser al menos 1.";
        }

        var index = IndexOf(line.ProductId);

        if (index < 0)
        {
            if (Lines.Count >= MaxLines)
            {
                return $"El carrito no admite más de {MaxLines} productos distintos.";
            }

            if (line.Quantity > MaxQuantityPerLine)
            {
                return $"No se pueden pedir más de {MaxQuantityText} unidades de un producto.";
            }

            Lines.Add(line);
            return null;
        }

        var existing = Lines[index];

        // long a proposito: dos int en el limite se desbordarian al sumarse, y el desbordamiento
        // daria un numero NEGATIVO que pasaria la comprobacion de abajo sin despeinarse.
        var total = (long)existing.Quantity + line.Quantity;

        if (total > MaxQuantityPerLine)
        {
            return $"Ya hay {existing.Quantity} en el carrito y no se pueden pedir más de " +
                   $"{MaxQuantityText} unidades de un producto.";
        }

        // Se reemplaza la linea entera en su sitio: CartLine es inmutable, y conservar la posicion
        // evita que anadir uno mas de algo que ya estaba lo mande al final de la tabla.
        Lines[index] = existing with { Quantity = (int)total };
        return null;
    }

    /// <summary>
    /// Fija la cantidad de una linea. Una cantidad de 0 o menos QUITA la linea — es lo que espera
    /// quien teclea 0 en la casilla, y evita un carrito con lineas de cero unidades que 6.4
    /// tendria que filtrar antes de mandar el pedido.
    ///
    /// Devuelve el motivo del rechazo o <c>null</c>. Un producto que no esta en el carrito tambien
    /// es un motivo, y no un silencio: llega de un formulario de una pagina que alguien dejo
    /// abierta mientras vaciaba el carrito en otra pestana.
    /// </summary>
    public string? SetQuantity(int productId, int quantity)
    {
        var index = IndexOf(productId);

        if (index < 0)
        {
            return "Ese producto ya no está en el carrito.";
        }

        if (quantity <= 0)
        {
            Lines.RemoveAt(index);
            return null;
        }

        if (quantity > MaxQuantityPerLine)
        {
            return $"No se pueden pedir más de {MaxQuantityText} unidades de un producto.";
        }

        Lines[index] = Lines[index] with { Quantity = quantity };
        return null;
    }

    /// <summary>Quita la linea. Devuelve la que se quito, o <c>null</c> si no estaba.</summary>
    public CartLine? Remove(int productId)
    {
        var index = IndexOf(productId);

        if (index < 0)
        {
            return null;
        }

        var removed = Lines[index];
        Lines.RemoveAt(index);

        return removed;
    }

    public void Clear() => Lines.Clear();

    /// <summary>
    /// Por posicion y no con <c>Lines.IndexOf(linea)</c>: <see cref="CartLine"/> es un
    /// <c>record</c>, asi que <c>IndexOf</c> buscaria por IGUALDAD DE VALOR —los seis campos— y no
    /// por identidad de producto. Hoy da lo mismo porque el <c>ProductId</c> es unico en el
    /// carrito; el dia que no lo fuera, la diferencia seria un reemplazo en la linea equivocada.
    /// </summary>
    private int IndexOf(int productId) => Lines.FindIndex(line => line.ProductId == productId);
}
