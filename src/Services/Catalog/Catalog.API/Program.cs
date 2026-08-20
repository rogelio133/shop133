using Catalog.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();

// El enrutado ya es case-insensitive, así que /products entra igual sin esto.
// Lo que arregla es la URL *generada*: sin ello, el Location del 201 de
// POST /products sale como "/Products/1" y el documento OpenAPI que consume 1.5
// publica las rutas capitalizadas, contradiciendo lo que dice el roadmap.
builder.Services.Configure<RouteOptions>(options => options.LowercaseUrls = true);
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
//
// El documento se generaba ya desde 1.2, pero con los metadatos por defecto:
// el título salía como "Catalog.API | v1", que es el nombre del ensamblado, no
// el del API. Un transformador de documento es la única vía para tocar el
// bloque "info" cuando no hay Swashbuckle de por medio.
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, context, cancellationToken) =>
{
    document.Info = new OpenApiInfo
    {
        Title = "shop133 — Catalog API",
        Version = "v1",
        Description =
            "Catálogo de productos de souvenirs. Es el servicio síncrono de la Fase 1: " +
            "dueño exclusivo de CatalogDb y única fuente de precios y datos de producto. " +
            "El Stock que se publica aquí es el que muestra el catálogo, no el stock " +
            "reservable — ese vive en InventoryDb desde la Fase 3.",
    };

    return Task.CompletedTask;
}));

// El connection string vive en User Secrets, nunca en appsettings.json: lleva
// la contraseña de catalog_user. Ver docs/fase_1_2.md.
//
// La guarda no es decorativa. UseSqlServer(null) revienta con un
// ArgumentNullException que no dice qué falta, y como User Secrets solo se
// cargan cuando el entorno es Development, el fallo aparece justo donde menos
// se espera: al ejecutar "dotnet ef", que no lee launchSettings.json.
var connectionString = builder.Configuration.GetConnectionString("CatalogDb")
    ?? throw new InvalidOperationException(
        "Falta la configuración 'ConnectionStrings:CatalogDb'. En local vive en User Secrets: " +
        "dotnet user-secrets set \"ConnectionStrings:CatalogDb\" \"Server=localhost,1433;Database=CatalogDb;" +
        "User Id=catalog_user;Password=...;TrustServerCertificate=True\" " +
        "--project src/Services/Catalog/Catalog.API");

// Sin Database.Migrate() al arrancar: las migraciones se aplican a mano con
// "dotnet ef database update" (ver Commands en CLAUDE.md). Migrar desde el
// arranque esconde el paso y no sobrevive a más de una instancia del servicio.
builder.Services.AddDbContext<CatalogDbContext>(options =>
    options.UseSqlServer(connectionString));

var app = builder.Build();

// Configure the HTTP request pipeline.
//
// Sin la guarda IsDevelopment() que traía la plantilla, y es una decisión, no un
// descuido: la imagen de 1.6 arranca en Production, así que con la guarda el
// contenedor no serviría ni el JSON ni la UI y el punto 1.5 solo existiría al
// ejecutar desde el IDE. Lo que se paga es que la superficie del API queda
// visible para quien alcance el puerto; se revisa cuando la Fase 5 ponga el
// Gateway delante y la 8.1 le añada autenticación.
//
// MapOpenApi sirve el documento en /openapi/v1.json; MapScalarApiReference, la
// interfaz en /scalar, que lee ese mismo JSON. Scalar no inspecciona la
// aplicación por su cuenta: sin la línea de arriba, la de abajo no tiene qué
// pintar.
app.MapOpenApi();
app.MapScalarApiReference();

// Guardado con IsDevelopment() desde 1.6, y por el motivo contrario al de
// MapOpenApi() de arriba: el contenedor solo escucha HTTP (ASPNETCORE_HTTP_PORTS
// = 8080, sin puerto https), asi que sin la guarda el middleware no encuentra a
// donde redirigir y loguea "Failed to determine the https port for redirect" en
// CADA peticion — un warning por request en "docker compose logs".
//
// Descartado dejarlo sin guarda y asumir el ruido, y descartado tambien borrar
// la linea: el perfil "https" de launchSettings.json sigue existiendo y ahi la
// redireccion si tiene sentido. Desde la Fase 5 la terminacion TLS es trabajo
// del Gateway, no de cada servicio.
if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseAuthorization();

app.MapControllers();

app.Run();
