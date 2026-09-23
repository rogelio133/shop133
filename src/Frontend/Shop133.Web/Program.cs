using Shop133.Web.Cart;
using Shop133.Web.Gateway;
using Shop133.Web.Orders;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

// Misma linea que Catalog (1.5) y Orders (2.3). El enrutado YA es case-insensitive, asi que
// /Catalog entra igual sin esto; lo que arregla es la URL *generada* por los tag helpers, que
// sin ella sale "/Catalog/Details/5" mientras los otros dos proyectos web sirven en minusculas.
builder.Services.Configure<RouteOptions>(options => options.LowercaseUrls = true);

// 6.2 — la primera y UNICA direccion que este proyecto conoce.
//
// La guarda revienta antes de app.Build() como las de los cinco servicios y las tres del
// Gateway. Sin ella el fallo no es limpio: con la clave ausente, new Uri(null!) lanza un
// ArgumentNullException que nombra un parametro del framework y no una clave de configuracion.
// Y la version "defensiva" (?? "") seria peor todavia — BaseAddress se quedaria en null, cada
// peticion se volveria relativa, HttpClient lanzaria InvalidOperationException, el catch lo
// leeria como indisponibilidad y TODAS las paginas dirian "el Gateway no responde" senalando a
// un proceso que esta perfectamente vivo.
var gatewayBaseUrl = builder.Configuration["Gateway:BaseUrl"];

if (string.IsNullOrWhiteSpace(gatewayBaseUrl))
{
    throw new InvalidOperationException(
        "Falta la configuracion 'Gateway:BaseUrl'. Es la direccion del Gateway (en local, " +
        "http://127.0.0.1:5104) y vive en appsettings.json, no en User Secrets: no es un " +
        "secreto. La regla 3 de CLAUDE.md manda que sea la UNICA direccion que este proyecto " +
        "conoce — aqui no se nombra ningun servicio.");
}

// Se valida que sea absoluta, como hace 5.3 con Cors:AllowedOrigins. Lo que NO se copia de
// alli es el rechazo de la barra final: aquella comparacion es literal contra la cabecera
// Origin, y esto es una base address, que la quiere.
if (!Uri.TryCreate(gatewayBaseUrl, UriKind.Absolute, out _))
{
    throw new InvalidOperationException(
        $"'Gateway:BaseUrl' = '{gatewayBaseUrl}' no es una URL absoluta.");
}

// 6.5 — la direccion que usa el NAVEGADOR. Opcional: si falta, el navegador llega por donde llega
// este servidor, que es cierto en el unico despliegue que hoy existe. Por eso NO hay guarda de
// ausencia como la de arriba; lo que si se valida es la forma, porque una URL relativa aqui
// produciria peticiones contra el propio frontend y un 404 que acusaria al sitio equivocado.
//
// Quien la usa no es ningun HttpClient de este proceso: acaba en un data- del HTML y la lee el
// fetch de wwwroot/js/order-status.js. El caso que obliga a declararla en local es el perfil
// https — ver el comentario de appsettings.json y OrdersController.BrowserGatewayBaseUrl.
var gatewayPublicBaseUrl = builder.Configuration["Gateway:PublicBaseUrl"];

if (!string.IsNullOrWhiteSpace(gatewayPublicBaseUrl)
    && !Uri.TryCreate(gatewayPublicBaseUrl, UriKind.Absolute, out _))
{
    throw new InvalidOperationException(
        $"'Gateway:PublicBaseUrl' = '{gatewayPublicBaseUrl}' no es una URL absoluta. Es la " +
        "direccion del Gateway tal y como la ve el NAVEGADOR, asi que tiene que llevar esquema, " +
        "host y puerto: el fetch de la pagina de estado del pedido sale del navegador, no de " +
        "este proceso. Dejala sin declarar para que el navegador use la misma que el servidor.");
}

