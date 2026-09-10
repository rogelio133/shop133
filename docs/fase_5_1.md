# Fase 5.1 — Rutas del Gateway con YARP

**Fecha:** 2026-09-07 · **Estado:** completado · [Roadmap](../plan-desarrollo-shop133.md)

---

## Objetivo

Abrir la Fase 5 poniendo el Gateway delante. `Shop133.Gateway` seguía siendo la plantilla `dotnet new web` intacta desde `0.1`: seis archivos, cero `PackageReference`, cero `ProjectReference` y un `app.MapGet("/", () => "Hello World!")`. La regla 3 de [CLAUDE.md](../CLAUDE.md) dice que `Shop133.Web` nunca guarda la URL base de un servicio concreto y que la Fase 6 consumirá **solo** el Gateway — hasta hoy no había nada que consumir.

Y al ponerlo delante se cobran dos deudas que otros puntos dejaron asignadas **por escrito** a esta fase:

- `1.5` y `1.6`: *"el punto donde hay que releer esta decisión es la Fase 5, cuando el Gateway se ponga delante y decida qué expone"* — el `/scalar` y el `/openapi/v1.json` de Catalog están sin guarda desde que el contenedor arranca en Production.
- `1.6`, escrito en un comentario del propio `Catalog.API/Program.cs`: *"Desde la Fase 5 la terminación TLS es trabajo del Gateway, no de cada servicio."*

La segunda no era teórica y es la que más condiciona el punto. Ver la decisión 4.

**Fuera de alcance, deliberadamente:** el rate limiting (`5.2`), CORS (`5.3`), los tests de humo del enrutado (`5.4`), la autenticación (`8.1`) y contenedorizar el Gateway. Tampoco se toca `Shop133.Web`, que sigue siendo la plantilla MVC intacta.

---

## Decisiones

### 1. Solo DOS rutas, y la ausencia de las otras tres es la decisión del punto

El título dice "`/api/catalog/*`, `/api/orders/*`, etc. hacia **cada servicio**". Se entregan dos.

`Inventory.API`, `Payments.API` y `Notifications.API` **no tienen ni carpeta `Controllers/`**: toda su superficie son consumers de RabbitMQ. Su `Program.cs` llama a `MapControllers()` sin un solo controller que mapear, así que lo único alcanzable por HTTP es `/openapi/v1.json` en Development. Una ruta del Gateway hacia ellos solo podría devolver 404.

*Descartado* declararlas igual para "cumplir el título y dejar el mapa completo". Una ruta que solo puede fallar es exactamente el **filtro que nunca engancha** que `3.2` rechazó al añadir reglas de arquitectura: pasa desapercibida para siempre y nadie puede verificarla en `5.4`. Entrarán aquí el día que alguno gane un controller, que es el mismo criterio con el que `3.2` decidió que *un contrato se revisa cuando aparece el consumidor que lo necesita*.

Queda escrito en el propio `appsettings.json`, que es donde lo busca quien se pregunte por qué falta su servicio.

### 2. `PathRemovePrefix`, y **el transform NO es el mismo en las dos rutas** — costó un 404

Esta es la parte que no se deduce leyendo la configuración, y la descubrió un fallo real durante la verificación.

El reparto de propiedad es: **el servicio es dueño de sus paths** (`/products`, `/categories`, `/orders`) y **el Gateway es dueño del espacio `/api/<servicio>`**. `PathRemovePrefix` es lo que une las dos cosas sin que ninguna sepa de la otra.

Para Catalog funciona el prefijo entero, porque el prefijo público es un espacio de nombres que el servicio desconoce:

```
/api/catalog/products  --PathRemovePrefix: /api/catalog-->  /products
```

Para Orders **no**, y el motivo es que el último segmento del prefijo *es a la vez el recurso del servicio*:

```
/api/orders  --PathRemovePrefix: /api/orders-->  (vacío)   404
/api/orders  --PathRemovePrefix: /api-------->  /orders    201
```

