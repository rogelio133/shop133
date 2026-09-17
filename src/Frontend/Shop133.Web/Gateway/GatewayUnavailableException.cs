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
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public int? UpstreamStatusCode { get; } = upstreamStatusCode;

    /// <summary>
    /// Lo que dice la cabecera <c>Retry-After</c> de un 429. Ojo con lo que 5.2 midio: ese valor
    /// es LA VENTANA ENTERA, no lo que queda de ella, asi que siempre son 60 s y es conservador.
    /// </summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;

    public bool IsRateLimited => UpstreamStatusCode == StatusCodes.Status429TooManyRequests;
}
