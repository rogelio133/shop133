namespace Shop133.Web.Gateway;

/// <summary>
/// Lo que contesta el 201 de <c>POST /api/orders</c>.
///
/// **Declara SOLO lo que este frontend usa.** <c>OrderResponse</c> trae ademas <c>CreatedAt</c> y
/// las lineas del pedido; <c>System.Text.Json</c> ignora en silencio lo que el destino no tiene,
/// asi que anadir campos "por si acaso" seria inventar consumidores. Precedente literal:
/// <see cref="CatalogProduct"/> re-declara 4 de los 9 campos de <c>ProductResponse</c>.
///
/// **<see cref="Id"/> es lo que hace innecesaria la cabecera <c>Location</c>, y eso no es un
/// detalle.** Ese 201 sale con <c>Location: http://localhost:5189/orders/{id}</c> — la direccion
/// REAL del servicio, sin el prefijo publico del Gateway: el servicio no conoce su prefijo y YARP
/// no trae transform de respuesta que lo reescriba. Es deuda medida en 5.1 y releida en 5.3, 6.1,
/// 6.2 y 6.3, que aviso de que en 6.4 dejaba de ser teorica. Sigue SIN DUENO: este frontend la
/// esquiva leyendo el id del cuerpo —que necesita de todas formas—, pero cualquier otro cliente
/// que siga la cabecera se sale del Gateway y se come la regla 3.
///
/// <see cref="Status"/> llega siempre como <c>"Pending"</c> y eso es correcto, no un fallo: la
/// saga acaba de arrancar. Verlo moverse a <c>Confirmed</c> o <c>Cancelled</c> es 6.5.
/// </summary>
public sealed record PlacedOrder
{
    public required Guid Id { get; init; }

    public required string CustomerEmail { get; init; }

    public required string Status { get; init; }

    public required decimal Total { get; init; }
}
