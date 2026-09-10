# Fase 5.4 — Smoke de enrutado del Gateway

**Fecha:** 2026-09-09 · **Estado:** completado · [Roadmap](../plan-desarrollo-shop133.md)

---

## Objetivo

`5.1`, `5.2` y `5.3` se verificaron **enteros a mano**, con `curl.exe` contra un Gateway arrancado desde el IDE y con Catalog.API y Orders.API al lado. Los tres documentos lo dicen en su sección Pendiente y los tres nombran a este punto como el que recoge la deuda:

- `5.1` — *"Ni un test del enrutado. La verificación de arriba es a mano y **la recoge `5.4`**."*
- `5.2` — *"Ni un test"*, y además: nada vigila que el cupo global siga por encima de los de ruta, *"un candidato claro para `5.4`"*.
- `5.3` — le encarga a `5.4` el preflight, la ausencia de cabeceras con un origen ajeno y el `429` **con** cabeceras CORS, y anota que *"nada vigila que las dos rutas declaren `CorsPolicy`"*.

Así que el título —*"cada ruta de 5.1 alcanza su servicio y el rate limiting de 5.2 devuelve 429 al superar el umbral"*— es el suelo y no el techo: dos puntos anteriores le pasaron invariantes por escrito.

Sale `tests/Gateway/Shop133.Gateway.Tests`, **26 tests, todos `Category=Fast`**. El repositorio pasa de 105 a **131**. Es la segunda suite `Fast` de servicio después de `OrderStateMachineTests` (`4.7`) y **la primera del proyecto que no necesita ni base de datos ni broker**: corre entera en **0,8 s** sin Docker.

**Una sola línea de `src/`**, el `public partial class Program { }` del Gateway. Ni configuración, ni pipeline, ni paquetes.

**Sin paquetes nuevos en el repositorio.** `xunit.v3` 4.0.0 y `Microsoft.AspNetCore.Mvc.Testing` 10.0.11, las dos versiones que ya estaban clavadas en `Catalog.Tests` y `Orders.Tests`. No entra `Testcontainers.MsSql` —no hay base de datos— y **no vuelve `WireMock.Net`**, ver la decisión 2.

**Fuera de alcance, deliberadamente:** contenerizar el Gateway, la cabecera `Location` de `5.1`, el documento OpenAPI de `1.5`, la autenticación (`8.1`) y los smoke E2E sobre compose (`8.6`).

---

## Decisiones

### 1. `WebApplicationFactory` para el Gateway y Kestrel de verdad para los destinos

El Gateway reenvía a direcciones HTTP reales, así que un test tiene que poner algo al otro lado. La forma que se entrega es **mixta y conviene entender por qué**: la entrada al Gateway es el `TestServer` en memoria de `WebApplicationFactory`, pero **el reenvío de YARP sale por un socket de verdad**, así que el destino no puede ser otro `TestServer` — tiene que ser un Kestrel escuchando en un puerto.

Era el riesgo estructural del punto y se resolvió con el primer test antes de escribir los otros veinticinco. Funciona, y el log lo dice literalmente:

```
Yarp.ReverseProxy.Forwarder.HttpForwarder[9]
      Proxying to http://127.0.0.1:50606/orders HTTP/2 RequestVersionOrLower
Yarp.ReverseProxy.Forwarder.HttpForwarder[56]
      Received HTTP/1.1 response 200.
```

`RequestVersionOrLower` degrada a HTTP/1.1 sobre HTTP en claro, que es exactamente lo que `5.1` ya había comprobado contra los backends reales.

*Descartado* levantar el Gateway sobre Kestrel real: costaría reescribir el arranque dentro del test para ganar una fidelidad que solo afecta a la clave de partición del rate limiter, y esa limitación queda dicha en la decisión 5.

### 2. Un stub escrito a mano, y NO resucitar `WireMock.Net`

