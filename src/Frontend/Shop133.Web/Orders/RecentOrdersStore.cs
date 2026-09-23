using System.Text.Json;

namespace Shop133.Web.Orders;

/// <summary>
/// Los pedidos tramitados desde esta sesion, para que el navbar tenga adonde llevar.
///
/// ── Esto releé la invariante que 6.3 dejo escrita, no la rompe ──
///
/// El <c>///</c> de <c>CartStore</c> dice que aquel es "el UNICO sitio del proyecto que toca
/// <c>ISession</c>", y aqui hay un segundo. Merece decirse en voz alta por que no es una
/// contradiccion: aquella regla defendia una propiedad concreta —que el PRECIO no salga al
/// navegador— y lo que la sostiene es que haya **un solo archivo capaz de romper cada cosa que se
/// guarda**, no que haya un solo archivo en total. Repartir la clave del carrito entre tres sitios
/// era el peligro; tener una clave por cosa guardada, cada una con su duenno, es lo contrario.
///
/// Aqui ademas no hay nada que proteger: lo que se guarda son identificadores de pedidos que ya
/// existen y un total que ya cobro Orders. Aunque esto viviera en una cookie no cambiaria ninguna
/// autoridad — y aun asi va en sesion, porque no hay motivo para mandarle al navegador una lista
/// que el servidor ya tiene donde ponerla.
///
/// ── Lo que se pierde, dicho en la propia pagina ──
///
/// <c>AddDistributedMemoryCache</c> no es distribuido (6.3), asi que reiniciar el proceso vacia
/// esto igual que vacia los carritos. Un pedido "perdido" de esta lista NO esta perdido: existe en
/// Orders y su pagina sigue respondiendo si alguien conserva el identificador. La lista es una
/// comodidad, no el registro.
///
/// *Descartado* persistirlo de verdad (una tabla propia, una cookie firmada): sin autenticacion no
/// hay a quien atribuir un pedido, asi que cualquier persistencia seria "los pedidos de este
/// navegador" disfrazados de "mis pedidos". Eso es 8.1.
/// </summary>
public sealed class RecentOrdersStore(IHttpContextAccessor httpContextAccessor)
{
    /// <summary>
    /// Clave propia y distinta de la del carrito. Las dos conviven en la misma sesion.
    /// </summary>
    private const string SessionKey = "recent-orders";

    /// <summary>
    /// Cuantos se recuerdan. El tope existe para que la sesion no crezca sin fin en una demo donde
    /// se tramitan pedidos en serie; diez son mas que suficientes para volver al de hace un rato y
    /// caben de sobra en el almacen.
    /// </summary>
    private const int MaxRemembered = 10;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private List<RecentOrder>? _cached;

    /// <summary>
    /// Los pedidos de esta sesion, **el mas reciente primero**. Nunca devuelve <c>null</c>: "no hay
    /// ninguno" es el caso normal de quien acaba de llegar, no un fallo.
    /// </summary>
    public async Task<IReadOnlyList<RecentOrder>> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        var session = Session;
        await session.LoadAsync(cancellationToken);

        if (session.TryGetValue(SessionKey, out var bytes) && bytes.Length > 0)
        {
            // Una lista ilegible se trata como vacia, igual que hace CartStore con el carrito y por
            // lo mismo: basta con que una version anterior de este proceso dejara otra forma en la
            // sesion. Reventar por eso seria peor que perder una lista de comodidad.
            try
            {
                _cached = JsonSerializer.Deserialize<List<RecentOrder>>(bytes, SerializerOptions);
            }
            catch (JsonException)
            {
                _cached = null;
            }
        }

        return _cached ??= [];
    }

    /// <summary>
    /// Apunta un pedido recien tramitado y guarda, en una sola llamada.
    ///
    /// **Escribe de inmediato, al reves que <c>CartStore</c>**, que separa <c>GetAsync</c> de
    /// <c>SaveAsync</c> porque su objeto se muta varias veces por peticion (anadir, cambiar
    /// cantidad, quitar). Aqui solo existe una mutacion posible y ocurre una vez, justo despues del
    /// 201: partirla en dos llamadas seria una invitacion a olvidar la segunda y perder el pedido
    /// de la lista sin ningun error.
    ///
    /// Si el pedido ya estaba, se mueve al principio en vez de duplicarse. No deberia pasar —el id
    /// lo acuna Orders por pedido— pero un F5 sobre algo inesperado sale barato de sostener.
    /// </summary>
    public async Task RememberAsync(RecentOrder order, CancellationToken cancellationToken = default)
    {
        var current = await GetAsync(cancellationToken);

        var updated = new List<RecentOrder> { order };
        updated.AddRange(current.Where(candidate => candidate.Id != order.Id).Take(MaxRemembered - 1));

        var session = Session;
        session.Set(SessionKey, JsonSerializer.SerializeToUtf8Bytes(updated, SerializerOptions));

        _cached = updated;

        // CommitAsync por el mismo motivo que en CartStore: esto va SIEMPRE seguido de un redirect,
        // y sin el, el navegador puede pedir la pagina siguiente antes de que la sesion se escriba.
        await session.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Si esto lanza, falta <c>app.UseSession()</c>. Fallo ruidoso a proposito, igual que en
    /// <c>CartStore</c>: una lista que se olvida en silencio es peor que una pagina que revienta
    /// nombrando lo que falta.
    /// </summary>
    private ISession Session =>
        httpContextAccessor.HttpContext?.Session
        ?? throw new InvalidOperationException(
            "No hay HttpContext: los pedidos recientes solo se pueden leer o escribir dentro de una peticion.");
}
