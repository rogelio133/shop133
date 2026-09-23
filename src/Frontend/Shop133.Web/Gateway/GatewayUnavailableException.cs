namespace Shop133.Web.Gateway;

/// <summary>
/// El Gateway no contesto, o contesto algo que no se puede pintar.
///
/// Existe para que el controller pueda distinguir "la dependencia no esta" de cualquier otra
/// excepcion y decidir QUE se le ensena al usuario. Es el espejo del
/// <c>CatalogUnavailableException</c> que 2.3 tenia en Orders, con una diferencia: aquel solo
/// tenia que producir un 502, y este ademas tiene que poder separar el rechazo por cupo.
///
/// <see cref="UpstreamStatusCode"/> es null cuando no hubo respuesta (conexion rechazada o
/// timeout) y lleva el codigo cuando el Gateway si contesto.
/// </summary>
public sealed class GatewayUnavailableException(
    string message,
    int? upstreamStatusCode = null,
    TimeSpan? retryAfter = null,
    string? quota = null,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public int? UpstreamStatusCode { get; } = upstreamStatusCode;

    /// <summary>
    /// Lo que dice la cabecera <c>Retry-After</c> de un 429. Ojo con lo que 5.2 midio: ese valor
    /// es LA VENTANA ENTERA, no lo que queda de ella, asi que siempre son 60 s y es conservador.
    /// </summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>
    /// Que cupo de 5.2 se acaba de agotar, en una frase para leer — lo pone el cliente que lanza,
    /// porque es el unico que sabe contra que ruta iba.
    ///
    /// **Entra en 6.4 porque anadir el SEGUNDO llamante demostro que la pagina de 429 mentia.**
    /// <c>Views/Shared/Unavailable.cshtml</c> llevaba escrito "60 por minuto" desde 6.2, que es el
    /// cupo <c>catalog-read</c>; el checkout va por <c>orders-route</c>, cuyo cupo
    /// <c>orders-write</c> son DIEZ por minuto —mucho mas estrecho a proposito, porque cada POST
    /// arranca la saga entera—, asi que la misma pagina le habria dado al usuario una cifra falsa
    /// y un consejo equivocado. Un texto por cliente es la forma barata de que no vuelva a pasar
    /// al llegar el tercero.
    ///
    /// **Es el valor CONFIGURADO POR DEFECTO, no el vivo**: el Gateway no le cuenta su cupo a
    /// nadie, solo devuelve 429. Bajarlo por variable de entorno —lo que hace 5.4 para provocar el
    /// rechazo sin crear diez pedidos— deja este texto desfasado. Se acepta: la alternativa seria
    /// que el frontend leyera la configuracion del Gateway, que es acoplamiento del que la regla 3
    /// existe para librar.
    /// </summary>
    public string? Quota { get; } = quota;

    public bool IsRateLimited => UpstreamStatusCode == StatusCodes.Status429TooManyRequests;
}
