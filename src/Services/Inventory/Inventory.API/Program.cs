using MassTransit;

using Microsoft.EntityFrameworkCore;

using Inventory.API.Consumers;
using Inventory.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// --- Persistencia (3.4) -----------------------------------------------------
//
// InventoryDb es la tercera base del sistema. La conexión va como
// inventory_user, que tiene db_owner sobre InventoryDb y ningún permiso sobre
// las otras tres — la regla 1 aplicada por el motor desde 0.4. Nunca sa.
//
// El connection string entero vive en User Secrets, no una plantilla sin
// contraseña en appsettings.json: la decisión 3 de docs/fase_1_2.md descartó eso
// porque deja versionado un connection string aparentemente válido que, copiado
// a otro servicio, arrastra el Database= y rompe la regla 1 sin que nadie lo note.
//
// La guarda revienta antes de app.Build() y nombra la clave que falta. En un
// servicio sin controllers como este importa más que en Orders: sin ella el
// fallo aparecería dentro del consumer, al resolver el DbContext, o sea como un
// mensaje en la cola order-created_error.
var inventoryDbConnectionString = builder.Configuration.GetConnectionString("InventoryDb")
    ?? throw new InvalidOperationException(
        "Falta la configuración 'ConnectionStrings:InventoryDb'. En local vive en User Secrets: " +
        "dotnet user-secrets set \"ConnectionStrings:InventoryDb\" \"Server=localhost,1433;Database=InventoryDb;" +
        "User Id=inventory_user;Password=...;TrustServerCertificate=True\" " +
        "--project src/Services/Inventory/Inventory.API");

// Sin Database.Migrate() al arrancar: las migraciones se aplican a mano con
// "dotnet ef database update" (ver Commands en CLAUDE.md). Migrar desde el
// arranque esconde el paso y no sobrevive a más de una instancia del servicio.
builder.Services.AddDbContext<InventoryDbContext>(options =>
    options.UseSqlServer(inventoryDbConnectionString));

// --- Mensajería (3.1, con su primer consumer en 3.4) ------------------------
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

// El bloque sigue siendo casi una copia literal del de Orders.API y Payments.API
// y NO se extrae, revisada la decisión 7 de docs/fase_3_1.md ahora que 3.4 ha
// tocado una de las tres copias: lo único que ha divergido es el AddConsumer de
// abajo, que es precisamente la parte que no se puede compartir. Sacar a un
// método común lo que sí es idéntico —host y formatter— dejaría la línea que
// distingue a cada servicio suelta fuera de él, que es peor de leer que la
// duplicación. Se vuelve a mirar en 3.5, con la copia de Payments ya tocada.
builder.Services.AddMassTransit(x =>
{
    // El primer consumer del proyecto. Registrarlo aquí es lo que crea la cola
    // "order-created" y la liga al exchange Shop133.Contracts.Events:OrderCreated
    // — hasta 3.3 ese fanout no tenía colas y el mensaje se publicaba al vacío.
    x.AddConsumer<OrderCreatedConsumer>();

    // El segundo, de 4.4, y con él Inventory pasa a tener dos colas. Ésta se llama
    // "release-stock" por el mismo formatter de abajo, y ese nombre **no es un
    // detalle interno**: la OrderStateMachine manda el comando con un Send a
    // queue:release-stock, o sea que lo tiene escrito. Cambiar el formatter, o
    // renombrar el consumer, deja los comandos apilándose en una cola que nadie
    // lee, sin error y sin aviso.
    //
    // Es también el primer consumer de un COMANDO del proyecto: los otros cuatro
    // reaccionan a hechos, a éste se le manda hacer algo.
    x.AddConsumer<ReleaseStockConsumer>();

    // Fijado en 3.1 con cero consumers, precisamente porque después no salía
    // gratis: el formatter decide el nombre de la cola de cada consumer, y
    // cambiarlo hoy dejaría colas huérfanas en el broker que nadie vacía.
    //
    // Kebab-case en minúsculas: los nombres de cola de RabbitMQ distinguen
    // mayúsculas, y "order-created" no da lugar a dudas donde "OrderCreated" sí.
    // Se le quita el sufijo "Consumer" al tipo, así que OrderCreatedConsumer da
    // la cola "order-created".
    x.SetKebabCaseEndpointNameFormatter();

    x.UsingRabbitMq((context, cfg) =>
    {
        // Host(Uri) saca usuario y contraseña del userinfo del URI, así que no
        // hacen falta h.Username()/h.Password() por separado.
        cfg.Host(new Uri(rabbitMqConnectionString));

        // Ahora sí registra algo. Sin esta línea, el AddConsumer de arriba no
        // crea ningún receive endpoint y el mensaje se pierde en silencio: el
        // fallo más caro de diagnosticar de esta fase, y el motivo por el que
        // 3.1 la dejó puesta cuando todavía no hacía nada.
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