`BackendStub` son ~40 líneas: un `WebApplication` en `http://127.0.0.1:0`, un `app.Map("/{**catch-all}")` que apunta el `Method`, el `Path`, la `QueryString` y el cuerpo, y una respuesta enlatada mutable.

*Descartado* volver a meter `WireMock.Net`, el paquete que `3.3` borró junto con la deuda síncrona de la Fase 2. Lo que hace falta aquí es apuntar qué path llegó: eso son dos líneas, y traer un framework de mocking para hacerlo devolvería al repositorio una dependencia que se quitó a propósito. El precio de escribirlo a mano —mantener 40 líneas— es menor que el de sostener esa reversión.

*Descartado* también apuntar a los servicios reales en 5124 y 5189: la suite necesitaría dos procesos arrancados a mano, más SQL Server y RabbitMQ, así que no podría ser `Fast` ni correr en CI (`8.3`). Lo que esa alternativa aportaba —comprobar que el servicio *sirve* ese path— se cubre a mano en la verificación 7 y su dueño de verdad es `8.6`.

**Se anuncia en el literal `127.0.0.1` y nunca en `localhost`.** Es la trampa que este repositorio lleva anotando desde `2.3` y que `5.2` volvió a medir en otra forma: `localhost` resuelve a `::1` y a `127.0.0.1`, que para el rate limiter son **dos claves de partición distintas**.

**Arranca de forma síncrona en su constructor**, y eso no es pereza: la fábrica del Gateway necesita la URL del stub para configurarse, así que el stub tiene que existir antes. Es el mismo orden que `2.4` descubrió con `CatalogStub`, y es lo que obliga a que las clases de test tengan constructor explícito en vez de inicializadores de campo.

### 3. `app.Map("/{**catch-all}")` y no `MapFallback`

Parece equivalente y no lo es. El patrón de `MapFallback` lleva la restricción `:nonfile`, así que un path que parezca un fichero —`/openapi/v1.json`, sin ir más lejos— **no engancharía** y el stub contestaría 404. El test fallaría culpando al Gateway de mandar la petición al sitio equivocado, cuando el equivocado sería el stub.

### 4. Cada test estrena su propia fábrica

El cupo gastado vive en el host, así que dos tests que compartieran fábrica se heredarían los `429`: el segundo pasaría —o fallaría— por lo que hizo el primero. Como xUnit construye la clase de test una vez por método, basta con que la fábrica sea un campo de instancia; es el mismo mecanismo que da una base de datos por test en Catalog y Orders (`2.4`).

**No hay `[Collection]`**, y es la primera suite de servicio sin él. Las otras cuatro lo llevan para colgar todas las clases del mismo contenedor de SQL Server; aquí no hay contenedor que compartir. Precedente `OrderStateMachineTests`, que también lleva trait y no lleva collection.

### 5. La partición POR IP no se ejerce, y se dice en voz alta

Sin socket de entrada no hay `RemoteIpAddress`, así que `ClientPartitionKey` devuelve `"unknown"` para todo el proceso: **una sola partición**. Los `429` se prueban de verdad, pero *que dos clientes distintos tengan cubos distintos* no lo comprueba nadie.

Es una limitación real y no un descuido. *Descartado* falsear la IP con una cabecera: hoy `ClientPartitionKey` lee la conexión y no `X-Forwarded-For` —`5.2` lo dejó anotado como lo que habrá que cambiar el día que algo se ponga delante del Gateway—, así que un test que inyectara la cabecera probaría un código que no existe.

### 6. Los dos invariantes de configuración van en esta suite, no en la de arquitectura

`GatewayConfigurationTests` no manda ni una petición: lee la **configuración ya cargada por el Gateway** (`factory.Services.GetRequiredService<IConfiguration>()`) con la fábrica construida sin sobreescribir nada. Así se comprueba lo que se despliega de verdad y no un fichero suelto, y de paso no hay que parsear a mano un `appsettings.json` que lleva comentarios `//`.

