using MassTransit;

using Microsoft.EntityFrameworkCore;

using Notifications.API.Consumers;
using Notifications.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// --- Persistencia (4.6) -----------------------------------------------------
//
// NotificationsDb es la QUINTA base del sistema y la primera que aparece después
// de la Fase 0: las otras cuatro las creó db/init/01-create-databases.sql desde
// el principio, y en 4.6 ese script gana su quinto bloque.
//
// La conexión va como notifications_user, que tiene db_owner sobre
// NotificationsDb y ningún permiso sobre las otras cuatro — la regla 1 aplicada
// por el motor desde 0.4. Nunca sa.
//
// Que este servicio tenga base de datos NO contradice el /// de OrderConfirmed,
// que afirma desde 0.3 que Notifications "no puede leer OrdersDb" y que por eso
// el CustomerEmail viaja en el evento. Sigue sin poder leerla. Esta base entra por
// el mismo motivo que la de Payments en 3.5: sin una fila que consultar, el
// consumer no puede ser idempotente de ninguna forma, y la regla 6 no admite
// excepciones.
//
// El connection string entero vive en User Secrets, no una plantilla sin
// contraseña en appsettings.json: la decisión 3 de docs/fase_1_2.md descartó eso
// porque deja versionado un connection string aparentemente válido que, copiado
// a otro servicio, arrastra el Database= y rompe la regla 1 sin que nadie lo note.
//
// La guarda revienta antes de app.Build() y nombra la clave que falta. En un
// servicio sin controllers como éste importa más que en Orders: sin ella el fallo
// aparecería dentro del consumer, al resolver el DbContext, o sea como un mensaje
// en la cola order-confirmed-notification_error.
var notificationsDbConnectionString = builder.Configuration.GetConnectionString("NotificationsDb")
    ?? throw new InvalidOperationException(
        "Falta la configuración 'ConnectionStrings:NotificationsDb'. En local vive en User Secrets: " +
        "dotnet user-secrets set \"ConnectionStrings:NotificationsDb\" \"Server=localhost,1433;" +
        "Database=NotificationsDb;User Id=notifications_user;Password=...;TrustServerCertificate=True\" " +
        "--project src/Services/Notifications/Notifications.API");

// Sin Database.Migrate() al arrancar: las migraciones se aplican a mano con
// "dotnet ef database update" (ver Commands en CLAUDE.md). Migrar desde el
// arranque esconde el paso y no sobrevive a más de una instancia del servicio.
builder.Services.AddDbContext<NotificationsDbContext>(options =>
    options.UseSqlServer(notificationsDbConnectionString));

// --- Mensajería (4.6) -------------------------------------------------------
//
// El URI de RabbitMQ vive en User Secrets porque lleva usuario y contraseña,
// igual que en los otros cuatro servicios desde 3.1.
//
// La guarda no es decorativa. Sin ella la clave ausente no falla aquí: falla al
// arrancar el bus, dentro de un hosted service, con un mensaje que no menciona
// la configuración. Aquí revienta antes de app.Build(), diciendo qué falta.
var rabbitMqConnectionString = builder.Configuration.GetConnectionString("RabbitMq")
    ?? throw new InvalidOperationException(
        "Falta la configuración 'ConnectionStrings:RabbitMq'. En local vive en User Secrets: " +
        "dotnet user-secrets set \"ConnectionStrings:RabbitMq\" \"amqp://guest:guest@localhost:5672\" " +
        "--project src/Services/Notifications/Notifications.API");

// Quinta copia casi literal del bloque de Orders, Inventory y Payments, y sigue
// SIN extraerse. La revisión se cerró en 3.5 y se reconfirmó en 4.5, cuando el
// outbox dejó la copia de Orders estructuralmente distinta: lo único que diverge
// entre servicios son los AddConsumer, que es justo lo que no se puede compartir.
// Sacar a un método común lo que sí es idéntico —host y formatter— dejaría fuera
// de él la línea que distingue a cada servicio, que se lee peor que la duplicación.
//
// Aquí hay además una diferencia de fondo: **este bloque no configura ningún
// outbox**, al revés que el de Orders. Notifications no publica nada — es el final
// de la coreografía, el único servicio que solo consume—, así que no hay doble
// escritura que cerrar.
builder.Services.AddMassTransit(x =>
{
    // ── Los nombres de estas dos clases son load-bearing ──
    //
    // El formatter de abajo deriva el nombre de la cola del tipo menos el sufijo
    // "Consumer", así que éstas dan "order-confirmed-notification" y
    // "order-cancelled-notification".
    //
    // Llamarlas OrderConfirmedConsumer / OrderCancelledConsumer —que es lo que pide
    // la convención del proyecto, "el consumer se llama como el mensaje"— daría las
    // colas "order-confirmed" y "order-cancelled", que **ya son de Orders.API desde
    // 4.3**. Dos servicios sobre la misma cola no son dos suscriptores del fanout:
    // son consumidores COMPETIDORES, y cada evento llegaría solo a uno de los dos.
    // La mitad de los pedidos se quedaría sin mover su Order.Status y la otra mitad
    // sin aviso, sin un solo error en ningún log.
    //
    // Ningún test de arquitectura puede ver esto: leen .csproj y rutas de archivo,
    // no la topología de un broker. Se verifica contra RabbitMQ.
    x.AddConsumer<OrderConfirmedNotificationConsumer>();
    x.AddConsumer<OrderCancelledNotificationConsumer>();

    // Kebab-case en minúsculas, el mismo formatter que los otros cuatro servicios
    // desde 3.1. No se cambia por un prefijo de servicio para resolver lo de
    // arriba: dejaría a Notifications con una convención de nombres distinta a la
    // de todo el resto, y el problema real —que dos clases homónimas colisionan—
    // seguiría ahí para el siguiente que lo pise.
    x.SetKebabCaseEndpointNameFormatter();

    x.UsingRabbitMq((context, cfg) =>
    {
        // Host(Uri) saca usuario y contraseña del userinfo del URI, así que no
        // hacen falta h.Username()/h.Password() por separado.
        cfg.Host(new Uri(rabbitMqConnectionString));

        // Sin esta línea los AddConsumer de arriba no crean ningún receive
        // endpoint y el mensaje se pierde en silencio: el fallo más caro de
        // diagnosticar de la Fase 3, y el motivo por el que 3.1 la dejó puesta
        // cuando todavía no hacía nada.
        //
        // La prueba de que funcionó son las dos líneas "Configured endpoint
        // order-confirmed-notification, Consumer: ..." en el log de arranque, antes
        // de "Bus started".
        cfg.ConfigureEndpoints(context);
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Sin guarda IsDevelopment(), igual que Inventory.API y Payments.API. Catalog sí
// la lleva desde 1.6 porque tiene contenedor y allí escucha solo en HTTP; el sitio
// de releer esta línea es el día que Notifications tenga imagen.
app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();

// Sin "public partial class Program { }", al revés que Catalog.API y Orders.API.
// Es la misma respuesta que dieron 3.4 y 3.5: sólo hace falta para que
// WebApplicationFactory<Program> vea el tipo, y 4.6 no trae suite. Si algún día la
// trae, el patrón a copiar es el de Inventory.Tests/Payments.Tests —un
// ServiceCollection alrededor del consumer— que tampoco lo necesita.
