using Catalog.API;
using Catalog.API.Consumers;
using Catalog.Infrastructure.Persistence;

using MassTransit;

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

// --- Validación de la foto de precios (4.8) ---------------------------------
//
// Sin guarda, a propósito y al contrario que las dos claves de arriba y abajo:
// tiene un valor por defecto sensato y su ausencia no deja el servicio a medias.
// Mismo criterio literal que Payments:DeclineAmountAbove en 3.5. Ver el /// de
// PricingValidationOptions, donde está el argumento entero.
builder.Services.Configure<PricingValidationOptions>(
    builder.Configuration.GetSection(PricingValidationOptions.SectionName));

// --- Mensajería (4.8) -------------------------------------------------------
//
// Catalog era el ÚNICO de los cinco servicios sin MassTransit: desde 1.6 solo se
// le podía hablar por HTTP, y 3.3 borró la última llamada síncrona que alguien le
// hacía. Entra ahora porque 4.8 le da dueño al importe del pedido — ver el /// de
// OrderCreatedPricingConsumer.
//
// El URI de RabbitMQ vive en User Secrets porque lleva usuario y contraseña,
// igual que en los otros cuatro servicios desde 3.1.
//
// La guarda no es decorativa. Sin ella la clave ausente no falla aquí: falla al
// arrancar el bus, dentro de un hosted service, con un mensaje que no menciona la
// configuración. Aquí revienta antes de app.Build(), diciendo qué falta.
//
// Y tiene dos consecuencias fuera de este archivo que hay que recordar juntas,
// porque las dos rompen algo que ya funcionaba:
//
//   1. Catalog.Tests. La regla que escribió 3.1: cada guarda nueva en un
//      Program.cs es una línea nueva en la fábrica de su suite, porque
//      WebApplicationFactory<Program> ejecuta este archivo y esta línea lanza
//      ANTES de app.Build(), así que ConfigureTestServices no llega a tener turno.
//      Sin tocar CatalogApiFactory, los 19 tests de 1.7 van a rojo en el
//      constructor.
//
//   2. El contenedor. catalog-api es el único servicio contenedorizado (1.6) y en
//      Production NO se cargan User Secrets, así que docker-compose.yml tiene que
//      traer ConnectionStrings__RabbitMq o el contenedor muere al arrancar.
var rabbitMqConnectionString = builder.Configuration.GetConnectionString("RabbitMq")
    ?? throw new InvalidOperationException(
        "Falta la configuración 'ConnectionStrings:RabbitMq'. En local vive en User Secrets: " +
        "dotnet user-secrets set \"ConnectionStrings:RabbitMq\" \"amqp://guest:guest@localhost:5672\" " +
        "--project src/Services/Catalog/Catalog.API");

// Sexta copia casi literal del bloque de Orders, Inventory, Payments y
// Notifications, y sigue SIN extraerse. La revisión se cerró en 3.5 y se
// reconfirmó en 4.5 y 4.6: lo único que diverge entre servicios son los
// AddConsumer, que es justo lo que no se puede compartir.
//
// Como el de Notifications y al revés que el de Orders, **este bloque no configura
// ningún outbox**. El motivo aquí no es que Catalog no publique —publica dos
// eventos— sino que su consumer no hace ninguna escritura de negocio con la que
// hubiera que casar el mensaje: lo único que escribe es la marca de idempotencia.
// El agujero que queda está anotado en el consumer.
builder.Services.AddMassTransit(x =>
{
    // ── El nombre de esta clase es load-bearing, igual que en 4.6 ──
    //
    // El formatter de abajo deriva la cola del nombre del tipo menos el sufijo
    // "Consumer", así que ésta da "order-created-pricing".
    //
    // Llamarla OrderCreatedConsumer —que es lo que pide la convención del
    // proyecto— daría "order-created", que **ya es de Inventory.API desde 3.4**.
    // Dos servicios sobre la misma cola no son dos suscriptores del fanout: son
    // consumidores COMPETIDORES, y cada OrderCreated llegaría solo a uno de los
    // dos, sin un solo error en ningún log. Ver el /// del consumer.
    x.AddConsumer<OrderCreatedPricingConsumer>();

    // Kebab-case en minúsculas, el mismo formatter que los otros cuatro servicios
    // desde 3.1.
    x.SetKebabCaseEndpointNameFormatter();

    x.UsingRabbitMq((context, cfg) =>
    {
        // Host(Uri) saca usuario y contraseña del userinfo del URI, así que no
        // hacen falta h.Username()/h.Password() por separado.
        cfg.Host(new Uri(rabbitMqConnectionString));

        // Sin esta línea el AddConsumer de arriba no crea ningún receive endpoint
        // y el mensaje se pierde en silencio: el fallo más caro de diagnosticar de
        // la Fase 3, y el motivo por el que 3.1 la dejó puesta cuando todavía no
        // hacía nada.
        //
        // La prueba de que funcionó es la línea "Configured endpoint
        // order-created-pricing, Consumer: ..." en el log de arranque, antes de
        // "Bus started". Esa línea es además la fuente de verdad del nombre de la
        // cola, del que depende toda la propiedad de seguridad de arriba.
        cfg.ConfigureEndpoints(context);
    });
});

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

// Los top-level statements generan una clase Program *internal*, y
// WebApplicationFactory<Program> necesita que el tipo sea accesible desde
// Catalog.Tests. Esta línea es la única razón por la que existe: no añade
// comportamiento, solo hace público el tipo que el compilador ya genera.
//
// Descartado <InternalsVisibleTo Include="Catalog.Tests" /> en el .csproj, que
// dejaría Program internal y acotaría el permiso a un ensamblado con nombre.
// Es la opción más estricta, pero pone la razón en un archivo distinto del que
// la provoca: quien lea Program.cs no vería por qué el tipo es visible desde
// fuera. Aquí el motivo está a la vista, que es lo que este repositorio valora.
public partial class Program { }
