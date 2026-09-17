using System.Text.Json;

namespace Shop133.Web.Cart;

/// <summary>
/// Lee y escribe el carrito en la SESION DE SERVIDOR, y es el UNICO sitio del proyecto que toca
/// <see cref="ISession"/>.
///
/// **Esa exclusividad es el punto de que exista.** El titulo de 6.3 dice "en sesion de servidor,
/// no en cookie", y esa es una propiedad que se sostiene mientras solo haya un archivo capaz de
/// romperla. Repartir <c>HttpContext.Session.Get(...)</c> entre el controller, el view component
/// y alguna vista dejaria la clave y el formato en tres sitios, y bastaria con que uno decidiera
/// escribir en <c>Response.Cookies</c> para que el precio volviera al navegador sin que nada
/// avisara.
///
/// **Que hay realmente en la cookie**: un identificador opaco, nada mas. Los datos —el
/// <c>UnitPrice</c> incluido— viven en el <c>IDistributedCache</c> de este proceso. Es la
/// diferencia entera entre las dos opciones que el roadmap plantea: con el carrito en cookie, el
/// precio lo acuna y lo devuelve el navegador, y entonces 4.8 se queda como unica defensa contra
/// un importe inventado. Ver la decision 2b de docs/fase_3_3.md.
///
/// Se registra como SCOPED y cachea la instancia durante la peticion: el controller y
/// <c>CartBadgeViewComponent</c> leen el carrito en la misma peticion, y sin la cache lo
/// deserializarian dos veces y —peor— trabajarian sobre dos objetos distintos, de modo que una
/// mutacion del controller no se veria en el badge que se pinta despues.
///
/// *Descartado* un metodo de extension estatico sobre <c>ISession</c>: no puede cachear nada, y
/// repartiria la clave y las opciones de JSON por los archivos que lo llamen.
/// </summary>
public sealed class CartStore(IHttpContextAccessor httpContextAccessor)
{
    /// <summary>
    /// La clave dentro de la sesion. Corta a proposito: viaja en cada lectura del almacen.
    /// </summary>
    private const string SessionKey = "cart";

    /// <summary>
    /// Sin indentar y con los nombres en camelCase. No se comparte con nada —esto no es un
    /// contrato, es el formato privado de una sesion—, asi que lo unico que importa es que el
    /// mismo proceso pueda leer lo que escribio.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private ShoppingCart? _cached;

    /// <summary>
    /// El carrito de esta sesion, o uno vacio si todavia no hay ninguno. Nunca devuelve
    /// <c>null</c>: un carrito vacio y un carrito inexistente son la misma cosa para quien compra,
    /// y distinguirlos obligaria a cada llamante a repetir el mismo <c>??</c>.
    ///
    /// Es <c>async</c> por el <c>LoadAsync()</c>, que es lo que carga la sesion del almacen sin
    /// bloquear. Los accesos sincronos de <c>ISession</c> tambien funcionan —hacen esa misma carga
    /// por detras y la esperan—, pero contra un <c>IDistributedCache</c> de verdad eso seria I/O
    /// sincrono dentro de una peticion. Hoy el almacen es memoria y da igual; el dia que no lo
    /// sea, la forma correcta ya esta escrita.
    /// </summary>
    public async Task<ShoppingCart> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        var session = Session;
        await session.LoadAsync(cancellationToken);

        // TryGetValue y no Get: el segundo devuelve null para "no hay nada", que aqui es
        // indistinguible de una sesion nueva y es el caso normal, no un fallo.
        if (session.TryGetValue(SessionKey, out var bytes) && bytes.Length > 0)
        {
            // Un carrito ilegible se trata como uno vacio. Es alcanzable de verdad: basta con que
            // una version anterior de este proceso dejara en la sesion una forma que ya no existe.
            // Reventar en la cara de quien compra por un formato viejo seria peor que perder un
            // carrito que, con el almacen en memoria, tampoco sobrevive a un reinicio.
            try
            {
                _cached = JsonSerializer.Deserialize<ShoppingCart>(bytes, SerializerOptions);
            }
            catch (JsonException)
            {
                _cached = null;
            }
        }

        return _cached ??= new ShoppingCart();
    }

    /// <summary>
    /// Persiste el carrito. Hay que llamarlo EXPLICITAMENTE despues de mutar, y eso es a
    /// proposito: <see cref="GetAsync"/> devuelve un objeto vivo, no una copia, asi que sin esta
    /// llamada la mutacion se pierde al acabar la peticion. Un guardado automatico al final
    /// escribiria en la sesion en cada render del catalogo sin que nadie haya tocado el carrito.
    ///
    /// Un carrito vacio se BORRA en vez de guardarse como una lista vacia: asi vaciar el carrito
    /// deja la sesion como estaba antes de la primera compra, en lugar de un objeto que solo
    /// ocupa sitio.
    /// </summary>
    public async Task SaveAsync(ShoppingCart cart, CancellationToken cancellationToken = default)
    {
        var session = Session;
        await session.LoadAsync(cancellationToken);

        if (cart.IsEmpty)
        {
            session.Remove(SessionKey);
        }
        else
        {
            session.Set(SessionKey, JsonSerializer.SerializeToUtf8Bytes(cart, SerializerOptions));
        }

        _cached = cart;

        // CommitAsync escribe el almacen ahora en vez de al final de la peticion. Sin el, un
        // redirect emitido inmediatamente despues puede provocar que el navegador pida la pagina
        // siguiente antes de que la sesion se haya escrito. Con el almacen en memoria la carrera
        // es teorica; con uno de verdad no lo es, y este metodo va SIEMPRE seguido de un redirect.
        await session.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Si esto lanza, el fallo es que falta <c>app.UseSession()</c> en <c>Program.cs</c> — y el
    /// mensaje del framework ("Session has not been configured for this application or request")
    /// lo dice con todas las letras. Es un fallo RUIDOSO y por eso no se disimula con un
    /// carrito vacio: un carrito que se olvida en silencio es mucho peor que una pagina que
    /// revienta nombrando la linea que falta.
    /// </summary>
    private ISession Session =>
        httpContextAccessor.HttpContext?.Session
        ?? throw new InvalidOperationException(
            "No hay HttpContext: el carrito solo se puede leer o escribir dentro de una peticion.");
}