El primer intento copió el transform de la ruta de arriba y el `POST /api/orders` devolvió un `404` limpio, sin una sola línea de log que dijera por qué: el path llegaba vacío a Orders.API, que no tiene ningún endpoint en la raíz. **La regla que queda: el transform no se copia de una ruta a otra, se deriva de la relación entre el prefijo público y los paths que el servicio sirve de verdad — y esa relación es distinta en cada servicio.**

*Descartado* el arreglo alternativo, reescribir los `[Route("[controller]")]` de los controllers a `/api/catalog/products`: eso mete la topología del Gateway dentro del servicio, le impide correr solo y es justo lo que la decisión de [fase_2_3.md](fase_2_3.md) dejó anotado al normalizar la `BaseAddress`.

### 3. La tabla de enrutado vive en configuración, no en código

`LoadFromConfig(...)` sobre una sección `ReverseProxy` de `appsettings.json`, no `LoadFromMemory`. Es el idioma de YARP, deja la tabla legible como datos y —lo que decide— hace cada destino **sobreescribible por variable de entorno** con el patrón que `1.6` estrenó para el connection string del contenedor:

```
ReverseProxy__Clusters__catalog__Destinations__primary__Address
```

que es exactamente como entrarán los nombres de servicio de compose el día que el Gateway tenga imagen.

**El archivo lleva comentarios, y son legales**: el proveedor de configuración JSON de ASP.NET Core parsea con `JsonCommentHandling.Skip`, así que `appsettings.json` admite `//` aunque JSON estricto no. Se usan porque las tres decisiones de arriba no se deducen de los valores. Verificado arrancando: el Gateway levanta y enruta con los comentarios puestos.

### 4. Se le quita `UseHttpsRedirection()` a Catalog.API y Orders.API — y esto **revierte** una decisión escrita de `1.6`

El comentario que `1.6` dejó en `Catalog.API/Program.cs` decía literalmente *"descartado también borrar la línea: el perfil `https` de launchSettings.json sigue existiendo y ahí la redirección sí tiene sentido"*, y cerraba anunciando que en la Fase 5 la terminación TLS pasaría al Gateway. Ya estamos en la Fase 5. **Se escribe como reversión y no se disimula**, con el precedente exacto de `3.3` revirtiendo la decisión 4 de `2.3`.

No es estético, está medido. Los siete `.csproj.user` del repositorio tienen `ActiveDebugProfile = https`, así que al arrancar desde el IDE los servicios escuchan en los dos esquemas y el middleware **sí** encuentra a dónde redirigir. Con la línea puesta, el salto HTTP del Gateway devolvía esto:

```
HTTP/1.1 307 Temporary Redirect
Location: https://localhost:7024/products
```

Dos daños, y el segundo es el grave: el enrutado roto, y **la dirección real del servicio devuelta al cliente a través del Gateway** — precisamente el fallo que la regla 3 existe para impedir. Un reverse proxy habla con su destino en claro; forzar allí el upgrade es pelearse con el proxy.

Lo que **no** se pierde: YARP manda `X-Forwarded-Proto` por defecto, así que el servicio sigue sabiendo con qué esquema entró la petición original; y Kestrel sigue escuchando en `https`, así que quien llame directo a `https://localhost:7024` no nota nada. Lo único que desaparece es el **forzado**, que solo servía a quien alcanza el servicio saltándose el Gateway.

*Descartadas* las dos alternativas:

- **Apuntar los destinos a los endpoints `https`.** No toca ningún servicio, pero TLS deja de terminar en el Gateway (se recifra hacia el backend), depende del certificado de desarrollo y contradice lo que `1.6` dejó escrito.
- **No tocar nada y exigir `--launch-profile http`.** Cero cambios de código, pero F5 en Visual Studio —que es el flujo real, con `https` activo en los siete `.csproj.user`— rompería el Gateway **en silencio**. Un acuerdo que nadie vigila y que se incumple con la tecla por defecto no es un acuerdo.

**Los otros tres servicios no se tocan**: no tienen ruta en el Gateway, así que nadie les hace un salto de proxy, y su comentario ya nombra cuándo releerlos.

