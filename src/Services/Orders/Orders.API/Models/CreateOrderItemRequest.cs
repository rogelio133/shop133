using System.ComponentModel.DataAnnotations;

namespace Orders.API.Models;

/// <summary>
/// Una línea del cuerpo de <c>POST /orders</c>.
///
/// **Solo lleva qué y cuánto.** El sku, el nombre y el precio no se piden al
/// cliente: los trae Catalog, que es su único dueño (regla 1 — Orders no puede
/// leer <c>CatalogDb</c>). Eso es lo que significa "validar productos/precios" en
/// el roadmap: no comparar contra un número que mandó el cliente, sino ir a
/// buscarlo a la única fuente que lo conoce.
///
/// *Descartado* que el cuerpo declarase el <c>unitPrice</c> que el cliente vio en
/// la ficha y devolver 409 si Catalog dice otro. Es un escenario real —el precio
/// cambia mientras compras— y enseña inconsistencia temporal, pero mete un
/// segundo número que puede desincronizarse y una rama de error más en el punto
/// que introduce el acoplamiento. Si la Fase 6 quiere enseñarlo desde el carrito,
/// se retoma entonces con el caso de uso delante.
///
/// Tampoco lleva sku: dos identificadores del mismo producto en el mismo cuerpo
/// obligarían a decidir cuál gana si no coinciden.
/// </summary>
public sealed record CreateOrderItemRequest
{
    /// <summary>
    /// El id de <c>CatalogDb</c>. El rango solo afirma que un id válido es
    /// positivo; que **exista** lo comprueba el controller contra Catalog por
    /// HTTP, y devuelve 400 si no — mismo criterio que el <c>categoryId</c> de
    /// <c>POST /products</c>, con la diferencia de que aquí la consulta cruza un
    /// límite de servicio en vez de una tabla.
    /// </summary>
    [Range(1, int.MaxValue)]
    public required int ProductId { get; init; }

    /// <summary>
    /// Cuántas unidades. El tope de 10.000 **no es una regla de negocio** —la
    /// entidad solo exige que sea positiva— sino una guarda de forma de entrada:
    /// al agrupar líneas repetidas se suman cantidades, y un tope explícito es lo
    /// que garantiza que esa suma no pueda desbordar el <c>int</c>. Con el
    /// máximo de 50 líneas de <see cref="CreateOrderRequest.Items"/>, el peor
    /// caso es 500.000, muy lejos de <c>int.MaxValue</c>.
    ///
    /// **No se comprueba contra el stock.** El <c>stock</c> que publica Catalog es
    /// el que muestra el catálogo; el reservable vive en <c>InventoryDb</c> desde
    /// 3.4 y es la saga quien lo reserva. Descontar aquí crearía un segundo
    /// número que llevaría la cuenta de lo mismo.
    /// </summary>
    [Range(1, 10_000)]
    public required int Quantity { get; init; }
}