*Descartado* ponerlos en `Shop133.ArchitectureTests`. Esa suite lee `.csproj` y rutas de fichero **y nada más** —comprobado: cero coincidencias de `appsettings` en sus cuatro ficheros de reglas—, así que meterle un lector de JSON sería estrenar una capacidad nueva en `ProjectGraph` y desmentir lo que CLAUDE.md dice que son esas reglas. **La suite de arquitectura se queda en 17.**

Su valor no está en las dos rutas de hoy —el resto de la suite ya las ejerce— sino en **la tercera que alguien añada mañana**: enumeran las rutas que haya, así que una nueva sin política falla aquí.

### 7. No entra ninguna regla de arquitectura, y se dice por escrito

Precedente de `3.3`, `3.5`, `4.5`, `5.1`, `5.2` y `5.3`. El proyecto nuevo vive bajo `tests/` y `ProjectGraph` solo enumera `<repo>/src`, así que ni lo ve. En particular **referenciar al Gateway desde el proyecto de test no incumple `Gateway_ReferencesNoProject`**: esa regla inspecciona los `ProjectReference` *del propio Gateway*. Es el mismo motivo por el que `Catalog.Tests` arrastra EF Core sin romper nada (`1.7`).

### 8. `public partial class Program { }` y no `InternalsVisibleTo`

Las instrucciones de nivel superior generan una clase `Program` *internal*. Precedente literal en `Catalog.API` (`1.7`) y `Orders.API` (`2.3`). *Descartado* el `InternalsVisibleTo` equivalente: son más líneas y nombra al proyecto de test desde el de producción, una flecha que no debería existir.

---

## Cambios

| Archivo | Rol |
|---|---|
| `src/Gateway/Shop133.Gateway/Program.cs` | **La única línea de `src/`**: `public partial class Program { }` al pie, con su comentario. |
| `tests/Gateway/Shop133.Gateway.Tests/Shop133.Gateway.Tests.csproj` | Proyecto nuevo. `OutputType=Exe` (regla 5b), dos paquetes ya presentes en el repositorio, una sola `ProjectReference`. |
| `tests/Gateway/Shop133.Gateway.Tests/Infrastructure/BackendStub.cs` | El sustituto de Catalog.API / Orders.API: Kestrel real en `127.0.0.1:0` que apunta lo que recibe. |
| `tests/Gateway/Shop133.Gateway.Tests/Infrastructure/GatewayFactory.cs` | `WebApplicationFactory<Program>` que reescribe los dos destinos y, si el test lo pide, los cupos. |
| `tests/Gateway/Shop133.Gateway.Tests/GatewayRoutingTests.cs` | 9 tests: la primera mitad del título. |
| `tests/Gateway/Shop133.Gateway.Tests/GatewayRateLimitingTests.cs` | 5 tests: la segunda mitad del título. |
| `tests/Gateway/Shop133.Gateway.Tests/GatewayCorsTests.cs` | 9 tests (uno es una `[Theory]` de dos casos): lo que encargó `5.3`. |
| `tests/Gateway/Shop133.Gateway.Tests/GatewayConfigurationTests.cs` | 3 tests: los invariantes que `5.2` y `5.3` dejaron sin vigilar. |
| `shop133.slnx` | Carpeta `/tests/Gateway/` y el proyecto. |
| `tests/README.md` | Fila de la suite nueva **y corrección de sus totales**, ver abajo. |
| `docs/fase_5_4.md`, `docs/README.md`, `plan-desarrollo-shop133.md`, `CLAUDE.md` | La ceremonia del punto. |

**`tests/README.md` estaba desactualizado desde antes de este punto y se corrige aquí.** Declaraba **84** tests (16 arquitectura / 19 Catalog / 25 Orders) cuando el repositorio llevaba en **105** desde `4.8`, `4.9` y `5.1` (17 / 29 / 35). No lo rompió `5.4`; se arregla de paso y se dice en vez de arreglarlo en silencio.

