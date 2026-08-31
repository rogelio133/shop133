using MassTransit;

using Microsoft.EntityFrameworkCore;

using Payments.API;
using Payments.API.Consumers;
using Payments.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// --- Persistencia (3.5) -----------------------------------------------------
//
// PaymentsDb es la cuarta y última base del sistema: existe vacía desde 0.4 y no
// se usa hasta aquí. La conexión va como payments_user, que tiene db_owner sobre
// PaymentsDb y ningún permiso sobre las otras tres — la regla 1 aplicada por el
// motor, no por convención. Nunca sa.
//
// Es lo que hace que "leer el total del pedido en OrdersDb" no sea una tentación
// sino un Msg 916, y por eso el importe tiene que viajar dentro del evento
// (decisión 1 de docs/fase_3_2.md).
//
// El connection string entero vive en User Secrets, no una plantilla sin
// contraseña en appsettings.json: la decisión 3 de docs/fase_1_2.md descartó eso
// porque deja versionado un connection string aparentemente válido que, copiado
// a otro servicio, arrastra el Database= y rompe la regla 1 sin que nadie lo note.
//
// La guarda revienta antes de app.Build() y nombra la clave que falta. En un
// servicio sin ningún endpoint HTTP como este importa más todavía que en
// Inventory: sin ella el fallo aparecería dentro del consumer, al resolver el
// DbContext, o sea como un mensaje en la cola stock-reserved_error, a varios
// saltos de la causa.
var paymentsDbConnectionString = builder.Configuration.GetConnectionString("PaymentsDb")
    ?? throw new InvalidOperationException(
        "Falta la configuración 'ConnectionStrings:PaymentsDb'. En local vive en User Secrets: " +
        "dotnet user-secrets set \"ConnectionStrings:PaymentsDb\" \"Server=localhost,1433;Database=PaymentsDb;" +
        "User Id=payments_user;Password=...;TrustServerCertificate=True\" " +
        "--project src/Services/Payments/Payments.API");

// Sin Database.Migrate() al arrancar: las migraciones se aplican a mano con
// "dotnet ef database update" (ver Commands en CLAUDE.md). Migrar desde el
// arranque esconde el paso y no sobrevive a más de una instancia del servicio.
builder.Services.AddDbContext<PaymentsDbContext>(options =>
    options.UseSqlServer(paymentsDbConnectionString));

// --- La pasarela simulada (3.5) ---------------------------------------------
//
// Aquí NO hay guarda, y es deliberado: al contrario que los connection strings,
// esta sección tiene un valor por defecto sensato y su ausencia no deja al
// servicio a medias. Ver el /// de PaymentSimulationOptions.
builder.Services.Configure<PaymentSimulationOptions>(
    builder.Configuration.GetSection(PaymentSimulationOptions.SectionName));

// --- Mensajería (3.1, con su consumer en 3.5) -------------------------------
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
        "--project src/Services/Payments/Payments.API");

// El bloque sigue siendo casi una copia literal del de Orders.API e Inventory.API
// y NO se extrae. La decisión 7 de docs/fase_3_1.md aplazó la pregunta hasta que
// 3.4 y 3.5 hubieran tocado dos de las tres copias; con las tres ya tocadas, la
// revisión se CIERRA aquí: lo único que ha divergido es el AddConsumer de abajo,
// que es precisamente la parte que no se puede compartir. Sacar a un método común
// lo que sí es idéntico —host y formatter— dejaría la línea que distingue a cada
// servicio suelta fuera de él, y eso se lee peor que la duplicación. Y exigiría
// un proyecto nuevo que los tres referencien y que cargue MassTransit, cosa que
// Shop133.Contracts no puede hacer (regla 4).
//
// El siguiente punto de relectura es 4.5, y esta vez con una divergencia real:
// el outbox transaccional mete MassTransit.EntityFrameworkCore y una
// configuración de persistencia SOLO en Orders.
builder.Services.AddMassTransit(x =>
{
    // El segundo consumer del proyecto. Registrarlo aquí es lo que crea la cola
    // "stock-reserved" y la liga al exchange Shop133.Contracts.Events:StockReserved
    // — hasta 3.4 ese fanout no tenía colas y el mensaje se publicaba al vacío.
    x.AddConsumer<StockReservedConsumer>();

    // Fijado en 3.1 con cero consumers, precisamente porque después no salía
    // gratis: el formatter decide el nombre de la cola de cada consumer, y
    // cambiarlo hoy dejaría colas huérfanas en el broker que nadie vacía.
    //
    // Kebab-case en minúsculas: los nombres de cola de RabbitMQ distinguen
    // mayúsculas, y "stock-reserved" no da lugar a dudas donde "StockReserved" sí.
    // Se le quita el sufijo "Consumer" al tipo, así que StockReservedConsumer da
    // la cola "stock-reserved".
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

// Sin ningún controller todavía, igual que Inventory.API. Este servicio no tiene
// superficie HTTP: todo lo que hace entra por la cola stock-reserved.
app.MapControllers();

app.Run();