### 5. Una guarda sobre `ReverseProxy:Routes`, con el criterio de las guardas de `ConnectionStrings:*`

Sin ella, una sección ausente o vacía **no falla**: `AddReverseProxy()` arranca con cero rutas, el servicio levanta con normalidad y **cada petición devuelve 404 sin una sola línea en el log**. Y ese 404 es indistinguible del que `/api/inventory/*` devuelve a propósito por la decisión 1, así que el diagnóstico empieza en el servicio de destino, a un salto de la causa. Es el fallo silencioso que este repositorio existe para evitar, y la misma razón por la que `3.1` puso la guarda de `ConnectionStrings:RabbitMq`.

### 6. Se añade `Gateway_ReferencesNoProject` — la mitad de la regla 3 que no vigilaba nadie

La suite pasa de **16 a 17**. `Frontend_DoesNotReference_ServicesOrGateway` existe desde `0.6` y vigila que `Shop133.Web` no referencie un servicio; **nada vigilaba lo simétrico en el Gateway**, que es el proyecto que de verdad está en medio.

Merece test y no solo prosa por lo mismo que lo merecía el sitio de un consumer en `3.4`: un `ProjectReference` a `Catalog.API` dejaría al Gateway compilando contra `CatalogDbContext` **sin una sola queja**, y el atajo de "leo la tabla y me ahorro el salto HTTP" quedaría a un `using` de distancia.

La regla es **cero** referencias, ni siquiera `Shop133.Contracts`, y esa exclusión es deliberada: el Gateway reenvía bytes y nunca deserializa un mensaje, así que el día que necesite los contratos lo que ha cambiado es su papel — y eso se habla antes de añadir la línea. Es lo que lo separa de los cinco servicios, que sí lo referencian todos.

**No se añade ninguna regla de paquete para YARP, y se dice por escrito** (precedente de `3.3`, `3.5` y `4.5`). `PackageRulesTests` vigila MassTransit porque la v9 es comercial y ya está publicada; YARP 2.3.0 es MIT y **no existe una rama 3.x en nuget.org**, así que la trampa no se puede dar y la regla sería un filtro que nunca engancha.

### 7. Sin contenedor, sin `MapGet("/")`, y un destino por cluster

- **Sin Dockerfile ni servicio de compose.** Solo `catalog-api` está contenedorizado; desde dentro de Docker el Gateway alcanzaría uno de sus dos destinos. Corre desde el IDE en el 5104, con los destinos sobreescribibles (decisión 3) para el día que eso cambie.
- **Se borra el `app.MapGet("/", ...)` de la plantilla.** El Gateway solo es dueño del espacio `/api/*`: un 404 en la raíz es la respuesta correcta, y la sonda de vida es `/health` en `8.4`. Con eso, `launchBrowser` pasa a `false` como en los cinco servicios — si no, F5 abriría un 404.
- **Sin `HealthCheck`, sin `LoadBalancingPolicy`, sin `SessionAffinity`.** Un destino por cluster: no hay réplicas que balancear ni sesión que fijar.

### 8. El `/openapi` y el `/scalar` de Catalog se reenvían, y el desajuste se mide en vez de esconderse

Es la relectura que `1.5` encargó. El catch-all los reenvía sin excepción explícita, y lo que aparece al medirlo está en la sección siguiente: el documento **funciona** a través del Gateway pero declara una superficie que no es la pública.

*Descartado* bloquearlos con una ruta anterior al catch-all: saldaría la deuda de `1.5` hoy, al precio de perder la referencia del API a través del Gateway justo cuando la Fase 6 va a necesitarla. *Descartado* también arreglar el documento dándole a Catalog un `servers`/base path con su prefijo público: es meter la topología del Gateway dentro del servicio, lo mismo que rechaza la decisión 2. **`1.5` queda releída y medida, no saldada**; decidir qué se expone hacia fuera es de `5.3`/`8.1`.

---

## Cambios

### `src/`