---

## Detalles que cuestan tiempo

- **`MapFallback` no sirve para un stub de proxy** — decisión 3. Es el error que se paga con un 404 que parece del Gateway.
- **Bajo `TestServer` no hay IP de cliente.** `Connection.RemoteIpAddress` es `null` y `ClientPartitionKey` devuelve `"unknown"`: una sola partición para todo el proceso. De ahí la decisión 4 (una fábrica por test) y la limitación de la decisión 5. Escribir la suite con una fábrica compartida da tests que pasan o fallan según el orden.
- **`curl.exe -o $null` desde PowerShell NO descarta el cuerpo**: `$null` se expande a cadena vacía, curl no ve fichero de salida y escribe la respuesta en stdout. Una medición de códigos de estado acaba agrupando cuerpos JSON de 14 kB. El literal que funciona es `-o NUL`. Cuarta variante de la familia de trampas de PowerShell que este repositorio lleva anotando desde `3.4`.
- **El Gateway registra a nivel `Information` y proxea cada petición con dos líneas**, así que 26 tests dejan ~330 líneas de log. Vale el consejo de siempre: redirigir a fichero en vez de leer la cola de la consola.
- **La ventana fija hay que esperarla entera.** Al medir el cupo real a mano, el bucle que comprueba que la ventana volvió a abrirse **se gasta un permiso**, así que el primer `429` sale en la petición 60 y no en la 61. No es un fallo del cupo; es que ya habías gastado uno preguntando.
- **Smart App Control no saltó ni una vez**, pese a un proyecto nuevo con ensamblados recién compilados. La escalada documentada (reintentar → `-c Release` → `dotnet <dll>` → `-p:Deterministic=false`) sigue en pie; simplemente no hizo falta.

---

## Verificación

### 1. Build de la solución

```
Build succeeded.
    2 Warning(s)
    0 Error(s)
```

Los dos avisos son los `xUnit1051` preexistentes de `Orders.Tests` que `5.2` ya registró. **El proyecto nuevo aporta 0.**

### 2. La suite nueva

```
Shop133.Gateway.Tests  Total: 26, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.784s
```

Sin Docker, sin SQL Server y sin RabbitMQ.

### 3. Las cinco roturas deliberadas

Ningún test se dio por bueno sin verlo antes en rojo. Cada rotura se restauró después; `git diff` sobre `appsettings.json` quedó vacío al terminar.

| # | Rotura | Resultado |
|---|---|---|
| 1 | Intercambiar los dos `PathRemovePrefix` | **6 fallos**, todos de enrutado |
| 2 | Invertir `UseCors()` y `UseRateLimiter()` | **2 fallos**, exactamente los dos previstos |
| 3 | Quitar el `"CorsPolicy"` de `catalog-route` | **6 fallos**: el de configuración y cuatro de comportamiento |
| 4 | `Global:PermitLimit` de 120 a 5 | **1 fallo** |
| 5 | Comentar `RejectionStatusCode = 429` | **6 fallos** |

**La rotura 1 da el mensaje que resume el punto** — el 404 de `5.1` convertido en aserción:

```
Assert.Equal() Failure: Strings differ
            ↓ (pos 1)
Expected: "/orders"
Actual:   "/"
```

**Y la rotura 4 es la más instructiva, porque casi no rompe nada.** Con el cupo global por debajo del de ruta, **25 de los 26 tests siguen en verde**: el único que se entera es `GlobalQuota_StaysAboveEveryRouteQuota`. Eso es exactamente el hueco que `5.2` describió —*"todo seguiría pareciendo correcto"*— comprobado en lugar de afirmado.

La rotura 2 falla **solo** por `RateLimitRejection_CarriesTheCorsHeaders` y `Preflight_DoesNotSpendRateLimitQuota`, y ninguno más: el orden del pipeline no tiene ninguna otra consecuencia observable, que es justo lo que lo hace peligroso.

