using MassTransit;

using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

using Orders.API.Consumers;
using Orders.Domain.Sagas;
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
            "Pedidos. El alta persiste el pedido y publica OrderCreated en RabbitMQ: no " +
            "llama a ningún otro servicio, así que Catalog puede estar caído y el pedido " +
            "se crea igual. Ese es el cambio de 3.3 — en la Fase 2 esa misma petición " +
            "devolvía 502 cuando Catalog no contestaba.\n\n" +
            "El precio de haber quitado la llamada es que el sku, el nombre y el precio de " +
            "cada línea los manda el cliente: Orders congela lo que recibe y no lo " +
            "contrasta con nadie.",
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

// Aquí vivía el AddHttpClient<CatalogClient> de 2.3, con su guarda de
// Services:CatalogBaseUrl y su timeout de 5 segundos. Lo borró 3.3: era la deuda
// deliberada de la regla 2 de CLAUDE.md, y su sustituto es el Publish de
// OrderCreated que hace OrdersController. No queda ni un HttpClient en el
// servicio, que es la comprobación más simple de que la llamada síncrona se fue
// de verdad y no se quedó escondida detrás de otro nombre.
//
// La clave Services:CatalogBaseUrl salió también de appsettings.json y de la
// fábrica de Orders.Tests: las guardas de este archivo y los UseSetting de esa
// fábrica cambian siempre juntos.

// --- Mensajería (3.1) -------------------------------------------------------
//
// El URI de RabbitMQ vive en User Secrets porque lleva usuario y contraseña:
// misma decisión que el connection string de arriba.
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
    // Se fijó en 3.1 con cero consumers, precisamente porque después no sale
    // gratis: el formatter decide el nombre de la cola de cada consumer, y
    // cambiarlo dejaría colas huérfanas en el broker que nadie vacía. 3.3 publica
    // el primer mensaje del proyecto y no lo tocó; 3.4 y 3.5 tampoco deben.
    //
    // Kebab-case en minúsculas: los nombres de cola de RabbitMQ distinguen
    // mayúsculas, y "order-created" no da lugar a dudas donde "OrderCreated" sí.
    x.SetKebabCaseEndpointNameFormatter();

    // --- La saga (4.1) ------------------------------------------------------
    //
    // Aquí es donde el bloque AddMassTransit deja de ser tres copias idénticas.
    // 3.1 aplazó su extracción, 3.4 y 3.5 la releyeron con el diff delante y la
    // dejaron: lo único que divergía era la línea AddConsumer, que es justo lo que
    // no se puede compartir. 3.5 dejó escrito que la próxima relectura sería 4.5,
    // con el outbox. Llega un punto antes: este AddSagaStateMachine no lo va a
    // tener nunca ni Inventory ni Payments. La conclusión no cambia — extraer la
    // mitad idéntica dejaría fuera precisamente lo que distingue cada servicio.
    //
    // El formatter de arriba nombra la cola a partir del tipo de la *instancia*,
    // no de la máquina de estados: OrderState → "order-state". Y quien la crea de
    // verdad es el ConfigureEndpoints de abajo; sin esa llamada, esto se registra
    // y no escucha nada, en silencio.
    //
    // InMemoryRepository() y no SQL Server: persistir el estado de la saga en
    // OrdersDb es 4.5, y son otra tabla, otro token de concurrencia optimista y la
    // pregunta de si comparte OrdersDbContext. Hasta entonces el estado vive en
    // memoria y **se pierde al reiniciar el servicio** — un pedido que estaba
    // esperando su StockReserved se queda sin saga que lo mueva y nadie se entera.
    // Está medido en la verificación de docs/fase_4_1.md, y es el argumento de 4.5.
    x.AddSagaStateMachine<OrderStateMachine, OrderState>()
        .InMemoryRepository();

    // --- Los dos primeros consumers del servicio (4.3) ----------------------
    //
    // Orders.API pasa a ser lo que hasta hoy solo eran Inventory y Payments: un
    // consumidor de mensajes, además de un API HTTP. Escuchan lo que publica la
    // saga de arriba —OrderConfirmed y OrderCancelled— y mueven el Order.Status
    // en OrdersDb, que es lo que la saga no puede hacer por sí misma: vive en
    // Orders.Domain y no ve OrdersDbContext (regla 5).
    //
    // Que el propio servicio consuma un mensaje que él mismo publica parece un
    // rodeo, y es exactamente el precio de esa regla — hecho visible en vez de
    // escondido detrás de una interfaz. Ver el /// de OrderConfirmedConsumer.
    //
    // Dos AddConsumer y no uno con dos interfaces: dos colas, order-confirmed y
    // order-cancelled. El motivo está en el /// de OrderCancelledConsumer.
    //
    // Con estas dos líneas, Orders es el primer servicio del proyecto con más de
    // un consumer, o sea el primero donde la clave compuesta (MessageId,
    // ConsumerName) de ProcessedMessages tiene un caso real.
    x.AddConsumer<OrderConfirmedConsumer>();
    x.AddConsumer<OrderCancelledConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        // Host(Uri) saca usuario y contraseña del userinfo del URI, así que no
        // hacen falta h.Username()/h.Password() por separado.
        cfg.Host(new Uri(rabbitMqConnectionString));

        // Puesta en 3.1 con cero consumers, por si acaso. Desde 4.1 ya no es
        // "por si acaso": es la línea que convierte el AddSagaStateMachine de
        // arriba en la cola order-state. Sin ella, registrar un IConsumer o una
        // saga no crea su receive endpoint y el mensaje se pierde en silencio,
        // que es el fallo más caro de diagnosticar de esta fase. La prueba de
        // que enganchó es la traza "Configured endpoint order-state, Saga: ..."
        // en el arranque.
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