// La configuracion de los DOS clientes tipados, en un solo sitio. Se extrae en 6.4, al aparecer
// el segundo: la base y el timeout tienen que ser los mismos, y dos lambdas calcadas son dos
// sitios donde el "/api/" puede divergir sin que nada avise.
//
// 6.6 ENGANCHA AQUI sus politicas de Polly, y entonces esto deja de ser una sola funcion: un
// reintento automatico de una LECTURA es gratis, y el de una ESCRITURA crea pedidos duplicados.
void ConfigureGatewayClient(HttpClient client)
{
    // Con barra final: sin ella, Uri resuelve la ruta relativa contra el PADRE y se come el
    // ultimo segmento. Aqui la base SI tiene segmento de ruta ("/api/"), asi que esto muerde
    // de verdad — en 2.3 era una precaucion teorica.
    client.BaseAddress = new Uri(gatewayBaseUrl.TrimEnd('/') + "/api/");

    // ── CORRECCION DE 6.4 ──
    //
    // Esta linea NO EXISTIA. El comentario de 6.2 describia este valor y advertia a 6.6 de que
    // tendria que releerlo, pero la asignacion nunca se escribio: el timeout real era el de
    // fabrica, CIEN SEGUNDOS. Se descubrio al anadir el segundo cliente, que copia esta
    // configuracion. Un comentario que describe una linea ausente es peor que no tenerlo —
    // sostiene que una decision esta tomada y ademas le pasa el aviso al punto siguiente.
    //
    // 5 s (precedente de 2.3). Con el valor de fabrica, "el Gateway esta caido" tardaria minuto y
    // medio en pintar el aviso y pareceria un cuelgue; y desde 6.4 eso pasa DELANTE de alguien que
    // esta tramitando un pedido. El rechazo de conexion en 127.0.0.1 son ~2 s (medido en 2.3 y
    // en 6.2), asi que 5 s deja margen sin acercarse a la espera de fabrica.
    //
    // 6.6 TIENE QUE RELEER ESTA LINEA, y ahora si hay linea que releer. Timeout es el plazo
    // EXTERIOR de todo el pipeline de resiliencia: con 5 s aqui, el total-request-timeout de 30 s
    // del handler estandar de Polly no cabe y los reintentos NO LLEGAN A EJECUTARSE NUNCA, sin
    // error y sin aviso.
    client.Timeout = TimeSpan.FromSeconds(5);
}

builder.Services.AddHttpClient<CatalogClient>(ConfigureGatewayClient);

// 6.4 — el segundo cliente tipado y el PRIMER POST que sale de este proyecto. Con el, la saga
// entera arranca desde un formulario de navegador en vez de desde un curl.
//
// Separado de CatalogClient y no un metodo mas alli: aquel se llama "Catalog" y hablaria con
// Orders, y son dos cupos distintos del rate limiter de 5.2 (60/min las lecturas del catalogo,
// 10/min los pedidos) con dos politicas de reintento incompatibles en 6.6.
builder.Services.AddHttpClient<OrdersClient>(ConfigureGatewayClient);

// 6.3 — el carrito, EN SESION DE SERVIDOR.
//
// Las tres lineas de abajo son el punto entero, y el motivo no es de comodidad sino de
// arquitectura. Desde 3.3 el cuerpo de POST /orders lleva el precio, asi que QUIEN GUARDA EL
// CARRITO ES QUIEN ACUNA LA FOTO DEL PEDIDO. Aqui la cookie lleva solo un identificador opaco y
// los datos —el UnitPrice incluido— viven en este proceso, de modo que la foto la acuna
// Shop133.Web leyendo Catalog por el Gateway (lo que la regla 3 permite). Con el carrito en
// cookie la acunaria el navegador, y el cliente volveria a dictar el importe con 4.8 como unica
// defensa. Ver la decision 2b de docs/fase_3_3.md: 6.3 y 8.1 deciden QUIEN puede mandar la foto,
// 4.8 decide si la foto es cierta. Hacen falta las dos.
//
// AddDistributedMemoryCache es un nombre enganoso: NO es distribuido. Es el almacen en memoria de
// este proceso, asi que el carrito muere con el y no se comparte entre replicas. Se dice en voz
// alta en lugar de esconderlo. Descartado un IDistributedCache de verdad (SQL Server o Redis):
// exige un paquete —y este .csproj sigue con CERO PackageReference, propiedad documentada que
// solo 6.6 puede romper— y una base de datos que este proyecto no posee.
builder.Services.AddDistributedMemoryCache();

