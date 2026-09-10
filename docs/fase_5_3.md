# Fase 5.3 — CORS centralizado en el Gateway

**Fecha:** 2026-09-08 · **Estado:** completado · [Roadmap](../plan-desarrollo-shop133.md)

---

## Objetivo

`5.1` dejó `/api/catalog/*` y `/api/orders/*` como única superficie pública y `5.2` le puso cupos. Falta lo tercero que la **regla 3 de [CLAUDE.md](../CLAUDE.md)** manda centralizar aquí: *CORS, rate limiting y (más tarde) auth*. Hasta hoy **no había una sola línea de CORS en el repositorio** — comprobado con `grep` sobre `src/` antes de empezar—, así que cualquier `fetch` de navegador contra el Gateway lo bloqueaba el navegador.

Va aquí y no en los cinco servicios por el mismo motivo que el rate limiting: escribirlo cinco veces y dejarlo sin efecto para quien entre por la puerta —que desde la Fase 6 es todo el mundo— no es centralizar nada.

**Lo que este punto NO es, y conviene decirlo antes de que alguien lo dé por hecho.** El título dice *"para que el Frontend solo hable con el Gateway"*, pero `Shop133.Web` es **MVC renderizado en servidor**: sus llamadas al Gateway (el `IHttpClientFactory` de `6.6`) son servidor-a-servidor y **no pasan por CORS en absoluto** — CORS lo aplica el navegador, no el proceso que llama. Quien lo necesita de verdad es el **JavaScript** de `6.5` (el polling del estado del pedido) y `6.7`. Se configura igualmente y aquí porque es donde lo pone la regla 3 y porque `6.5` lo va a necesitar; lo que no se hace es fingir que sin esto la Fase 6 no arranca.

**Fuera de alcance, deliberadamente:** el smoke de enrutado (`5.4`), la autenticación (`8.1`), contenerizar el Gateway y tocar cualquiera de los cinco servicios. `Shop133.Web` sigue siendo la plantilla MVC intacta.

**Sin paquetes nuevos.** `Microsoft.AspNetCore.Cors` va en el framework compartido y `Yarp.ReverseProxy` 2.3.0 ya trae `RouteConfig.CorsPolicy` — comprobado en el XML del paquete antes de escribir una línea, igual que `5.2` hizo con `RateLimiterPolicy`. El Gateway se queda con **un solo `PackageReference` y cero `ProjectReference`**, que desde `5.1` es una regla ejecutable.

---

## Decisiones

### 1. Política con nombre declarada por ruta, no una política por defecto

La política se llama `frontend`, la registra `Program.cs` y **cada ruta la declara en `appsettings.json`** con `"CorsPolicy": "frontend"`, al lado de su `"RateLimiterPolicy"`. Así todo lo transversal de una ruta se lee en el mismo sitio, que es la forma que `5.2` ya dejó puesta.

*Descartado* `AddDefaultPolicy(...)` + `app.UseCors()` global, que es una línea menos. El motivo no es estético y está medido en la verificación 9: **YARP no engancha el preflight de una ruta que no declara `CorsPolicy`** — el `OPTIONS` se reenvía al servicio de destino, que contesta `405`. Una política por defecto dejaría entonces un estado a medias: las peticiones simples con cabeceras correctas y el preflight muriendo en un backend que no sabe nada de CORS. El XML del paquete lo dice en una frase: *"If not set then the route won't be automatically matched for cors preflight requests"*.

### 2. No hay red de seguridad al estilo del `GlobalLimiter`, y el motivo invierte el argumento de `5.2`

`5.2` añadió un `GlobalLimiter` que el título no pedía, con este razonamiento: una ruta futura que se olvide de declarar `RateLimiterPolicy` **nacería sin límite y sin que nadie se entere**, porque no hay error ni log.

Aquí ese argumento **no se traslada, y decirlo es la mitad del valor del punto**. Olvidar `CorsPolicy` en una ruta nueva no produce un fallo silencioso: el navegador bloquea la petición y lo grita en la consola, y el preflight ni siquiera llega a contestarse bien (`405`, verificación 9). El fallo ya es ruidoso, así que la red de seguridad no compra nada y en cambio introduce el estado a medias de la decisión 1.

La regla que queda escrita: **una red de seguridad se añade contra un fallo silencioso, no por simetría con el punto anterior.**

