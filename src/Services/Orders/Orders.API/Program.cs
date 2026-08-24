using Microsoft.EntityFrameworkCore;

using Orders.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// El connection string vive en User Secrets, nunca en appsettings.json: lleva la
// contraseña de orders_user. Misma decisión que Catalog (decisión 3 de
// docs/fase_1_2.md) — una plantilla versionada dejaría un connection string
// aparentemente válido en el repositorio, y el día que alguien lo copie a otro
// servicio se llevará el "Database=OrdersDb" con él.
//
// La guarda no es decorativa. UseSqlServer(null) revienta con un
// ArgumentNullException que no dice qué falta, y como User Secrets solo se
// cargan cuando el entorno es Development, el fallo aparece justo donde menos se
// espera: al ejecutar "dotnet ef", que no lee launchSettings.json.
var connectionString = builder.Configuration.GetConnectionString("OrdersDb")
    ?? throw new InvalidOperationException(
        "Falta la configuración 'ConnectionStrings:OrdersDb'. En local vive en User Secrets: " +
        "dotnet user-secrets set \"ConnectionStrings:OrdersDb\" \"Server=localhost,1433;Database=OrdersDb;" +
        "User Id=orders_user;Password=...;TrustServerCertificate=True\" " +
        "--project src/Services/Orders/Orders.API");

// Sin Database.Migrate() al arrancar: las migraciones se aplican a mano con
// "dotnet ef database update" (ver Commands en CLAUDE.md). Migrar desde el
// arranque esconde el paso y no sobrevive a más de una instancia del servicio.
builder.Services.AddDbContext<OrdersDbContext>(options =>
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