builder.Services.AddSession(options =>
{
    // Se escribe aunque coincida con el valor de fabrica, para que la relacion con los 30 min de
    // PricingSnapshotWindowMinutes (4.8) quede a la vista. Y OJO, porque parece que la acota y no
    // lo hace: CADA PETICION REINICIA ESTE CONTADOR, asi que un carrito en uso puede sostener una
    // linea anadida hace horas. Esto protege la memoria del proceso, no la frescura del precio —
    // de eso se encarga 4.8 rechazando la foto, y el pedido se cancela solo en 6.4/6.5.
    options.IdleTimeout = TimeSpan.FromMinutes(20);

    // HttpOnly ya es el valor por defecto; se escribe porque esta cookie es exactamente lo que el
    // titulo de 6.3 dice que NO debe llevar el carrito, y conviene ver que ni siquiera el
    // JavaScript de la propia pagina puede leer el identificador.
    options.Cookie.HttpOnly = true;

    // Sin esto, la cookie desaparece en cuanto alguien anada una politica de consentimiento
    // (CheckConsentNeeded), y el carrito se vaciaria solo sin un error en ninguna parte. No hay
    // consentimiento de cookies en este proyecto; la marca esta puesta para que siga sin haberlo
    // el dia que lo haya.
    options.Cookie.IsEssential = true;

    options.Cookie.Name = ".Shop133.Session";
});

// CartStore necesita llegar a HttpContext.Session, y IHttpContextAccessor NO esta registrado por
// defecto. Es una linea y esta en el framework compartido — sin paquete.
builder.Services.AddHttpContextAccessor();

// Scoped: cachea el carrito durante la peticion, de modo que el controller y el
// CartBadgeViewComponent vean la MISMA instancia. Con un transient, el badge pintaria el carrito
// de antes de la mutacion.
builder.Services.AddScoped<CartStore>();

// 6.5 — los pedidos tramitados en esta sesion, para que el item "Estado del pedido" del navbar
// tenga adonde llevar. Scoped por el mismo motivo que CartStore: cachea la lista durante la
// peticion, de modo que el controller no la deserialice dos veces.
//
// Segundo archivo del proyecto que toca ISession, y el /// de CartStore decia ser el unico. No es
// una contradiccion y esta explicado en RecentOrdersStore: aquella regla defendia que el PRECIO no
// saliera al navegador, y lo que la sostiene es que haya un solo duenno POR COSA guardada.
builder.Services.AddScoped<RecentOrdersStore>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

// Se queda, al reves que en Catalog.API y Orders.API. 5.1 se la quito a esos dos porque
// estan DETRAS del Gateway: su 307 devolvia al cliente la direccion real del servicio, que
// es justo lo que la regla 3 existe para impedir. Shop133.Web no lo esta — es la app que
// abre el navegador, no la proxea nadie, y su perfil activo sirve tambien en https.
app.UseHttpsRedirection();
app.UseRouting();

// 6.3. El sitio importa: DESPUES de UseRouting y ANTES del endpoint, porque quien lee la sesion
// es el controller. Colocado detras del MapControllerRoute, CartStore lanzaria en cada peticion
// con "Session has not been configured for this application or request" — un fallo ruidoso, que
// es justamente por lo que CartStore no lo disimula devolviendo un carrito vacio.
app.UseSession();

// Aqui iba el app.UseAuthorization() de la plantilla. Se quita: no hay ningun esquema de
// autenticacion registrado detras, asi que el middleware no puede autorizar nada. Vuelve
// en 8.1, cuando el JWT del Gateway le de algo que mirar.

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();
