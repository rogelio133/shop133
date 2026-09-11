using Shop133.Web.Gateway;

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

builder.Services.AddHttpClient<CatalogClient>(client =>
{
    // Con barra final: sin ella, Uri resuelve la ruta relativa contra el PADRE y se come el
    // ultimo segmento. Aqui la base SI tiene segmento de ruta ("/api/"), asi que esto muerde
    // de verdad — en 2.3 era una precaucion teorica.
    client.BaseAddress = new Uri(gatewayBaseUrl.TrimEnd('/') + "/api/");

    // 5 s en vez de los 100 de fabrica (precedente de 2.3). Con el valor por defecto, "el
    // Gateway esta caido" tardaria minuto y medio en pintar el aviso y pareceria un cuelgue.
    //
    // 6.6 TIENE QUE RELEER ESTA LINEA. Timeout es el plazo EXTERIOR de todo el pipeline de
    // resiliencia: con 5 s aqui, el total-request-timeout de 30 s del handler estandar de
    // Polly no cabe y los reintentos NO LLEGAN A EJECUTARSE NUNCA, sin error y sin aviso.
});

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

// Aqui iba el app.UseAuthorization() de la plantilla. Se quita: no hay ningun esquema de
// autenticacion registrado detras, asi que el middleware no puede autorizar nada. Vuelve
// en 8.1, cuando el JWT del Gateway le de algo que mirar.

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();
