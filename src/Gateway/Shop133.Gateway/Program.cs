// shop133 — API Gateway (puntos 5.1 y 5.2)
//
// Es la puerta única del sistema: la regla 3 de CLAUDE.md dice que Shop133.Web
// nunca guarda la URL base de un servicio concreto, así que a partir de la Fase 6
// todo el tráfico del frontend entra por aquí.
//
// Este archivo es deliberadamente corto. La tabla de enrutado no vive en código
// sino en appsettings.json, bajo la sección "ReverseProxy" — ver la decisión 3 de
// docs/fase_5_1.md. Los cupos del rate limiting de 5.2 viven al lado, en la
// sección "RateLimiting", por el mismo motivo y con un motivo extra: 5.4 necesita
// poder bajarlos por variable de entorno para provocar el 429 sin mandar sesenta
// peticiones.

using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// La sección completa de YARP: rutas (qué prefijo va a qué cluster y con qué
// transformación) y clusters (a qué dirección se reenvía).
var reverseProxySection = builder.Configuration.GetSection("ReverseProxy");

// La guarda no es decorativa, y es el mismo criterio que las de
// ConnectionStrings:* en los cinco servicios desde 3.1.
//
// Sin ella, una sección ausente o vacía NO falla: AddReverseProxy arranca con
// cero rutas, el servicio levanta con normalidad y cada petición devuelve 404
// sin una sola línea en el log. Un 404 del Gateway es indistinguible de "esa
// ruta no existe", que es exactamente lo que /api/inventory/* devuelve a
// propósito — así que el fallo se diagnostica en el servicio de destino, a un
// salto de distancia de la causa. Aquí revienta antes de app.Build() diciendo
// qué falta.
if (!reverseProxySection.GetSection("Routes").GetChildren().Any())
{
    throw new InvalidOperationException(
        "Falta la configuración 'ReverseProxy:Routes' o está vacía. Es la tabla de enrutado del " +
        "Gateway y vive en appsettings.json (no es un secreto: son rutas y hosts, no credenciales). " +
        "Sin ella YARP arrancaría con cero rutas y devolvería 404 a todo, en silencio.");
}

// --- Rate limiting (5.2) -----------------------------------------------------
//
// El punto donde va es el que fija la regla 3 de CLAUDE.md: CORS, rate limiting y
// (más tarde) auth se centralizan aquí, porque el Gateway es lo único que el
// cliente alcanza. Ponerlo en cada servicio sería repetirlo cinco veces y dejarlo
// sin efecto para quien entre por la puerta.
//
// Ventana fija, y su defecto se dice en voz alta en vez de esconderlo: el
// contador se reinicia de golpe, así que un cliente puede gastar su cupo al final
// de una ventana y el cupo entero otra vez al principio de la siguiente — hasta
// 2N peticiones en un instante a caballo de las dos. *Descartada* la ventana
// deslizante (más justa, pero para razonar cuándo vuelve un permiso hay que saber
// en qué segmento se gastó) y *descartado* el token bucket (modela mejor el
// tráfico real, pero el umbral depende del tiempo transcurrido y eso volvería
// frágil el test de 5.4, que tiene que poder afirmar "la petición N+1 falla").
//
// La partición es por IP del cliente y no un contador único: con un cupo global
// compartido, un cliente agota la cuota de todos. Hoy el Gateway es el borde, así
// que la IP de la conexión ES la del cliente; el día que algo se ponga delante
// habrá que mirar X-Forwarded-For y todos colapsarían en una sola clave.

const string CatalogReadPolicy = "catalog-read";
const string OrdersWritePolicy = "orders-write";

// Los cupos son configuración, no literales, por el motivo de 5.4: bajarlos con
// RateLimiting__OrdersWrite__PermitLimit=3 es lo que permite provocar el 429 sin
// crear diez pedidos de verdad, cada uno de los cuales arranca la saga entera.
//
// La validación es la misma guarda de la sección ReverseProxy de más arriba y por
// el mismo motivo. Sin ella, una sección ausente da PermitLimit = 0 —el valor por
// defecto de GetValue<int>— y un limitador de cero permisos rechaza absolutamente
// todo con 429: el Gateway levantaría sin una queja y el diagnóstico empezaría
// buscando quién está inundando la puerta.
static (int PermitLimit, TimeSpan Window) ReadQuota(IConfiguration configuration, string name)
{
    var section = configuration.GetSection($"RateLimiting:{name}");
    var permitLimit = section.GetValue<int>("PermitLimit");
    var windowSeconds = section.GetValue<int>("WindowSeconds");

    if (permitLimit <= 0 || windowSeconds <= 0)
    {
        throw new InvalidOperationException(
            $"La configuración 'RateLimiting:{name}' falta o no es válida: PermitLimit y " +
            $"WindowSeconds tienen que ser mayores que 0 (leídos: {permitLimit} y {windowSeconds}). " +
            "Vive en appsettings.json junto a la tabla de enrutado. Sin esta guarda un cupo de 0 " +
            "permisos rechazaría todas las peticiones con 429 sin decir por qué.");
    }

    return (permitLimit, TimeSpan.FromSeconds(windowSeconds));
}

