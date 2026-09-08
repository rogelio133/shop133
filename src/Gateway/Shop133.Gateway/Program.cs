// shop133 — API Gateway (punto 5.1)
//
// Es la puerta única del sistema: la regla 3 de CLAUDE.md dice que Shop133.Web
// nunca guarda la URL base de un servicio concreto, así que a partir de la Fase 6
// todo el tráfico del frontend entra por aquí.
//
// Este archivo es deliberadamente corto. La tabla de enrutado no vive en código
// sino en appsettings.json, bajo la sección "ReverseProxy" — ver la decisión 3 de
// docs/fase_5_1.md.

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

builder.Services.AddReverseProxy()
    .LoadFromConfig(reverseProxySection);

var app = builder.Build();

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