### 4. La suite de arquitectura no se mueve

```
Shop133.ArchitectureTests  Total: 17, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.220s
```

### 5. Las cuatro suites de servicio, para probar que la línea de `src/` no rompió nada

```
Catalog.Tests    Total: 29, Failed: 0, Time: 148.042s
Orders.Tests     Total: 35, Failed: 0, Time:  81.178s
Inventory.Tests  Total: 15, Failed: 0, Time:  97.851s
Payments.Tests   Total:  9, Failed: 0, Time:  62.816s
```

**131 tests en total**: 61 `Fast` y 70 `Docker`.

### 6. Enrutado real, a mano, con los tres procesos arriba

Lo que ningún test automático sustituye: comprobar que **los stubs no se han desviado de los servicios de verdad**. Con `docker compose up -d`, Catalog.API en 5124, Orders.API en 5189 y el Gateway en 5104:

```
/api/catalog/products            200  14267 bytes
/api/catalog/products/1          200  303 bytes
/api/catalog/categories          200  130 bytes
/api/inventory/anything          404  0 bytes
/                                404  0 bytes
```

Los mismos paths que los stubs dan por supuestos. Y el `POST`:

```
POST /api/orders -> 201
{"id":"9a7abeef-c3e6-4281-8a99-49d9b2893e98", ... "status":"Pending","total":249.00, ...}
Location: http://localhost:5189/orders/9a7abeef-c3e6-4281-8a99-49d9b2893e98
```

**La cabecera `Location` sigue apuntando al backend sin el prefijo público.** La deuda sin dueño de `5.1`, releída y **no** saldada por este punto.

### 7. CORS real

```
--- preflight ---
HTTP/1.1 204 No Content
Access-Control-Allow-Headers: content-type
Access-Control-Allow-Methods: POST
Access-Control-Allow-Origin: http://localhost:5025
Access-Control-Max-Age: 600
--- origen ajeno ---
HTTP/1.1 200 OK
```

Las mismas cabeceras que afirman los tests, `Max-Age: 600` incluido. Y el origen ajeno recibe `200` con **cero** cabeceras `Access-Control`: **CORS no es autorización**, la medición que resume `5.3`.

### 8. El cupo que se despliega de verdad

Los tests bajan siempre el umbral, así que **nadie ejercita el 60 de `appsettings.json`**. A mano, 62 peticiones seguidas:

```
200: 59
429: 3
primer 429 en la peticion #60
```

59 + el permiso que gastó el bucle de espera = **60 exactos**.

---

## Pendiente

- **El stub prueba que el Gateway *manda* `/products`, no que Catalog.API lo *sirva*.** Renombrar el `[Route("[controller]")]` de un controller dejaría esta suite en verde; solo la verificación 6, que es a mano, une las dos mitades. Ese enlace es de **`8.6`** (los smoke E2E sobre `docker compose`), que ya lo tiene asignado — al contrario que la deuda de tests de `4.6`, que sigue sin dueño.
- **La partición por IP del rate limiter no se ejerce** (decisión 5). Cuando `X-Forwarded-For` entre —el día que algo se ponga delante del Gateway— hará falta un test que hoy no se puede escribir.
- **El pico de la ventana fija** (hasta 2N peticiones a caballo de dos ventanas) sigue documentado y sin probar: afirmarlo exige controlar el reloj.
- **Sin saldar, y no es de este punto:** la cabecera `Location` del `201` (`5.1`, sin dueño) y el documento OpenAPI que declara sus paths sin prefijo con un `servers` apuntando al backend (`1.5` → `8.1`).
- **El Gateway sigue sin contenedor y sin `/health`** (`8.4`), y los cinco servicios siguen siendo alcanzables saltándose el Gateway (`8.1`).