| Archivo | Rol |
|---|---|
| [`src/Gateway/Shop133.Gateway/Shop133.Gateway.csproj`](../src/Gateway/Shop133.Gateway/Shop133.Gateway.csproj) | **Modificado.** Único paquete del punto: `Yarp.ReverseProxy` 2.3.0. Sigue sin un solo `ProjectReference`, que ahora es una regla ejecutable. |
| [`src/Gateway/Shop133.Gateway/Program.cs`](../src/Gateway/Shop133.Gateway/Program.cs) | **Modificado.** De la plantilla `Hello World` a `AddReverseProxy().LoadFromConfig(...)` + `MapReverseProxy()`, con la guarda de la decisión 5. |
| [`src/Gateway/Shop133.Gateway/appsettings.json`](../src/Gateway/Shop133.Gateway/appsettings.json) | **Modificado.** La tabla de enrutado: 2 rutas, 2 clusters, y las decisiones 1, 2 y 4 comentadas donde se leen. |
| [`src/Gateway/Shop133.Gateway/Properties/launchSettings.json`](../src/Gateway/Shop133.Gateway/Properties/launchSettings.json) | **Modificado.** `launchBrowser` a `false` en los dos perfiles. |
| [`src/Gateway/Shop133.Gateway/Shop133.Gateway.http`](../src/Gateway/Shop133.Gateway/Shop133.Gateway.http) | **Nuevo.** Las peticiones de la verificación, repetibles desde el IDE. Los cinco servicios tienen el suyo. |
| [`src/Services/Catalog/Catalog.API/Program.cs`](../src/Services/Catalog/Catalog.API/Program.cs) | **Modificado.** Fuera el bloque `UseHttpsRedirection()`; en su sitio, el comentario que explica la reversión de la decisión 4. |
| [`src/Services/Orders/Orders.API/Program.cs`](../src/Services/Orders/Orders.API/Program.cs) | **Modificado.** Lo mismo. Aquí la línea venía sin guarda. |

**Ni una migración, ni un contrato, ni un consumer, ni un `.csproj` de servicio.** `Shop133.Contracts` sigue en 12 mensajes.

### `tests/`

| Archivo | Rol |
|---|---|
| [`tests/Shop133.ArchitectureTests/ServiceBoundaryRulesTests.cs`](../tests/Shop133.ArchitectureTests/ServiceBoundaryRulesTests.cs) | **Modificado.** `Gateway_ReferencesNoProject`. Suite **16 → 17**, repositorio **104 → 105**. |

No se añade ninguna suite: el enrutado se verifica a mano aquí y lo automatiza `5.4`, que es el precedente de `3.4`/`3.5` recogidos por `3.7`.

### Otros

Roadmap (checkbox de `5.1` + nota "Sobre 5.1"), [`docs/README.md`](README.md) (fila del índice) y [`CLAUDE.md`](../CLAUDE.md) (narrativa de la Fase 5, tabla de estado, recuento de tests y sección *Commands*).

---

## Detalles que cuestan tiempo

**El transform correcto de una ruta no se deduce del prefijo, se deduce de lo que sirve el servicio.** Es la decisión 2 y aquí va el síntoma: `POST /api/orders` con `PathRemovePrefix: /api/orders` devuelve un `404` **sin un solo mensaje**, porque el path llega vacío y Orders.API no tiene endpoint en la raíz. No hay nada en el log del Gateway ni en el del servicio que diga "te ha llegado la ruta vacía". Se diagnostica comparando el path público con el `[Route]` del controller, no leyendo trazas.

**Un `Location` relativo sobrevive al proxy; uno absoluto lo atraviesa y filtra la dirección real.** Los dos casos aparecieron en la misma verificación y el contraste es la lección. El `302` de Scalar manda `Location: scalar/` —relativo—, así que el navegador lo resuelve contra la URL del Gateway y acaba en `/api/catalog/scalar/`: la UI funciona entera detrás del Gateway. El `307` de `UseHttpsRedirection` mandaba `Location: https://localhost:7024/products` —absoluto—, y ahí no hay proxy que valga. **La regla: revisa toda cabecera `Location` que salga de un servicio detrás de un Gateway; la relativa está bien por construcción y la absoluta hay que mirarla una por una.**