### 3. Lista estricta de orígenes, la misma en Development que en Production

`Cors:AllowedOrigins` son los dos perfiles de `launchSettings.json` de `Shop133.Web` — `http://localhost:5025` y `https://localhost:7227`.

*Descartado* un `AllowAnyOrigin()` de conveniencia en `appsettings.Development.json`, que es lo cómodo mientras se pelea con puertos en la Fase 6. Dos motivos: lo que se prueba a diario dejaría de ser lo que se despliega —y CORS es exactamente la clase de cosa que solo falla en el entorno donde no la probaste—, y `AllowAnyOrigin()` es **incompatible con `AllowCredentials()`**, que es justo lo que `8.1` puede querer. Estrenar hoy una configuración que hay que deshacer en `8.1` no es un atajo.

*Descartado* también añadir el origen del propio Gateway (`5104`/`7138`) por el Scalar que llega a través de `/api/catalog/scalar`: eso es mismo-origen y no manda cabecera `Origin`, así que serían dos entradas que no enganchan nunca — el *"filtro que nunca engancha"* que `3.2` rechazó.

### 4. `WithExposedHeaders("Location", "Retry-After")` — la parte menos obvia del punto

Por defecto el navegador solo deja que el JavaScript lea **seis** cabeceras de respuesta (la *safelist*: `Cache-Control`, `Content-Language`, `Content-Type`, `Expires`, `Last-Modified`, `Pragma`). Todo lo demás existe en la respuesta y es **invisible para el código que la recibió**. Dos cabeceras de este sistema caen fuera y las dos hacen falta:

- **`Location`**, la del `201` de `POST /api/orders`. Sin exponerla, `6.5` no puede saber qué pedido acaba de crear. Engancha con la deuda sin dueño de `5.1`: esa cabecera sigue apuntando a `http://localhost:5189/orders/{id}` —la dirección real del backend, sin el prefijo público— y eso este punto **no lo arregla**; ahora simplemente se puede leer, que no es lo mismo que estar bien.
- **`Retry-After`**, la del `429` de `5.2`. Sin exponerla el cliente ve el rechazo pero no cuánto esperar, que es justo lo que aquel punto se molestó en calcular.

Es la clase de detalle que no falla en `curl` —donde todas las cabeceras se ven— y falla en el navegador.

### 5. `AllowAnyMethod()` y `AllowAnyHeader()`, sin `AllowCredentials()`

**Qué se puede llamar lo decide la tabla de enrutado, no esta política.** CORS es una regla sobre **quién** (el origen), no sobre **qué**; enumerar aquí `GET, POST, PUT, DELETE` sería una segunda lista que mantener en sincronía con la superficie que el Gateway ya expone, y cuya divergencia no rompería ninguna build. `AllowAnyHeader()` por lo mismo, y porque `Content-Type: application/json` ya es lo que obliga al preflight del `POST`.

**Sin `AllowCredentials()`**: no hay cookies ni sesión que viaje al Gateway, y un JWT en `Authorization` **no la necesita** (es una cabecera normal, ya cubierta por `AllowAnyHeader`). Queda anotado para `8.1`, que es quien decide si aparecen cookies.

`SetPreflightMaxAge(10 min)` para que el `OPTIONS` del `POST` no se repita en cada pedido. Es conservador a propósito: los navegadores lo recortan por su cuenta y una política que cambia se despliega con el Gateway.

### 6. `UseCors()` **antes** de `UseRateLimiter()`, y las dos consecuencias están medidas

El orden en el pipeline no es cosmético, y el experimento inverso salió **peor de lo previsto** (verificación 8):

1. Con el orden correcto, el `429` sale **con** `Access-Control-Allow-Origin`, así que el JavaScript puede leer el código y el `Retry-After`. Invertido, el middleware de CORS ni se ejecuta: el navegador solo ve un error de red opaco. El límite seguiría funcionando y sería indiagnosticable desde el cliente, que es lo contrario de lo que el `429` existe para decirle.
2. Con el orden correcto el preflight lo contesta CORS y corta, así que **no gasta cupo**. Invertido lo previsto era "el cupo real se queda a la mitad para los navegadores"; lo medido es más gordo: con `OrdersWrite:PermitLimit=1` el preflight se come el único permiso y **el `POST` que venía detrás recibe `429`** — un navegador no consigue crear **ni un** pedido, mientras que un `curl` con el mismo cupo lo crea sin enterarse. **El cliente que respeta CORS sale penalizado por preguntar.**

