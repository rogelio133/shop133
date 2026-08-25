using MassTransit;

using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

using Orders.Infrastructure.Catalog;
using Orders.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();

// El enrutado ya es case-insensitive, así que /orders entra igual sin esto. Lo
// que arregla es la URL *generada*: sin ello, el Location del 201 de POST /orders
// sale como "/Orders/{guid}". Misma razón que en Catalog (1.5).
builder.Services.Configure<RouteOptions>(options => options.LowercaseUrls = true);

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
//
// Con los metadatos por defecto el título sale como "Orders.API | v1", que es el
// nombre del ensamblado. Un transformador de documento es la única vía para tocar
// el bloque "info" sin Swashbuckle de por medio.
//
// OpenApiInfo vive en Microsoft.OpenApi y NO en Microsoft.OpenApi.Models: la v2
// movió los tipos, así que el using de cualquier tutorial anterior no compila.
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, context, cancellationToken) =>
{
    document.Info = new OpenApiInfo
    {
        Title = "shop133 — Orders API",
        Version = "v1",
        Description =
            "Pedidos. En la Fase 2 el alta llama a Catalog.API de forma síncrona para " +
            "congelar sku, nombre y precio de cada línea: es deuda deliberada, y por eso " +
            "un Catalog caído devuelve 502 y el pedido no se crea. En la Fase 3 esa " +
            "llamada desaparece y Orders publica OrderCreated en RabbitMQ.",
    };

    return Task.CompletedTask;
}));

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

// PHASE-2 DEBT: replaced by OrderCreated event in Phase 3.
//
// La dirección de Catalog NO es un secreto, así que vive en appsettings.json y no
// en User Secrets — al contrario que el connection string, que lleva contraseña.
// Se sobreescribe con la variable de entorno Services__CatalogBaseUrl el día que
// Orders tenga contenedor (allí sería http://catalog-api:8080).
//
// La guarda existe por el mismo motivo que la del connection string: sin ella,
// new Uri(null) revienta con un mensaje que no dice qué falta.
var catalogBaseUrl = builder.Configuration["Services:CatalogBaseUrl"]
    ?? throw new InvalidOperationException(
        "Falta la configuración 'Services:CatalogBaseUrl'. Es la dirección de Catalog.API " +
        "(en local, http://localhost:5124) y vive en appsettings.json, no en User Secrets: " +
        "no es un secreto.");

builder.Services.AddHttpClient<CatalogClient>(client =>
{
    // La barra final es obligatoria. Uri combina base + relativa descartando el
    // último segmento de la base si no acaba en "/", así que sin ella una base
    // como "http://gateway/api/catalog" perdería el "/catalog" al pedir
    // "products/1". Hoy la base es la raíz y no se notaría; en la Fase 5, con el
    // Gateway delante, sí.
    client.BaseAddress = new Uri(catalogBaseUrl.TrimEnd('/') + '/');

    // 5 segundos en vez de los 100 de fábrica. Con el valor por defecto, "Catalog
    // caído" tardaría minuto y medio por línea en devolver el 502 — el test de
    // 2.4 sería inviable y el fallo parecería un cuelgue en vez de un error.
    client.Timeout = TimeSpan.FromSeconds(5);
});

// --- Mensajería (3.1) -------------------------------------------------------
//
// El URI de RabbitMQ vive en User Secrets porque lleva usuario y contraseña:
// misma decisión que el connection string de arriba, y la opuesta a la de
// Services:CatalogBaseUrl, que está en appsettings.json justamente por no ser
// un secreto.
//
// La guarda no es decorativa. Sin ella la clave ausente no falla aquí: falla al
// arrancar el bus, dentro de un hosted service, con un mensaje que no menciona
// la configuración. Aquí revienta antes de app.Build(), diciendo qué falta.
//
// En contenedor se sobreescribe con ConnectionStrings__RabbitMq, construido a
// partir de ${RABBITMQ_DEFAULT_USER}/${RABBITMQ_DEFAULT_PASS} como hace
// catalog-api con ${CATALOG_DB_PASSWORD}. Allí el host es "rabbitmq" —el nombre
// del servicio dentro de shop133-net—, no "localhost".
var rabbitMqConnectionString = builder.Configuration.GetConnectionString("RabbitMq")
    ?? throw new InvalidOperationException(
        "Falta la configuración 'ConnectionStrings:RabbitMq'. En local vive en User Secrets: " +
        "dotnet user-secrets set \"ConnectionStrings:RabbitMq\" \"amqp://guest:guest@localhost:5672\" " +
        "--project src/Services/Orders/Orders.API");

builder.Services.AddMassTransit(x =>
{
    // Se fija ahora, con cero consumers, precisamente porque después no sale
    // gratis: el formatter decide el nombre de la cola de cada consumer, y
    // cambiarlo en 3.3 dejaría colas huérfanas en el broker que nadie vacía.
    //
    // Kebab-case en minúsculas: los nombres de cola de RabbitMQ distinguen
    // mayúsculas, y "order-created" no da lugar a dudas donde "OrderCreated" sí.
    x.SetKebabCaseEndpointNameFormatter();

    x.UsingRabbitMq((context, cfg) =>
    {
        // Host(Uri) saca usuario y contraseña del userinfo del URI, así que no
        // hacen falta h.Username()/h.Password() por separado.
        cfg.Host(new Uri(rabbitMqConnectionString));

        // Hoy no registra nada: no hay consumers. Se deja puesta porque es la
        // línea que 3.4 y 3.5 esperan encontrar — sin ella, registrar un
        // IConsumer no crea su receive endpoint y el mensaje se pierde en
        // silencio, que es el fallo más caro de diagnosticar de esta fase.
        cfg.ConfigureEndpoints(context);
    });
});

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

// Los top-level statements generan una clase Program *internal*, y
// WebApplicationFactory<Program> necesita que el tipo sea accesible desde
// Orders.Tests (2.4). Esta línea es la única razón por la que existe: no añade
// comportamiento, solo hace público el tipo que el compilador ya genera.
//
// Misma decisión que en Catalog.API desde 1.7, con el mismo descarte:
// <InternalsVisibleTo> sería más estricto pero pondría el motivo en un archivo
// distinto del que lo provoca.
public partial class Program { }
