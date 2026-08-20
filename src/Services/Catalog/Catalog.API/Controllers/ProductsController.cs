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
    [EndpointSummary("Lista todos los productos del catálogo")]
    [EndpointDescription(
        "Devuelve el catálogo completo ordenado por Id, con el nombre de la categoría de cada " +
        "producto ya resuelto. No está paginado ni admite filtros todavía. La lista vacía es un " +
        "200 con un array vacío, nunca un 404.")]
    [ProducesResponseType<IReadOnlyList<ProductResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ProductResponse>>> GetAll(CancellationToken cancellationToken)
    {
        var products = await db.Products
            .AsNoTracking()
            .Include(product => product.Category)
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
    [EndpointSummary("Obtiene un producto por su Id")]
    [EndpointDescription(
        "404 si el Id no existe. El Id lo asigna la base de datos al crear el producto y hay que " +
        "leerlo de la respuesta del POST: no empieza en 1 ni es correlativo, porque el IDENTITY de " +
        "SQL Server salta de bloque al reiniciar el servicio y quema números en los INSERT que " +
        "fallan. Para buscar por código de negocio está el campo sku de la respuesta.")]
    [ProducesResponseType<ProductResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProductResponse>> GetById(int id, CancellationToken cancellationToken)
    {
        var product = await db.Products
            .AsNoTracking()
            .Include(candidate => candidate.Category)
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        return product is null
            ? NotFound()
            : ProductResponse.From(product);
    }

    [HttpPost]
    [EndpointSummary("Crea un producto")]
    [EndpointDescription(
        "Devuelve 201 con el producto creado y la cabecera Location apuntando a GET /products/{id}. " +
        "El sku se guarda recortado y en mayúsculas, así que 'lap-14' y 'LAP-14' son el mismo código.\n\n" +
        "Errores:\n" +
        "- 400 — falla la validación del cuerpo. También es 400, y no 404, cuando el categoryId no " +
        "existe: lo que falta es un valor del cuerpo, no el recurso de la URL. El error nombra el " +
        "campo; las categorías válidas se consultan en GET /categories.\n" +
        "- 409 — ya existe otro producto con ese sku.")]
    [ProducesResponseType<ProductResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProductResponse>> Create(
        CreateProductRequest request,
        CancellationToken cancellationToken)
    {
        // La categoría se busca —y no solo se comprueba con AnyAsync— porque la
        // entidad que vuelve queda rastreada por el contexto, y eso es lo que
        // hace que EF rellene product.Category por fix-up al añadir el producto.
        // Sin ella, ProductResponse.From no tendría el nombre que devolver en el
        // 201 y haría falta una segunda consulta.
        if (await FindCategoryOrNull(request.CategoryId, cancellationToken) is null)
        {
            return UnknownCategory(request.CategoryId);
        }

        Product product;

        try
        {
            product = new Product(
                request.Sku,
                request.Name,
                request.Description,
                request.Price,
                request.Stock,
                request.CategoryId,
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
    [EndpointSummary("Reemplaza un producto completo")]
    [EndpointDescription(
        "Reemplazo total: hay que mandar todos los campos, no solo los que cambian. Devuelve 204 sin " +
        "cuerpo. El sku sí se puede cambiar —un código de negocio se corrige—; el Id nunca.\n\n" +
        "Errores, comprobados en este orden:\n" +
        "- 404 — no existe el producto del Id de la URL. Gana sobre el 400: si el recurso que se " +
        "pretendía reemplazar no está, lo que traiga el cuerpo ya da igual.\n" +
        "- 400 — falla la validación del cuerpo, o el categoryId no existe (ver GET /categories).\n" +
        "- 409 — el sku nuevo ya lo tiene otro producto.")]
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

        // 404 primero y 400 de categoría después: el 404 habla del recurso de la
        // URL y el 400 del cuerpo. Un PUT a un id inexistente con una categoría
        // también inexistente es un 404 — el recurso que se pretendía reemplazar
        // no está, y lo que traía el cuerpo ya da igual.
        if (await FindCategoryOrNull(request.CategoryId, cancellationToken) is null)
        {
            return UnknownCategory(request.CategoryId);
        }

        try
        {
            product.Update(
                request.Sku,
                request.Name,
                request.Description,
                request.Price,
                request.Stock,
                request.CategoryId,
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
    [EndpointSummary("Borra un producto")]
    [EndpointDescription(
        "Borrado físico: la fila desaparece y no hay papelera. 204 si se borró, 404 si el Id no " +
        "existía. Un pedido que ya se hizo sobre este producto sigue siendo válido y sabe qué se " +
        "compró: la línea de pedido congela sku, nombre y precio en el momento de la compra.")]
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
    /// Comprueba que la categoría exista antes de guardar, en vez de dejar que
    /// salte la clave foránea (1.4).
    ///
    /// *Descartado* guardar sin comprobar y traducir el error 547 de SQL Server
    /// en <c>DbUpdateExceptionExtensions</c>, como ya se hace con el 2601/2627
    /// del índice único. Ahorraría este viaje a la base, pero los dos casos no
    /// son iguales: la unicidad **solo** la puede responder el conjunto entero
    /// de filas en el instante del INSERT, así que ahí la excepción es la única
    /// vía posible; que una categoría exista es una consulta corriente que se
    /// puede hacer antes. Y el 547 no distingue *qué* FK falló, así que el
    /// mensaje de error saldría peor.
    ///
    /// Devuelve la entidad y no un <c>bool</c> a propósito: al quedar rastreada
    /// por el contexto, EF rellena por fix-up la navegación
    /// <c>Product.Category</c> del producto que se añade justo después, que es
    /// lo que necesita el 201 para incluir el nombre de la categoría.
    /// </summary>
    private Task<Category?> FindCategoryOrNull(int categoryId, CancellationToken cancellationToken) =>
        db.Categories.FirstOrDefaultAsync(candidate => candidate.Id == categoryId, cancellationToken);

    /// <summary>
    /// 400 y no 404: el que no existe es el <c>CategoryId</c> del **cuerpo**, no
    /// el recurso al que apunta la URL. Sale como <c>ValidationProblemDetails</c>
    /// nombrando el campo, igual que los errores de DataAnnotations, para que el
    /// cliente no tenga que distinguir dos formatos de error de entrada.
    /// </summary>
    private ActionResult UnknownCategory(int categoryId)
    {
        ModelState.AddModelError(
            nameof(CreateProductRequest.CategoryId),
            $"No existe la categoría {categoryId}. Las categorías válidas están en GET /categories.");

        return ValidationProblem(ModelState);
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