### 7. CORS no es autorización, y se mide

La verificación 4 es la que más enseña del punto: una petición con `Origin: http://evil.example` devuelve **`200` con el cuerpo entero**, solo que sin ninguna cabecera `Access-Control-*`. Lo único que se le niega al atacante es el permiso para que **su JavaScript** lea la respuesta dentro de un navegador; `curl`, un script o cualquier cliente que no sea un navegador se lo saltan por completo.

Dicho de otra forma: esto protege a los usuarios del sistema de otras webs, **no protege al sistema de nadie**. Quien controla el acceso es `8.1`.

### 8. No entra ni una regla de arquitectura, y se dice por escrito

Precedente de `3.3`, `3.5`, `4.5` y la decisión 6 de `5.1`: no entra paquete, ni proyecto, ni una forma nueva que vigilar. La suite se queda en **17** y el repositorio en **105**. Inventar una regla para subir el contador es el *"filtro que nunca engancha"* de `3.2`.

Lo que sí queda **sin vigilar** y se anota como tal: nada comprueba que las dos rutas declaren `CorsPolicy`. Los tests de arquitectura leen `.csproj` y rutas de archivo, no `appsettings.json`; es el mismo tipo de hueco que la colisión de nombres de cola de `4.6`/`4.8`, y su candidato natural es `5.4`.

### 9. No se toca ningún servicio

A diferencia de `5.1`, que tuvo que quitarle `UseHttpsRedirection()` a Catalog.API y Orders.API, aquí no hay nada que retirar: ninguno de los cinco tenía CORS. Si algún día uno lo declara por su cuenta, el sitio donde mirar es esta decisión.

---

## Cambios

| Archivo | Rol |
|---|---|
| `src/Gateway/Shop133.Gateway/Program.cs` | Sección CORS nueva: la constante `FrontendCorsPolicy`, la guarda `ReadAllowedOrigins`, el `AddCors` con la política `frontend` y el `app.UseCors()` **antes** de `app.UseRateLimiter()`. |
| `src/Gateway/Shop133.Gateway/appsettings.json` | Sección `Cors:AllowedOrigins` con los dos orígenes de `Shop133.Web`, y `"CorsPolicy": "frontend"` en las dos rutas. |
| `docs/fase_5_3.md` | Este documento. |
| `docs/README.md` | Fila del índice. |
| `plan-desarrollo-shop133.md` | Casilla marcada + párrafo *Sobre 5.3*. |
| `CLAUDE.md` | Párrafo de `5.3`, tabla de estado y nota de CORS en el bloque de comandos del Gateway. |

La guarda tiene **la misma forma** que las otras dos del archivo (`ReverseProxy:Routes` de `5.1`, `RateLimiting:*` de `5.2`) y revienta antes de `app.Build()` en dos casos:

- **lista ausente o vacía** — la política no engancharía con ningún origen y el navegador bloquearía cada petición sin que el Gateway dijera nada;
- **origen con barra final o no absoluto** — la cabecera `Origin` nunca lleva barra y la comparación es literal, así que `http://localhost:5025/` **no engancha jamás** y el síntoma es idéntico a no haber configurado nada. Es EL fallo clásico de CORS y cuesta una tarde.

---

## Detalles que cuestan tiempo

