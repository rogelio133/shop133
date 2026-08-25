using MassTransit;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// --- Mensajería (3.1) -------------------------------------------------------
//
// Este servicio todavía no tiene ni entidades ni base de datos: lo único que
// hace de momento es conectarse al broker. Los consumers de OrderCreated y la
// reserva de stock contra InventoryDb llegan en 3.4.
//
// El URI de RabbitMQ vive en User Secrets porque lleva usuario y contraseña,
// igual que el connection string de Catalog (1.2) y el de Orders (2.2).
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
        "--project src/Services/Inventory/Inventory.API");

// El bloque es una copia literal del de Orders.API y Payments.API, y eso es
// deliberado: extraerlo a un método de extensión compartido exigiría un proyecto
// que los tres referencien, y Shop133.Contracts tiene que quedarse en cero
// paquetes (regla 4 de CLAUDE.md). Mismo criterio que la copia literal de
// SqlServerContainerFixture en 2.4 — con tres copias todavía no hay evidencia
// de qué se va a divergir. Se reevalúa cuando 3.4 y 3.5 las hayan tocado.
builder.Services.AddMassTransit(x =>
{
    // Se fija ahora, con cero consumers, precisamente porque después no sale
    // gratis: el formatter decide el nombre de la cola de cada consumer, y
    // cambiarlo en 3.4 dejaría colas huérfanas en el broker que nadie vacía.
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
        // línea que 3.4 espera encontrar — sin ella, registrar un IConsumer no
        // crea su receive endpoint y el mensaje se pierde en silencio, que es el
        // fallo más caro de diagnosticar de esta fase.
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