**El documento OpenAPI filtra el backend por partida doble, y una de las dos no se ve leyendo los paths.** Servido a través del Gateway, el documento de Catalog declara sus rutas como `/products` (no `/api/catalog/products`) **y además trae un bloque `servers: [{ "url": "http://localhost:5124/" }]`** apuntando al servicio. Lo segundo es peor que lo primero: el botón de probar de Scalar leería ese `servers` y se saltaría el Gateway entero. Sale así porque YARP reescribe la cabecera `Host` hacia el destino por defecto, y `Microsoft.AspNetCore.OpenApi` construye el `servers` con lo que ve. Un `RequestHeaderOriginalHost: true` lo movería a `http://localhost:5104/`, más cerca pero **todavía sin el `/api/catalog`**: no es un arreglo, es medio arreglo.

**`appsettings.json` admite comentarios `//` y casi nadie lo sabe.** El proveedor JSON de configuración parsea con `JsonCommentHandling.Skip`. No es JSON estricto, así que algún validador de esquema los marcará, pero ASP.NET Core los ignora sin ruido — verificado arrancando el Gateway con ellos puestos.

**Los servicios corriendo bloquean el build, y el runner corre entonces el binario viejo en verde.** Es la lección de `4.9` y volvió a aplicar: hay que parar los cinco `.API` y el Gateway antes de compilar las suites. Se paran con `Get-Process -Name "Catalog.API" | Stop-Process -Force` (el nombre del proceso es el del ensamblado, sin `.exe`).

**Smart App Control no saltó ni una vez**, pese a un paquete recién descargado (`Yarp.ReverseProxy` estaba ya en la caché de NuGet, que probablemente es el motivo) más siete ensamblados reconstruidos. La escalada documentada sigue vigente; simplemente no hizo falta.

---

## Verificación

Con `docker compose up -d`, los cinco servicios arrancados con el perfil `https` —el mismo que usa F5— y el Gateway en el 5104.

### 1. La medición que justifica la decisión 4, **antes** de tocar nada

```
> curl.exe -s -i "http://localhost:5104/api/catalog/products"
HTTP/1.1 307 Temporary Redirect
Content-Length: 0
Date: Mon, 07 Sep 2026 23:12:04 GMT
Server: Kestrel
Location: https://localhost:7024/products
```

El enrutado roto y la dirección real del servicio en manos del cliente. Ésta es la prueba del punto.

### 2. La misma petición, después

```
> curl.exe -s -i "http://localhost:5104/api/catalog/products"
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8
Server: Kestrel

> total = 50
```

Los 50 productos del seed de `1.4`, a través del Gateway.

### 3. El resto de la superficie de Catalog

```
> curl.exe -s "http://localhost:5104/api/catalog/products/1"
{"id":1,"sku":"TAZA-001","name":"Taza Talavera Puebla",...,"price":249.00,"stock":42,"categoryId":1,"categoryName":"Tazas"}

> curl.exe -s "http://localhost:5104/api/catalog/categories"
[{"id":5,"name":"Libretas"},{"id":2,"name":"Llaveros"},{"id":4,"name":"Pines"},{"id":3,"name":"Playeras"},{"id":1,"name":"Tazas"}]
```

### 4. El 404 de la decisión 2, y su arreglo

Con `PathRemovePrefix: /api/orders`:

```
> curl.exe -s -i -X POST "http://localhost:5104/api/orders" ...
HTTP/1.1 404 Not Found
Content-Length: 0
Server: Kestrel
```

Con `PathRemovePrefix: /api`:

```
HTTP/1.1 201 Created
Location: http://localhost:5189/orders/4b316be6-1c19-4109-95f6-da903d80c392

{"id":"4b316be6-...","customerEmail":"cliente@example.com","status":"Pending","total":498.00,...}
```

**Y ahí está el `Location` que el Gateway no puede arreglar**: apunta a `localhost:5189` y sin el prefijo `/api/orders`. Ver Pendiente.

