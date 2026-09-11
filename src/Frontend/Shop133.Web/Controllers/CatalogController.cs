using Microsoft.AspNetCore.Mvc;
using Shop133.Web.Gateway;
using Shop133.Web.Models;

namespace Shop133.Web.Controllers;


public sealed class CatalogController(CatalogClient catalogClient, ILogger<CatalogController> logger)
    : Controller
{
    
    public async Task<IActionResult> Index(int? categoryId, int page, CancellationToken cancellationToken)
    {
        // page llega a 0 cuando no viene en la URL, que es el caso normal. El recorte a 1 lo hace
        // CatalogClient, que es quien habla con una API que devuelve 400 a un page=0.
        CatalogPage<CatalogProduct> products;
        IReadOnlyList<CatalogCategory> categories;

        try
        {
            var productsTask = catalogClient.GetProductsAsync(categoryId, page, cancellationToken);
            var categoriesTask = catalogClient.GetCategoriesAsync(cancellationToken);

            await Task.WhenAll(productsTask, categoriesTask);

            products = await productsTask;
            categories = await categoriesTask;
        }
        catch (GatewayUnavailableException exception)
        {
            return Unavailable(exception);
        }

        return View(new CatalogIndexViewModel
        {
            Products = products.Items,
            // Los chips vienen tal cual: desde 6.2.1 traen su propio recuento, calculado por la
            // API sobre el catalogo ENTERO. Calcularlo aqui sobre products.Items contaria los de
            // la pagina, que es una respuesta distinta y equivocada.
            Categories = categories,
            SelectedCategoryId = categoryId,
            Page = products.Page,
            TotalPages = products.TotalPages,
            TotalItems = products.TotalItems,
        });
    }

    public async Task<IActionResult> Details(int id, CancellationToken cancellationToken)
    {
        CatalogProduct? product;

        try
        {
            product = await catalogClient.FindProductOrNullAsync(id, cancellationToken);
        }
        catch (GatewayUnavailableException exception)
        {
            return Unavailable(exception);
        }

        if (product is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return View("NotFound", id);
        }

        return View(product);
    }

    /// <summary>
    /// El aviso de que el Gateway no contesta, con un codigo de estado que NO es 200.
    ///
    /// Se devuelve <c>503</c> y no el <c>502</c> que 2.3 eligio, y la diferencia esta en la
    /// premisa y no en el gusto: alli Orders actuaba de INTERMEDIARIO de Catalog dentro de la
    /// misma peticion, que es literalmente lo que el 502 describe. <c>Shop133.Web</c> no proxea
    /// nada — es el servidor de origen, y lo que no puede es CONSTRUIR SU PAGINA. Copiar el 502
    /// por simetria seria quedarse con la conclusion y tirar la premisa, que es exactamente lo
    /// que la decision 7 de 6.1 se nego a hacer con <c>UseHttpsRedirection()</c>.
    ///
    /// El estado importa aunque el navegador pinte el HTML igual: es lo unico que hace la rama
    /// COMPROBABLE desde la linea de comandos. Con el Gateway parado <c>/Catalog</c> devuelve
    /// 503 y con el arriba 200; un 200 con un cartel dentro no se distingue de una pagina que
    /// funciona con ningun comando.
    /// </summary>
    private IActionResult Unavailable(GatewayUnavailableException exception)
    {
        logger.LogWarning(exception, "No se pudo pintar el catalogo.");

        Response.StatusCode = exception.IsRateLimited
            ? StatusCodes.Status429TooManyRequests
            : StatusCodes.Status503ServiceUnavailable;

        return View("Unavailable", exception);
    }
}
