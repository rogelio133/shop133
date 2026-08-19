using Catalog.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();

// El enrutado ya es case-insensitive, así que /products entra igual sin esto.
// Lo que arregla es la URL *generada*: sin ello, el Location del 201 de
// POST /products sale como "/Products/1" y el documento OpenAPI que consume 1.5
// publica las rutas capitalizadas, contradiciendo lo que dice el roadmap.
builder.Services.Configure<RouteOptions>(options => options.LowercaseUrls = true);
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

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
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
