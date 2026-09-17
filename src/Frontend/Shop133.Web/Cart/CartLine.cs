using System.Text.Json.Serialization;

namespace Shop133.Web.Cart;

/// <summary>
/// Una linea del carrito: la FOTO de un producto tal y como estaba cuando se anadio.
///
/// Los cinco primeros campos son exactamente los cinco de <c>OrderLine</c> —el DTO que viaja
/// dentro de <c>OrderCreated</c> desde 0.3— y los mismos que pide <c>CreateOrderItemRequest</c>
/// en el cuerpo de <c>POST /orders</c> desde 3.3. No es casualidad ni parecido: es que ESTE tipo
/// es quien acuna esa foto, y 6.4 la copiara campo a campo al cuerpo del pedido.
///
/// **Y ahi esta el punto entero de 6.3.** Desde 3.3 el precio viaja en el cuerpo, asi que quien
/// guarda el carrito es quien dicta el importe. Guardado en SESION DE SERVIDOR, esos cuatro
/// campos los rellena <c>CartController.Add</c> leyendo Catalog por el Gateway —lo que la regla 3
/// permite— y el navegador no los ve ni los manda nunca. En una cookie los acunaria el navegador,
/// y el cliente volveria a dictar el precio con 4.8 como unica defensa. Ver la decision 2b de
/// docs/fase_3_3.md: 6.3 y 8.1 deciden QUIEN puede mandar la foto, 4.8 decide si la foto es
/// cierta; hacen falta las dos.
///
/// <c>record</c> con <c>init</c> como el resto de los modelos del proyecto: una linea no se muta,
/// se reemplaza. Cambiar la cantidad es construir otra — asi el precio congelado no puede
/// modificarse por descuido al tocar la cantidad, que es la unica cosa que el usuario SI decide.
/// </summary>
public sealed record CartLine
{
    /// <summary>
    /// El id de <c>CatalogDb</c>. Puntero DEBIL, no clave foranea: Catalog borra productos
    /// fisicamente desde 1.3, asi que el producto de esta linea puede dejar de existir mientras
    /// el carrito sigue vivo. Esa es justamente la razon de que los tres campos siguientes esten
    /// congelados aqui en vez de releerse.
    /// </summary>
    public required int ProductId { get; init; }

    /// <summary>El codigo tal y como estaba al anadir. Congelado.</summary>
    public required string ProductSku { get; init; }

    /// <summary>El nombre tal y como estaba al anadir. Congelado.</summary>
    public required string ProductName { get; init; }

    /// <summary>
    /// El precio al que se anadio, no el que tenga el catalogo dentro de una hora.
    ///
    /// **No se vuelve a leer nunca** (decision 3 de 6.3): un carrito ES una foto, y revalidar en
    /// cada render costaria una llamada al Gateway POR LINEA y cambiaria el precio bajo los pies
    /// de quien esta comprando. La limitacion que eso deja, dicha en voz alta: la ventana de
    /// autenticidad de 4.8 son 30 minutos (<c>PricingSnapshotWindowMinutes</c>), asi que una linea
    /// anadida hace mas de media hora sera rechazada por Catalog y el pedido se cancelara solo en
    /// 6.4/6.5 — que es exactamente lo que esos puntos existen para ensenar.
    /// </summary>
    public required decimal UnitPrice { get; init; }

    /// <summary>
    /// Cuantas unidades. Es el UNICO campo de la linea que decide el usuario, y el unico que
    /// <see cref="ShoppingCart"/> deja cambiar despues.
    /// </summary>
    public required int Quantity { get; init; }

    /// <summary>
    /// **El unico campo que NO forma parte de la foto**, y por eso esta anotado en vez de
    /// disimulado: no viaja a <c>OrderLine</c> ni lo mirara 6.4. Existe para que la pagina del
    /// carrito pueda pintar la miniatura sin volver a llamar al catalogo — que es lo mismo que
    /// hace la card de 6.2 con la respuesta que ya tiene en la mano.
    ///
    /// Anulable porque lo es en origen (<c>CreateProductRequest.ImageUrl</c> es opcional).
    /// </summary>
    public string? ImageUrl { get; init; }

    /// <summary>
    /// Lo que cuesta esta linea. CALCULADO, nunca almacenado — precedente literal de
    /// <c>OrderItem.Subtotal</c> en 2.1: un solo origen de verdad, por el mismo motivo por el que
    /// nada descuenta <c>Product.Stock</c> al comprar.
    ///
    /// El <c>[JsonIgnore]</c> no es adorno. Sin el, <c>System.Text.Json</c> ESCRIBE esta propiedad
    /// al serializar el carrito a la sesion (es un getter publico) y no puede leerla de vuelta —
    /// dejando en el almacen un numero que ya nadie actualiza y que puede contradecir a sus
    /// propias lineas. Un dato que solo se escribe es peor que no tenerlo.
    /// </summary>
    [JsonIgnore]
    public decimal Subtotal => UnitPrice * Quantity;
}
