using Microsoft.AspNetCore.Mvc;
using Shop133.Web.Gateway;
using Shop133.Web.Models;

namespace Shop133.Web.Controllers;

/// <summary>
/// La vista de catalogo (6.2). Es la primera pagina del proyecto que habla con el backend.
///
/// Hereda de <c>Controller</c> y NO lleva <c>[ApiController]</c> ni <c>[Route("[controller]")]</c>,
/// aunque la seccion Conventions de CLAUDE.md los pida: esa convencion se escribio para los
/// controllers de API de los cinco servicios. Aqui <c>[ApiController]</c> seria activamente
/// danino — convierte un fallo de binding en un <c>400 ProblemDetails</c> EN VEZ de en un
/// <c>ModelState</c> invalido, que es lo que 6.4 necesita para sus formularios.
///
/// Se llama <c>CatalogController</c> y no <c>ProductsController</c>, saltandose la convencion de
/// nombrar en plural el recurso: eso vale para un recurso, y esto es una PAGINA. El nombre
/// coincide con el titulo del roadmap, con la etiqueta del navbar, con el prefijo del Gateway
/// (<c>/api/catalog</c>) y con el <c>CartController</c> de 6.3.
/// </summary>
public sealed class CatalogController(CatalogClient catalogClient, ILogger<CatalogController> logger)
    : Controller
{
    /// <summary>
    /// El grid, con filtro opcional por categoria y paginado.
    ///
    /// <paramref name="categoryId"/> y <paramref name="page"/> se enlazan desde la CADENA DE
    /// CONSULTA sin ningun atributo, porque no estan en la plantilla de ruta
    /// (<c>{controller}/{action}/{id?}</c>).
    ///
    /// **Desde 6.2.1 el filtro y el recorte los hace la API.** Hasta aqui este metodo se traia el
    /// catalogo entero y descartaba 40 filas en memoria para ensenar 10, porque
    /// <c>GET /products</c> no aceptaba ni un parametro de consulta; el <c>///</c> de aquel
    /// endpoint decia por escrito desde 1.3 que la paginacion entraria "si 6.2 la necesita". La
    /// necesito, 6.2 decidio no tocar un servicio para servir a una vista, y 6.2.1 recogio la
    /// deuda. Lo que desaparece de aqui es un <c>Where</c> y un <c>GroupBy</c> sobre cincuenta
    /// filas que ya no viajan.
    ///
    /// Un <c>categoryId</c> que no existe ensena el estado vacio; NO devuelve 404. La regla de
    /// 2.3 —un valor malo en el cuerpo es 400, en la URL es 404— no aplica: una cadena de
    /// consulta es un FILTRO sobre un recurso, no la identidad del recurso, y <c>/Catalog</c>
    /// existe se le cuelgue lo que se le cuelgue. La API opina lo mismo desde 6.2.1 y devuelve
    /// 200 con la pagina vacia.
    /// </summary>
    public async Task<IActionResult> Index(int? categoryId, int page, CancellationToken cancellationToken)
    {
        // page llega a 0 cuando no viene en la URL, que es el caso normal. El recorte a 1 lo hace
        // CatalogClient, que es quien habla con una API que devuelve 400 a un page=0.
        CatalogPage<CatalogProduct> products;
        IReadOnlyList<CatalogCategory> categories;

        try
        {
            // Las dos llamadas van EN PARALELO, justo lo que 2.3 rechazo hacer. Alli eran
            // secuenciales a proposito, para que el coste de que un servicio llame a otro se
            // viera; esto es frontend -> Gateway, que es el acoplamiento que la regla 3 MANDA
            // tener, asi que no hay nada que hacer visible y esconderlo no ensena nada.
            //
            // Siguen siendo DOS por render contra el cupo catalog-read de 60/60 s de 5.2: la
            // paginacion recorta el CUERPO, no el CUPO, asi que el primer 429 sigue llegando en
            // el render #30 como midio 6.2.
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

    /// <summary>
    /// La ficha de un producto. Se pasa el <c>CatalogProduct</c> tal cual a la vista: un view
    /// model con una sola propiedad seria inventar la forma antes del caso de uso.
    /// </summary>
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
            // Vista propia en vez de `return NotFound()`, que en una aplicacion de cara al
            // usuario pinta una pagina en blanco. Descartado UseStatusCodePagesWithReExecute:
            // es un middleware que cambiaria TODOS los 404 de la aplicacion —incluido el de
            // /Home/Privacy que 6.1 verifico—, y esa es una decision de la superficie de error
            // entera, no del punto que resulta necesitar el primer 404 presentable.
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