### 5. La saga entera detrás del Gateway — camino feliz

```
> curl.exe -s "http://localhost:5104/api/orders/4b316be6-1c19-4109-95f6-da903d80c392"
{"id":"4b316be6-...","status":"Confirmed","total":498.00,...}
```

`PricingPending → StockPending → PaymentPending → Confirmed`, cinco servicios, sin que el cliente conozca ni un puerto de ninguno.

Y una medición que salió sin buscarla, al repetir la verificación con solo Catalog, Orders y el Gateway levantados: el `POST` devuelve `201` igual y el pedido se queda en `Pending`, porque Inventory, Payments y Notifications no estaban. Al arrancarlos **sin volver a pedir nada**, el mismo id pasó solo a `Confirmed`:

```
> status=Pending        (Inventory / Payments / Notifications parados)
> ... se arrancan los tres ...
> {"id":"46c8b6de-...","status":"Confirmed","total":498.00,...}
```

Las colas son durables, así que el Gateway no cambia nada de lo que la Fase 3 ganó: un servicio caído es un **retraso**, no un `502`. Es lo mismo que `4.9` midió con `catalog-api` parado, ahora visto desde fuera de la puerta única.

### 6. La compensación detrás del Gateway — el punto entero del proyecto

Producto más caro del catálogo, leído *por el Gateway*: `id=25 sku=PLAY-005 price=399.00`. Tres unidades = `1197.00`, por encima del umbral de `3.5`.

```
> pedido a51cbd4d-030f-4a90-ada7-c86e662264e0 total=1197.00 status inicial=Pending
> status final = Cancelled
```

Y la regla 7, comprobada en `InventoryDb`:

```
> SELECT ProductId, QuantityOnHand, QuantityReserved FROM StockItems WHERE ProductId = 25;
25   61   0
```

`QuantityReserved = 0`: la compensación soltó el stock sin intervención manual, con todo el flujo entrando por la puerta única.

### 7. Las rutas que a propósito no existen

```
> curl.exe -s -o NUL -w "status=%{http_code}" "http://localhost:5104/api/inventory/anything"
status=404
> curl.exe -s -o NUL -w "status=%{http_code}" "http://localhost:5104/"
status=404
```

### 8. La relectura de `1.5`

```
> curl.exe -s -i "http://localhost:5104/api/catalog/scalar" | Select-String "^HTTP|^Location"
HTTP/1.1 302 Found
Location: scalar/

> curl.exe -s -L -o NUL -w "status=%{http_code} url=%{url_effective}" ".../api/catalog/scalar"
status=200  url=http://localhost:5104/api/catalog/scalar/
```

La UI de Scalar funciona a través del Gateway porque su redirect es **relativo**. El documento, en cambio:

```
=== info.title ===
shop133 — Catalog API
=== paths que declara el documento servido POR el Gateway ===
  /categories
  /products
  /products/{id}
=== tiene bloque servers? ===
{"url":"http://localhost:5124/"}
```

Paths sin el prefijo público y un `servers` apuntando al backend. Medido, no arreglado.

### 9. Build y suite de arquitectura

```
> dotnet build
Build succeeded.
    0 Warning(s)
    0 Error(s)

> dotnet tests\Shop133.ArchitectureTests\bin\Debug\net10.0\Shop133.ArchitectureTests.dll
   Shop133.ArchitectureTests  Total: 17, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.466s
```

Nota honesta: cuando `Orders.Tests` se recompila de verdad (no incremental) salen **2 warnings `xUnit1051`** en `CreateOrderTests.cs`, líneas 248 y 282. **No son de este punto** — `git status` confirma que ese archivo no se ha tocado; vienen de la reescritura de `4.9`. Se dejan como están, pero quedan anotados porque `CLAUDE.md` presume de builds a 0 warnings y eso ya no es cierto en una compilación limpia de esa suite.

### 10. La regla nueva, rota a propósito

Añadiendo un `ProjectReference` temporal del Gateway a `Shop133.Contracts`:

