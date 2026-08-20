using Catalog.API.Models;
using Catalog.Infrastructure.Persistence;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Catalog.API.Controllers;

/// <summary>
/// El catálogo de categorías (1.4). Un solo verbo, de solo lectura.
///
/// *Descartado* un CRUD completo simétrico al de <c>ProductsController</c>. Las
/// categorías son cinco filas fijas que pone el seed; un POST necesitaría
/// decidir qué pasa con el índice único del nombre y un DELETE tendría que
/// resolver qué hacer con los 10 productos que cuelgan de la categoría —dos
/// caminos de error nuevos— para una operación que nadie va a ejecutar. Añadir
/// una categoría hoy es editar <c>CatalogSeedData</c> y generar una migración,
/// que además deja constancia del cambio en el historial.
///
/// *Descartado* también no exponerlas en absoluto: sin este endpoint, un
/// cliente no tiene forma de averiguar qué <c>CategoryId</c> es válido para un
/// <c>POST /products</c> salvo leyendo el código, y la vista de catálogo de 6.2
/// no podría pintar el filtro por categoría.
///
/// Inyecta el <see cref="CatalogDbContext"/> directo, igual que
/// <c>ProductsController</c> y por el mismo motivo: sobre una lectura sin
/// lógica, un repositorio sería un passthrough.
/// </summary>
[ApiController]
[Route("[controller]")]
public sealed class CategoriesController(CatalogDbContext db) : ControllerBase
{
    /// <summary>
    /// Ordenadas por nombre y no por id: el id es un detalle de la base y su
    /// orden es el de inserción del seed, que no significa nada para quien pinta
    /// un menú. Alfabético es un criterio que el cliente no tiene que reordenar.
    ///
    /// Sin paginación por motivos evidentes, y sin 404 posible: la lista vacía
    /// es un 200 con un array vacío, no un "no encontrado".
    /// </summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<CategoryResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CategoryResponse>>> GetAll(CancellationToken cancellationToken)
    {
        var categories = await db.Categories
            .AsNoTracking()
            .OrderBy(category => category.Name)
            .ToListAsync(cancellationToken);

        return categories.Select(CategoryResponse.From).ToList();
    }
}