- **El preflight no se parece a lo que uno imagina.** Un `OPTIONS` con `Origin` pero **sin** `Access-Control-Request-Method` no es un preflight: se reenvía al servicio y devuelve `405`. Solo con esa cabecera lo intercepta el middleware. Ese contraste (`405` vs `204` sobre la misma URL) es la mejor prueba de que el preflight **no llega al servicio** — mejor que buscarlo en un log, porque los cinco servicios tienen `Microsoft.AspNetCore` en `Warning` y no registran peticiones.
- **`$env:X = ""` en PowerShell BORRA la variable en vez de vaciarla.** Intentando medir una ruta sin `CorsPolicy` mediante `${env:ReverseProxy__Routes__catalog-route__CorsPolicy} = ""`, el override nunca llegó a existir: el Gateway siguió leyendo `frontend` del JSON y la medición salió *"funciona igual sin la línea"* — una conclusión **falsa** que casi acaba en este documento. La rotura de verdad hubo que hacerla renombrando la clave en `appsettings.json`. Es la versión PowerShell de la trampa que este repositorio lleva anotando desde `3.4`: la herramienta se come la entrada antes de que llegue a donde crees.
- **Una variable de entorno con guion no se puede escribir como `$env:Nombre`.** Los ids de ruta llevan guion (`catalog-route`), así que `$env:ReverseProxy__Routes__catalog-route__CorsPolicy` es un error de sintaxis (*"Unexpected token '-route'"*). Hay que usar `${env:...}`.
- **Un nombre de política inexistente NO arranca el Gateway**, igual que con `RateLimiterPolicy` en `5.2`: `CORS policy 'no-existe' not found for route 'catalog-route'` envuelto en `Unable to load or apply the proxy configuration`. Es la razón de que este punto no añada una guarda propia para eso — la de `5.1` existe contra un fallo silencioso, y éste es de todo menos silencioso.
- **`Vary: Origin` aparece solo cuando hay política.** Es lo que impide que una caché intermedia sirva a un origen la respuesta autorizada de otro; no hay que configurarlo, pero si falta en una respuesta que sí lleva `Access-Control-Allow-Origin`, hay un problema de caché esperando.

---

## Verificación

Con Catalog (5124), Orders (5189) y el Gateway (5104) levantados. Se usa el literal `127.0.0.1` por la dualidad `::1`/`127.0.0.1` que midió `5.2`.

**1. Compila.**

```
dotnet build src\Gateway\Shop133.Gateway
  Shop133.Gateway -> ...\bin\Debug\net10.0\Shop133.Gateway.dll
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

**2. Preflight de `POST /api/orders`.**

```
curl.exe -s -i -X OPTIONS "http://127.0.0.1:5104/api/orders" -H "Origin: http://localhost:5025" `
  -H "Access-Control-Request-Method: POST" -H "Access-Control-Request-Headers: content-type"

HTTP/1.1 204 No Content
Access-Control-Allow-Headers: content-type
Access-Control-Allow-Methods: POST
Access-Control-Allow-Origin: http://localhost:5025
Access-Control-Max-Age: 600
Vary: Origin
```

**3. El preflight no llega al servicio** — el mismo `OPTIONS` sin la cabecera que lo convierte en preflight sí se reenvía:

```
OPTIONS directo a Orders.API (sin Gateway)                       -> 405
OPTIONS por el Gateway SIN Access-Control-Request-Method         -> 405
OPTIONS por el Gateway CON preflight completo                    -> 204
```

**4. Origen permitido vs. origen ajeno vs. sin `Origin`** (`GET /api/catalog/products/1`):

```
--- Origin: http://localhost:5025 ---
HTTP/1.1 200 OK
Access-Control-Allow-Origin: http://localhost:5025
Access-Control-Expose-Headers: Location,Retry-After
Vary: Origin
{"id":1,"sku":"TAZA-001",...,"price":249.00,...}

--- Origin: http://evil.example ---
HTTP/1.1 200 OK
(ninguna cabecera Access-Control-*)
{"id":1,"sku":"TAZA-001",...,"price":249.00,...}   <-- el cuerpo entero

--- sin Origin ---
HTTP/1.1 200 OK
(ninguna cabecera Access-Control-*)
{"id":1,"sku":"TAZA-001",...}
```

Los tres cuerpos son idénticos: **CORS no es autorización** (decisión 7).

**5. El segundo origen de la lista también engancha** (`https://localhost:7227` sobre `/api/catalog/categories`) → `200` con `Access-Control-Allow-Origin: https://localhost:7227`.

**6. `POST /api/orders` con origen permitido.**

```
HTTP/1.1 201 Created
Access-Control-Allow-Origin: http://localhost:5025
Access-Control-Expose-Headers: Location,Retry-After
Location: http://localhost:5189/orders/695f5729-71a6-45dd-a0ef-6504444ca76d
Vary: Origin
{"id":"695f5729-...","status":"Pending","total":249.00,...}
```

`Location` ya es legible desde el navegador — y sigue apuntando al backend sin prefijo, que es la deuda de `5.1` (decisión 4).