```
Shop133.ArchitectureTests.ServiceBoundaryRulesTests.Gateway_ReferencesNoProject [FAIL]
  El Gateway enruta por HTTP: no referencia ningún proyecto, ni siquiera Shop133.Contracts
  — reenvía bytes, nunca deserializa un mensaje. Referencias de más: Shop133.Contracts

   Shop133.ArchitectureTests  Total: 17, Errors: 0, Failed: 1
```

Nombra la referencia culpable. Restaurado, vuelve a 17/17.

### 11. Las dos suites afectadas por la decisión 4

```
> Catalog.Tests  Total: 29, Errors: 0, Failed: 0, Time: 143.498s
> Orders.Tests   Total: 35, Errors: 0, Failed: 0, Time:  76.048s
```

Ningún resultado cambia, que era la predicción. Y la otra mitad de la predicción, comprobada en vez de afirmada — ocurrencias de `Failed to determine the https port for redirect` en la salida de `Orders.Tests`: **0**. Antes había una por petición.

| # | Comprobación | Resultado |
|---|---|---|
| 1 | El 307 que rompía el enrutado, antes | ✓ reproducido |
| 2 | `/api/catalog/products` → 50 productos | ✓ |
| 3 | `/api/catalog/products/{id}` y `/categories` | ✓ |
| 4 | `POST /api/orders` → 201 | ✓ (tras corregir el transform) |
| 5 | Camino feliz completo → `Confirmed` | ✓ |
| 6 | Compensación → `Cancelled` y `QuantityReserved = 0` | ✓ |
| 7 | Rutas inexistentes → 404 | ✓ |
| 8 | Relectura de `1.5` medida | ✓ |
| 9 | Build 0 errores + arquitectura 17/17 | ✓ |
| 10 | Regla nueva vista en rojo | ✓ |
| 11 | `Catalog.Tests` 29/29, `Orders.Tests` 35/35, warning fuera | ✓ |

---

## Pendiente

**La cabecera `Location` del `201` de `POST /orders` apunta al servicio y sin prefijo.** Medido en la verificación 4: `http://localhost:5189/orders/{id}`. Es la misma clase de fuga que el 307 y **el Gateway no puede arreglarla solo**, porque el servicio no conoce su prefijo público. Las dos salidas son un `ResponseTransform` a medida en el Gateway (YARP no trae uno para reescribir `Location`) o decirle a Orders cuál es su prefijo, que es justo lo que la decisión 2 rechaza. **Sin dueño en el roadmap**; el punto donde duele es `6.5`, cuando un cliente siga esa cabecera.

**El `servers` del documento OpenAPI apunta al backend**, y con él el botón de probar de Scalar se salta el Gateway. Mismo problema y misma falta de dueño que el anterior; el medio arreglo conocido (`RequestHeaderOriginalHost`) está descrito arriba.

**Qué expone el Gateway hacia fuera.** La deuda de `1.5` queda **releída y medida, no saldada** (decisión 8). El Scalar único que agregue los cinco documentos y la decisión de bloquear o no `/openapi` son de `5.3` y `8.1`.

**El Gateway no tiene contenedor**, y tampoco lo tienen cuatro de los cinco servicios. Mientras siga así, los destinos son puertos del IDE. **Sin dueño en el roadmap.**

**TLS de verdad.** `1.6` delegó la terminación en el Gateway y `5.1` la acepta quitándosela a los servicios, pero hoy el Gateway escucha HTTP y HTTPS con el certificado de desarrollo. Terminar TLS en serio es cosa del despliegue.

**Ni un test del enrutado.** La verificación de arriba es a mano y **la recoge `5.4`**, que es el precedente de `3.4`/`3.5` recogidos por `3.7` — y lo contrario de `4.6`, cuya deuda de tests se quedó sin dueño y sigue así.

**`Shop133.Web` sigue sin tocarse.** La regla 3 tiene ya sus dos mitades vigiladas por tests, pero el frontend no consume el Gateway hasta la Fase 6.
