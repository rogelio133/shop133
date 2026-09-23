using System.ComponentModel.DataAnnotations;

namespace Catalog.API.Models;

/// <summary>
/// Los parámetros de consulta de <c>GET /products</c> (6.2.1), recogiendo la
/// deuda que 1.3 dejó aplazada para la paginación y 1.4 para el filtro.
///
/// Es un tipo y no tres parámetros sueltos en la acción por las
/// DataAnnotations: enlazados uno a uno, <c>[Range]</c> sobre un parámetro
/// simple **no** produce un 400 —<c>[ApiController]</c> solo valida el
/// <c>ModelState</c>, y un parámetro primitivo fuera de rango no lo ensucia—,
/// así que un <c>?page=0</c> se colaría hasta el <c>Skip</c> con un
/// desplazamiento negativo. Dentro de un modelo, las mismas anotaciones sí se
/// evalúan y el 400 sale con la forma de <c>ValidationProblemDetails</c> que
/// este servicio ya devuelve en POST y PUT.
///
/// *Descartado* recortar los valores en silencio (un <c>Math.Clamp</c>): un
/// <c>?pageSize=100000</c> devolvería 12 elementos sin decir por qué y el
/// cliente creería que el catálogo tiene 20 filas. Pedir algo imposible es un
/// error del cliente y se le dice, que es el criterio del resto del servicio.
/// </summary>
public sealed record GetProductsRequest
{
    /// <summary>
    /// 12 es el tamaño que pide el roadmap. No es un número mágico suelto: el
    /// frontend tiene el suyo propio (6.2.1) y este es el que se aplica cuando
    /// el cliente no dice nada.
    /// </summary>
    public const int DefaultPageSize = 12;

    /// <summary>
    /// El tope existe porque sin él <c>?pageSize=1000000</c> es una forma
    /// perfectamente válida de pedir el catálogo entero, y entonces la
    /// paginación no protege de nada — sería una sugerencia, no un límite.
    /// 100 deja sitio de sobra para un cliente que quiera menos viajes.
    /// </summary>
    public const int MaxPageSize = 100;

    /// <summary>Empieza en 1, no en 0: es una página, no un desplazamiento.</summary>
    [Range(1, int.MaxValue)]
    public int Page { get; init; } = 1;

    [Range(1, MaxPageSize)]
    public int PageSize { get; init; } = DefaultPageSize;

    /// <summary>
    /// Anulable porque su ausencia significa "todo el catálogo", que es distinto
    /// de cualquier valor concreto.
    ///
    /// El rango solo dice que un id válido es positivo. Que **exista** no se
    /// comprueba, al revés que en <see cref="CreateProductRequest.CategoryId"/>:
    /// allí ese id **escribe** una relación que tiene que existir, y aquí solo
    /// **selecciona**. Un <c>categoryId</c> inexistente es un <c>200</c> con la
    /// página vacía, porque "no hay nada" es una respuesta cierta — y es lo que
    /// 6.2 ya había decidido y verificado desde el lado del frontend.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int? CategoryId { get; init; }
}