**7. El preflight no gasta cupo, y el `429` conserva las cabeceras CORS.** Con `RateLimiting__OrdersWrite__PermitLimit=1`:

```
OPTIONS x3                    -> 204, 204, 204
POST #1                       -> 201        (los tres preflight no gastaron permiso)
POST #2                       -> 429
    Access-Control-Allow-Origin: http://localhost:5025
    Access-Control-Expose-Headers: Location,Retry-After
    Retry-After: 60
    Content-Type: application/problem+json
    {"title":"Demasiadas peticiones","status":429,...,"traceId":"00-e9d0f109..."}
```

**8. Invertir el orden del pipeline (rotura deliberada).** Con `app.UseRateLimiter()` antes de `app.UseCors()` y el mismo cupo de 1:

```
OPTIONS                       -> 204
POST #1                       -> 429   <-- el preflight se comio el unico permiso
POST #2                       -> 429 SIN Access-Control-Allow-Origin
```

Las dos consecuencias de la decisión 6, y la segunda es peor de lo que se había predicho: un navegador no crea **ni un** pedido, mientras que un `curl` con el mismo cupo lo crea. Restaurado el orden después.

**9. Una ruta sin `CorsPolicy` (rotura deliberada).** Renombrando la clave en `catalog-route`:

```
preflight en /api/catalog/products  -> 405 Method Not Allowed (Allow: GET, POST)
GET con Origin permitido            -> 200 con CERO cabeceras Access-Control
la ruta de orders, que si la declara -> 204
```

Confirma la decisión 1 (YARP no engancha el preflight) y la 2 (el fallo es ruidoso, no silencioso). Restaurada la clave después.

**10. Las dos guardas, rotas a propósito.**

```
Cors__AllowedOrigins__0=http://localhost:5025/   (barra final)
  -> System.InvalidOperationException: El origen 'http://localhost:5025/' de 'Cors:AllowedOrigins'
     no es válido: ... y NO puede acabar en '/'. ...

seccion "Cors" renombrada
  -> System.InvalidOperationException: Falta la configuración 'Cors:AllowedOrigins' o está vacía. ...
```

**11. Nombre de política inexistente** (`ReverseProxy__Routes__catalog-route__CorsPolicy=no-existe`):

```
System.InvalidOperationException: Unable to load or apply the proxy configuration.
 ---> System.AggregateException: The proxy config is invalid.
      (CORS policy 'no-existe' not found for route 'catalog-route'.)
```

**12. La suite de arquitectura sigue verde y en 17.**

```
dotnet tests\Shop133.ArchitectureTests\bin\Debug\net10.0\Shop133.ArchitectureTests.dll
=== TEST EXECUTION SUMMARY ===
   Shop133.ArchitectureTests  Total: 17, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.222s
```

**13. Verificación final con la configuración limpia**, sin variables de entorno: preflight de catalog `204`, preflight de orders `204`, `GET /api/catalog/categories` con el segundo origen `200` + las dos cabeceras.

---

## Pendiente

- **`5.4`** — el smoke de enrutado es el sitio natural para automatizar lo de arriba: preflight `204` en las dos rutas, ausencia de cabeceras con un origen ajeno y el `429` **con** cabeceras CORS. Hoy **nada vigila que las dos rutas declaren `CorsPolicy`** (decisión 8), y nada vigila tampoco que el cupo global siga por encima de los otros dos, que `5.2` dejó anotado con el mismo estado.
- **`8.1`** — releer `AllowCredentials()` y la lista de orígenes cuando entre el JWT. Un token en `Authorization` no obliga a cambiar nada; una cookie de sesión sí, y entonces `AllowAnyOrigin` deja de ser siquiera legal.
- **`6.3`/`6.5`** — los orígenes de `Cors:AllowedOrigins` son los de `launchSettings.json` de `Shop133.Web`; si la Fase 6 cambia de puerto, esta lista se entera por el navegador y no por una build.
- **El día que el Gateway tenga contenedor** los orígenes serán otros y aparece el mismo problema que `5.2` anotó para la clave de partición: con algo delante, `Origin` sigue siendo del cliente pero la IP ya no.
- **La deuda de `1.5`/`5.1` sigue abierta**: el documento OpenAPI de Catalog llega por el Gateway declarando sus paths sin prefijo y con un `servers` que apunta al backend. CORS no la toca.