// QueueLimit = 0 a propósito: un límite que encola no es un límite, es un retraso.
// Encolar en la puerta le pasa la latencia al cliente y esconde que se pasó del
// cupo, que es justo lo que el 429 existe para decirle.
static RateLimitPartition<string> FixedWindowFor(
    string partitionKey,
    (int PermitLimit, TimeSpan Window) quota) =>
    RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = quota.PermitLimit,
        Window = quota.Window,
        QueueLimit = 0,
        AutoReplenishment = true
    });

static string ClientPartitionKey(HttpContext httpContext) =>
    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

var catalogReadQuota = ReadQuota(builder.Configuration, "CatalogRead");
var ordersWriteQuota = ReadQuota(builder.Configuration, "OrdersWrite");
var globalQuota = ReadQuota(builder.Configuration, "Global");

// Para que el 429 salga con forma de ProblemDetails, igual que los errores que
// producen los cinco servicios desde 2.3. Sin esto el rechazo sale con el cuerpo
// vacío y el cliente no sabe ni cuánto esperar.
builder.Services.AddProblemDetails();

builder.Services.AddRateLimiter(options =>
{
    // NO es decorativo: el valor por defecto de RejectionStatusCode es 503, no 429.
    // El roadmap pide 429 y 5.4 lo va a afirmar.
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Dos políticas y no una: el límite protege el COSTE, no el número de
    // peticiones. Un GET al catálogo lee una tabla; un POST /orders arranca la
    // saga entera — cinco servicios, doce mensajes, cinco bases de datos.
    options.AddPolicy<string>(CatalogReadPolicy, httpContext =>
        FixedWindowFor(ClientPartitionKey(httpContext), catalogReadQuota));

    options.AddPolicy<string>(OrdersWritePolicy, httpContext =>
        FixedWindowFor(ClientPartitionKey(httpContext), ordersWriteQuota));

    // Red de seguridad para lo que no declare política. Una ruta nueva a la que se
    // le olvide su "RateLimiterPolicy" en appsettings.json nace limitada igual, en
    // vez de nacer sin límite y sin que nadie se entere — el mismo criterio de la
    // guarda de 5.1: lo que hay que hacer imposible es el fallo silencioso.
    //
    // OJO: cuando una ruta SÍ declara política, se aplican LAS DOS (la del
    // endpoint y ésta). Por eso el cupo global tiene que quedar por encima de los
    // otros dos: si fuese el menor, sería él quien limita de verdad y las dos
    // políticas de arriba quedarían decorativas.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        FixedWindowFor(ClientPartitionKey(httpContext), globalQuota));

    options.OnRejected = async (context, cancellationToken) =>
    {
        // Retry-After sale de los metadatos del lease, no de una cuenta a mano: es
        // el limitador quien lleva el reloj.
        //
        // Medido en 5.2, porque no es lo que uno espera: el limitador de ventana
        // fija reporta LA VENTANA ENTERA, no lo que queda de ella — un rechazo a
        // mitad de una ventana de 60 s devuelve igualmente "Retry-After: 60". Es un
        // número conservador, no un fallo; calcularlo aquí sería duplicar ese reloj.
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        }

        var problemDetailsService = context.HttpContext.RequestServices
            .GetRequiredService<IProblemDetailsService>();

        await problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context.HttpContext,
            ProblemDetails =
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Demasiadas peticiones",
                Detail = "Se superó el límite de peticiones de esta ruta. " +
                         "La cabecera Retry-After dice cuántos segundos quedan para que " +
                         "se abra la siguiente ventana."
            }
        });
    };
});

builder.Services.AddReverseProxy()
    .LoadFromConfig(reverseProxySection);

var app = builder.Build();

// Antes de MapReverseProxy: es lo que aplica la política que cada ruta declara en
// appsettings.json con "RateLimiterPolicy" (YARP la traduce a metadata del
// endpoint). Sin esta línea la configuración estaría puesta y no haría nada.
app.UseRateLimiter();

// Sin UseHttpsRedirection(), al contrario que los cinco servicios hasta 5.1.
// Desde este punto la terminación TLS es trabajo del Gateway —lo que 1.6 dejó
// escrito en el Program.cs de Catalog—, y forzar aquí el upgrade tendría el mismo
// efecto que tenía allí: romper el salto del proxy. Terminar TLS de verdad es
// cosa del despliegue, no de este punto.

// Todo lo que este proceso hace. No hay MapGet("/") de la plantilla: el Gateway
// solo es dueño del espacio /api/*, así que un 404 en la raíz es la respuesta
// correcta. La sonda de vida llega en 8.4 con /health.
app.MapReverseProxy();

app.Run();
