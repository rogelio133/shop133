using Catalog.API.Models;
using Catalog.Infrastructure.Entities;
using Catalog.Infrastructure.Persistence;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Catalog.API.Controllers;

/// <summary>
/// El CRUD del catálogo (1.3). Cinco acciones sobre <see cref="Product"/>.
///
/// Inyecta <see cref="CatalogDbContext"/> directamente, sin repositorio de por
/// medio. El DbContext ya es Unit of Work + Repository; sobre un CRUD, una capa
/// más sería un passthrough que solo añade un archivo que leer. La regla de
/// "controllers delgados" de CLAUDE.md se sostiene igual porque aquí no hay
/// lógica de negocio: las invariantes viven en el constructor de la entidad y
/// la unicidad en el índice de 1.2. Este tipo solo traduce entre HTTP y esas
/// dos cosas.
///
/// Los tres caminos de error son el entregable real del punto:
/// - 400 — lo cubren las DataAnnotations del DTO, y por debajo las guardas de
///   la entidad (ver <see cref="ToValidationProblem"/>).
/// - 404 — id que no existe.
/// - 409 — Sku duplicado, que detecta el índice único de 1.2.
/// </summary>
[ApiController]
[Route("[controller]")]
public sealed class ProductsController(CatalogDbContext db) : ControllerBase
{
    /// <summary>
    /// Sin paginación: hasta que haya volumen sería complejidad sin caso. El
    /// seed de 1.4 mete decenas de filas, no miles. Entra si 6.2 la necesita.
    ///
    /// <c>AsNoTracking</c> porque nada de lo que se lee aquí se va a modificar:
    /// evita que EF construya el ChangeTracker para una lista de solo lectura.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ProductResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ProductResponse>>> GetAll(CancellationToken cancellationToken)
    {
        var products = await db.Products
            .AsNoTracking()
            .OrderBy(product => product.Id)
            .ToListAsync(cancellationToken);

        // El mapeo se hace en memoria, no dentro de la consulta: ProductResponse.From
        // es un método estático y EF no sabe traducirlo a SQL. Da igual porque la
        // respuesta lleva todas las columnas de todas formas.
        return products.Select(ProductResponse.From).ToList();
    }

    /// <summary>
    /// La acción tiene nombre propio porque <c>CreatedAtAction</c> la referencia
    /// para construir la cabecera <c>Location</c> del 201.
    /// </summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType<ProductResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProductResponse>> GetById(int id, CancellationToken cancellationToken)
    {
        var product = await db.Products
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        return product is null
            ? NotFound()
            : ProductResponse.From(product);
    }

    [HttpPost]
    [ProducesResponseType<ProductResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProductResponse>> Create(
        CreateProductRequest request,
        CancellationToken cancellationToken)
    {
        Product product;

        try
        {
            product = new Product(
                request.Sku,
                request.Name,
                request.Description,
                request.Price,
                request.Stock,
                request.ImageUrl);

            db.Products.Add(product);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return ToValidationProblem(exception);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueConstraintViolation())
        {
            return DuplicateSkuConflict(request.Sku);
        }

        // El Id ya no vale 0: SaveChangesAsync lo trae de vuelta del IDENTITY.
        return CreatedAtAction(
            nameof(GetById),
            new { id = product.Id },
            ProductResponse.From(product));
    }

    /// <summary>
    /// Reemplazo completo. Devuelve 204 y no 200 con cuerpo porque el servidor
    /// no tiene nada que contar que el cliente no acabe de mandar; si algo
    /// hiciera falta (un campo calculado, una versión), este sería el sitio
    /// donde cambiarlo.
    ///
    /// Aquí la consulta sí rastrea la entidad: hay que modificarla, y sin
    /// ChangeTracker el SaveChanges no generaría ningún UPDATE.
    /// </summary>
    [HttpPut("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(
        int id,
        UpdateProductRequest request,
        CancellationToken cancellationToken)
    {
        var product = await db.Products
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        if (product is null)
        {
            return NotFound();
        }

        try
        {
            product.Update(
                request.Sku,
                request.Name,
                request.Description,
                request.Price,
                request.Stock,
                request.ImageUrl);

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return ToValidationProblem(exception);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueConstraintViolation())
        {
            // El PUT puede chocar con el índice único igual que el POST porque
            // el Sku es modificable — decisión 9 de docs/fase_1_1.md.
            return DuplicateSkuConflict(request.Sku);
        }

        return NoContent();
    }

    /// <summary>
    /// Borrado físico. Un borrado lógico necesitaría columna, migración y filtro
    /// global de consulta, y no está en el roadmap.
    ///
    /// Lo que sí abre es una consecuencia que conviene tener escrita: desde la
    /// Fase 3, un <c>OrderLine.ProductId</c> puede apuntar a un producto que
    /// aquí ya no existe. Ninguna FK puede impedirlo — las bases están separadas
    /// por la regla 1 de CLAUDE.md — así que es una referencia colgante entre
    /// servicios, no un bug de este endpoint.
    ///
    /// <c>ExecuteDeleteAsync</c> en vez de cargar la entidad y llamar a Remove:
    /// una sola ida y vuelta, y las filas afectadas ya distinguen el 404 sin
    /// materializar nada.
    /// </summary>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var deletedRows = await db.Products
            .Where(candidate => candidate.Id == id)
            .ExecuteDeleteAsync(cancellationToken);

        return deletedRows == 0
            ? NotFound()
            : NoContent();
    }

    /// <summary>
    /// Segunda línea de defensa del 400, y no es teórica. Las DataAnnotations
    /// del DTO cubren casi todo, pero hay un hueco medido en 1.3:
    /// <c>ImageUrl</c> es opcional, así que el DTO solo le pone
    /// <c>[MaxLength]</c> — un <c>"   "</c> pasa la validación del modelo y la
    /// entidad lo rechaza, porque para ella un ImageUrl presente pero en blanco
    /// no es lo mismo que ausente. Sin este catch, ese cuerpo saldría como 500.
    ///
    /// Un solo catch de <see cref="ArgumentException"/> cubre también
    /// <see cref="ArgumentOutOfRangeException"/>, que hereda de él — medido en
    /// 1.1, junto con que el <c>ParamName</c> llega siempre relleno. Ese nombre
    /// es lo que permite devolver el error apuntando al campo.
    /// </summary>
    private ActionResult ToValidationProblem(ArgumentException exception)
    {
        ModelState.AddModelError(exception.ParamName ?? string.Empty, exception.Message);

        return ValidationProblem(ModelState);
    }

    private ActionResult DuplicateSkuConflict(string sku) => Conflict(new ProblemDetails
    {
        Status = StatusCodes.Status409Conflict,
        Title = "Sku duplicado",
        Detail = $"Ya existe un producto con el Sku '{sku.Trim().ToUpperInvariant()}'.",
    });
}
