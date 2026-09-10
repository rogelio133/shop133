# Fase 5.2 — Rate limiting básico en el Gateway

**Fecha:** 2026-09-08 · **Estado:** completado · [Roadmap](../plan-desarrollo-shop133.md)

---

## Objetivo

`5.1` puso el Gateway delante y dejó `/api/catalog/*` y `/api/orders/*` como la única superficie pública del sistema. Esa puerta no tenía **ningún** control de caudal: un bucle de `curl` contra `POST /api/orders` arranca la saga entera —cinco servicios, doce mensajes, cinco bases de datos— por cada petición, y nada lo frena.

El punto va aquí y no dentro de cada servicio porque lo dice la regla 3 de [CLAUDE.md](../CLAUDE.md): *CORS, rate limiting y (más tarde) auth se centralizan en el Gateway*. Ponerlo en los cinco servicios sería escribirlo cinco veces y dejarlo sin efecto para quien entre por la puerta, que desde la Fase 6 es todo el mundo.

Deja además el `429` que `5.4` tiene que poder provocar — está escrito en el propio título de aquel punto: *"el rate limiting de 5.2 devuelve `429` al superar el umbral"*.

**Fuera de alcance, deliberadamente:** CORS (`5.3`), el smoke de enrutado (`5.4`), la autenticación (`8.1`), contenedorizar el Gateway y cualquier límite dentro de los cinco servicios. Tampoco se toca `Shop133.Web`, que sigue siendo la plantilla MVC intacta.

**Sin paquetes nuevos.** `Microsoft.AspNetCore.RateLimiting` va en el framework compartido desde .NET 7 y `Yarp.ReverseProxy` 2.3.0 ya trae `RouteConfig.RateLimiterPolicy` — comprobado en el XML del paquete antes de escribir una línea. El Gateway se queda con **un solo `PackageReference` y cero `ProjectReference`**, que desde `5.1` es una regla ejecutable.

---

## Decisiones

### 1. Ventana fija, y su defecto se dice en voz alta

`FixedWindowRateLimiter`: N peticiones por ventana de S segundos, contador que se reinicia de golpe.

Su defecto es conocido y **no se esconde**: un cliente puede gastar su cupo entero al final de una ventana y el cupo entero otra vez al principio de la siguiente, o sea **hasta 2N peticiones en un instante** a caballo de las dos. Con 60/60 s eso son 120 peticiones en dos segundos.

Se elige igualmente por dos motivos. El primero es que se explica solo: para saber cuándo vuelve un permiso basta mirar el reloj. El segundo es `5.4`: hace el `429` **determinista** —la petición N+1 falla siempre— y eso es lo que permite escribir un test que afirme algo en vez de esperar a que la suerte acompañe.

*Descartada* la **ventana deslizante**: más justa, sin pico de frontera, pero para razonar cuándo vuelve un permiso hay que saber en qué segmento se gastó, y el punto es "rate limiting básico".

*Descartado* el **token bucket**: modela mejor el tráfico real (ráfaga permitida, goteo constante), pero el umbral depende del tiempo transcurrido entre peticiones, así que el test de `5.4` pasaría o no según lo rápido que el runner mande el bucle. Un test que depende de eso es intermitente, y este repositorio ya tiene una carrera escondida sin dueño desde `4.7`; no hacen falta dos.

### 2. Dos políticas y no una: el límite protege el COSTE, no el número de peticiones

`catalog-read` 60/60 s y `orders-write` 10/60 s.

Un `GET /api/catalog/products` lee una tabla de 50 filas. Un `POST /api/orders` escribe en `OrdersDb`, publica `OrderCreated` por el outbox y dispara la validación de precio de Catalog, la reserva de Inventory, el cobro de Payments, los dos consumers de Orders y el email de Notifications. **No cuestan lo mismo y no pueden tener el mismo cupo**, y ponerles el mismo sería tratar el rate limiting como un contador de peticiones en vez de como una defensa del trabajo que hay detrás.

*Descartada* una única política aplicada a las dos rutas. Es lo mínimo que cumple el título, pero deja el cupo de escritura donde no protege nada: 60 pedidos por minuto son 60 sagas por minuto.

La asimetría se comprueba y no se afirma: con el cupo de lectura agotado el `POST` sigue devolviendo `201` — son dos cubos distintos, ver la verificación 4.

### 3. `GlobalLimiter` además de las políticas por ruta

Las dos políticas cubren las dos rutas que hay hoy. Lo que no cubren es **la ruta que entre mañana y se olvide de declarar `RateLimiterPolicy`**: nacería sin límite, y nadie se enteraría porque no hay error ni log — funciona perfectamente, solo que sin protección.

Es exactamente la clase de fallo contra la que existe la guarda de `5.1`, así que se cierra igual: un `GlobalLimiter` per-IP con cupo holgado que se aplica a todo. Verificado rompiéndolo a propósito (verificación 6), con la disciplina que este repositorio se impuso en `3.2`/`3.4`: una regla que nunca engancha pasa en verde para siempre.

**El detalle que hay que tener delante: cuando una ruta declara política, se aplican LAS DOS** —la del endpoint y la global—, así que el cupo global tiene que quedar **por encima** de los otros dos. Si fuese el menor, sería él quien limita de verdad y las dos políticas de la decisión 2 quedarían decorativas sin que nada lo delatara. De ahí 120 frente a 60 y 10.

### 4. La partición es por IP del cliente, no un contador único

`HttpContext.Connection.RemoteIpAddress` como clave de partición.

Un contador único compartido es peor que no tener límite: el primer cliente que se pase agota la cuota **de todos**, que es una denegación de servicio regalada. Hoy el Gateway es el borde real del sistema, así que la IP de la conexión *es* la del cliente.

Queda anotado en Pendiente lo que pasa el día que algo se ponga delante: todos los clientes colapsarían en la IP del proxy y habría que mirar `X-Forwarded-For`.

### 5. Los cupos son configuración, no literales

Sección `RateLimiting` en `appsettings.json`, al lado de la tabla de enrutado y por el mismo motivo de la decisión 3 de `5.1` — más uno propio: **`5.4` necesita poder bajarlos**.

```
RateLimiting__OrdersWrite__PermitLimit=3
```

Sin eso, un test del `429` tendría que crear diez pedidos de verdad, cada uno arrancando la saga entera, para llegar al rechazo. Es el patrón que `1.6` estrenó para el connection string del contenedor.

Con ellos viene su **guarda**, con el criterio de las de `ConnectionStrings:*` desde `3.1`. Si la sección falta, `GetValue<int>` devuelve `0`, un limitador de cero permisos es perfectamente válido y **rechaza absolutamente todo con `429`**: el Gateway levantaría sin una queja y el diagnóstico empezaría buscando quién está inundando la puerta. Revienta antes de `app.Build()` nombrando la clave y el valor leído (verificación 7).

### 6. `QueueLimit = 0`: un límite que encola no es un límite

`FixedWindowRateLimiterOptions.QueueLimit` a cero, así que la petición que se pasa se rechaza en el acto en vez de esperar turno.

Encolar en la puerta convierte el límite en un **retraso invisible**: el cliente no recibe un `429` que le diga que se pasó, recibe una respuesta lenta y no sabe por qué. Además la cola es memoria del Gateway, que es justo el proceso que se está intentando proteger.

### 7. El `429` sale con forma de `ProblemDetails` y con `Retry-After`

Por defecto el rechazo sale con **el cuerpo vacío**, y el cliente no sabe ni cuánto esperar. Con `AddProblemDetails()` + `IProblemDetailsService` en `OnRejected` sale `application/problem+json` con la misma forma que producen los cinco servicios desde `2.3` — y con el `traceId` dentro, que es lo que la Fase 7 va a querer.

El `Retry-After` sale de los metadatos del lease (`MetadataName.RetryAfter`), no de una cuenta a mano: es el limitador quien sabe cuánto le queda a la ventana. Ver en *Detalles que cuestan tiempo* lo que resultó valer ese número, que no es lo que uno espera.

### 8. No se añade ninguna regla de arquitectura, y se dice por escrito

La suite se queda en **17** y el repositorio en **105**. No entra paquete, ni proyecto, ni `.csproj` tocado, ni forma estructural nueva que vigilar: es configuración y composition root de un proyecto que ya tiene su regla (`Gateway_ReferencesNoProject`, de `5.1`).

Inventar una para subir el contador sería el **filtro que nunca engancha** que `3.2` rechazó. Precedente de `3.3`, `3.5`, `4.5` y la decisión 6 de `5.1`: se dice que no se añade, en vez de callarlo.

Lo que sí vigila algo, y no es un test de este repositorio, es **YARP**: un `RateLimiterPolicy` que nombre una política no registrada **no deja arrancar el Gateway**. Ver la verificación 8 — costó una predicción equivocada.

---

## Cambios

### `src/`

| Archivo | Rol |
|---|---|
| [`src/Gateway/Shop133.Gateway/Program.cs`](../src/Gateway/Shop133.Gateway/Program.cs) | **Modificado.** `ReadQuota` (la guarda de la decisión 5), `FixedWindowFor`, `ClientPartitionKey`, `AddProblemDetails()`, el bloque `AddRateLimiter` con las dos políticas + el `GlobalLimiter` + `OnRejected`, y `app.UseRateLimiter()` **antes** de `MapReverseProxy()`. |
| [`src/Gateway/Shop133.Gateway/appsettings.json`](../src/Gateway/Shop133.Gateway/appsettings.json) | **Modificado.** Sección `RateLimiting` con los tres cupos y `"RateLimiterPolicy"` en las dos rutas, con las decisiones comentadas donde se leen. |
| [`src/Gateway/Shop133.Gateway/Shop133.Gateway.http`](../src/Gateway/Shop133.Gateway/Shop133.Gateway.http) | **Modificado.** Dos peticiones nuevas y cómo bajar el cupo para ver el `429` en dos intentos en vez de en sesenta. |

**Ni una migración, ni un contrato, ni un consumer, ni un `.csproj`, ni un paquete.** No se toca ningún servicio: `Catalog.Tests` y `Orders.Tests` **no se vuelven a correr**, y se dice en vez de correrlas por rutina.

### `tests/`

Ninguno. La verificación de abajo es a mano y **la recoge `5.4`**, que ya la tiene escrita en el roadmap — el precedente de `3.4`/`3.5` recogidos por `3.7`, y lo contrario de `4.6`, cuya deuda de tests sigue sin dueño.

### Otros

Roadmap (checkbox de `5.2` + nota "Sobre 5.2"), [`docs/README.md`](README.md) (fila del índice) y [`CLAUDE.md`](../CLAUDE.md) (narrativa de la Fase 5, tabla de estado y sección *Commands*).

---

## Detalles que cuestan tiempo

**El código de rechazo por defecto es `503`, no `429`.** `RateLimiterOptions.RejectionStatusCode` viene a `Status503ServiceUnavailable`. Hay que ponerlo a mano, y si no se hace el punto entrega un limitador que funciona perfectamente devolviendo el código equivocado — `5.4` fallaría afirmando algo cierto.

**`Retry-After` dice la ventana entera, no lo que queda de ella.** Medido: la cabecera salió `Retry-After: 60` en un rechazo que ocurrió a mitad de la ventana de 60 s. El `FixedWindowRateLimiter` de .NET reporta el periodo de reposición completo, no el tiempo restante, así que es un número **conservador**: un cliente que lo obedezca al pie de la letra espera más de lo necesario. No es un fallo y no se arregla a mano —calcularlo aquí sería duplicar el reloj del limitador—, pero conviene saberlo antes de escribir un test que afirme "esperando `Retry-After` segundos vuelve a pasar": pasará, y de sobra.

**`localhost` reparte entre `::1` y `127.0.0.1`, y aquí eso significa DOS cubos.** Este repositorio ya midió esa dualidad en `2.3` (la conexión rechazada que tardaba 4.13 s porque `localhost` resuelve a las dos). Como clave de partición muerde distinto: **un cliente que alterne parece dos clientes y gasta el doble de cupo**. La verificación usa el literal `127.0.0.1` por eso, y `5.4` tendrá que hacer lo mismo o sus umbrales no cuadrarán.

**Una política inexistente NO falla en la primera petición: impide arrancar.** Es una predicción que se escribió en un comentario, se comprobó y salió **al revés** — así que el comentario se corrigió. Poniendo `"RateLimiterPolicy": "no-existe"` el Gateway no llega a escuchar y muere con `InvalidOperationException: Unable to load or apply the proxy configuration`. Es mejor de lo previsto: YARP valida los nombres de política al cargar la configuración, así que este error se descubre arrancando y no en producción. Por eso este punto **no** añade una guarda propia que cruce los nombres de `appsettings.json` con los registrados: la guarda de `5.1` existe contra un fallo *silencioso*, y éste es de todo menos silencioso.

**`PowerShell 5.1` corrompe un archivo UTF-8 al hacerle un round-trip de `Get-Content -Raw` + `Set-Content`.** Pasó al restaurar `appsettings.json` después de la verificación 8: `Get-Content` lee sin BOM como ANSI, así que `está` volvió al disco como `estÃ¡` **y con un BOM añadido**, en un archivo que el resto del proyecto escribe sin él. No es un problema de la aplicación —la configuración seguía cargando— pero deja el diff lleno de basura. Es la tercera variante del mismo tema que este repositorio lleva anotando desde `3.4` (el JSON sin BOM para RabbitMQ) y `4.8` (`curl.exe` con JSON inline). **Para tocar un archivo del repositorio, la herramienta de edición; PowerShell solo para leerlo.**

**Parar los servicios antes de compilar**, la lección de `4.9` que `5.1` volvió a sufrir: un `.API` vivo bloquea su `.dll`, el build falla con `MSB3027` y el runner corre entonces el binario viejo en verde.

**Smart App Control no saltó ni una vez.** No entró ningún paquete nuevo, que es probablemente el motivo. La escalada documentada (reintentar → `-c Release` → `dotnet <dll>` → `-p:Deterministic=false`) sigue vigente.

---

## Verificación

Con `docker compose up -d`, Catalog.API en el 5124, Orders.API en el 5189 y el Gateway en el 5104. Todas las peticiones contra el literal `127.0.0.1` por el motivo de arriba.

### 1. Build

```
> dotnet build src/Gateway/Shop133.Gateway/Shop133.Gateway.csproj
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

Y la solución entera:

```
> dotnet build
    2 Warning(s)
    0 Error(s)
```

Los 2 warnings son los `xUnit1051` de `CreateOrderTests.cs` (líneas 248 y 282) que ya venían de la reescritura de `4.9` y que `5.1` dejó anotados. **No son de este punto**; ese archivo no se ha tocado.

### 2. Las lecturas normales siguen pasando

```
> GET /api/catalog/products -> 200
> GET /api/catalog/products -> 200
> GET /api/catalog/products -> 200
```

### 3. Se agota el cupo de lectura — el `429` que pide `5.4`

62 peticiones más en la misma ventana:

```
  200 x 57
  429 x 5
  primera peticion rechazada: la numero 61
```

**Exactamente 60 aceptadas y la 61 rechazada**, que es lo que "ventana fija determinista" significa. Y el rechazo entero:

```
> curl.exe -s -i "http://127.0.0.1:5104/api/catalog/products"
HTTP/1.1 429 Too Many Requests
Content-Type: application/problem+json
Retry-After: 60
Server: Kestrel

{"title":"Demasiadas peticiones","status":429,"detail":"Se superó el límite de
peticiones de esta ruta. La cabecera Retry-After dice cuántos segundos quedan
para que se abra la siguiente ventana.","traceId":"00-6ea96209a8c6ad21a2c31ade
14ae2725-1fa5168870013fdb-00"}
```

Código, `Content-Type`, `Retry-After` y `traceId`: las cuatro cosas de la decisión 7 en una sola respuesta.

### 4. Los dos cubos son independientes — la decisión 2, comprobada

Con el cupo de lectura agotado:

```
  POST /api/orders           -> 201
  GET  /api/catalog/products -> 429  (sigue agotado)
```

El `POST` pasa mientras el `GET` está bloqueado. Sin esto, las dos políticas serían una sola con dos nombres.

Y el cupo de escritura, agotado por su cuenta (10/60 s, uno ya gastado arriba):

```
  POST 2..10 -> 201
  POST 11    -> 429
  POST 12    -> 429
```

Diez pedidos y el once rechazado. Nueve sagas completas corrieron detrás de esos `201`.

### 5. La ventana se reabre sola

```
> Start-Sleep -Seconds 65
  GET  /api/catalog/products -> 200
  POST /api/orders           -> 201
```

Sin reiniciar nada y sin intervención: el contador se reinició al entrar la ventana siguiente.

### 6. El `GlobalLimiter`, roto a propósito

Añadiendo temporalmente una tercera ruta `TEMP-probe-route` hacia el cluster `catalog` **sin `RateLimiterPolicy`**, y arrancando con `RateLimiting__Global__PermitLimit=5` para no mandar 120 peticiones:

```
  GET /api/probe/products/1  #1 -> 200
  GET /api/probe/products/1  #2 -> 200
  GET /api/probe/products/1  #3 -> 200
  GET /api/probe/products/1  #4 -> 200
  GET /api/probe/products/1  #5 -> 200
  GET /api/probe/products/1  #6 -> 429
  GET /api/probe/products/1  #7 -> 429
```

Una ruta que no declara nada **sí** está limitada. Ruta temporal eliminada después.

### 7. La guarda de la decisión 5, vista en rojo

```
> $env:RateLimiting__Global__PermitLimit = "0"
> dotnet run --project src/Gateway/Shop133.Gateway

Unhandled exception. System.InvalidOperationException: La configuración
'RateLimiting:Global' falta o no es válida: PermitLimit y WindowSeconds tienen
que ser mayores que 0 (leídos: 0 y 60). Vive en appsettings.json junto a la
tabla de enrutado. Sin esta guarda un cupo de 0 permisos rechazaría todas las
peticiones con 429 sin decir por qué.
   at Program.<<Main>$>g__ReadQuota|0_0(...) in ...\Program.cs:line 83
```

Nombra la clave y el valor leído, y revienta antes de `app.Build()`.

### 8. La predicción que salió al revés

Poniendo `"RateLimiterPolicy": "no-existe"` en `catalog-route`, esperando que el Gateway arrancara y fallara en la primera petición:

```
  arranque -> no levanto
  GET /api/catalog/products -> 000
  InvalidOperationException: Unable to load or apply the proxy configuration.
```

**No arranca.** YARP valida los nombres de política al cargar la configuración. El comentario de `appsettings.json` que afirmaba lo contrario se corrigió con esta medición delante.

### 9. El enrutado de `5.1` sigue intacto

Después de reescribir `appsettings.json` entero (ver el gotcha de PowerShell):

```
  GET  /api/catalog/products    -> 200
  GET  /api/catalog/categories  -> 200
  POST /api/orders              -> 201
  GET  /api/inventory/anything  -> 404
```

Incluido el 404 de las rutas que a propósito no existen.

### 10. Suite de arquitectura

```
> dotnet tests\Shop133.ArchitectureTests\bin\Debug\net10.0\Shop133.ArchitectureTests.dll
   Shop133.ArchitectureTests  Total: 17, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.616s
```

17/17, sin regla nueva — decisión 8.

| # | Comprobación | Resultado |
|---|---|---|
| 1 | Build del Gateway 0 warnings, solución 0 errores | ✓ |
| 2 | Lecturas normales siguen a 200 | ✓ |
| 3 | Petición 61 → `429` + `Retry-After` + `problem+json` | ✓ |
| 4 | Los dos cubos son independientes; escritura corta en la 11 | ✓ |
| 5 | La ventana se reabre sola | ✓ |
| 6 | `GlobalLimiter` cubre una ruta sin política | ✓ (roto a propósito) |
| 7 | La guarda de `RateLimiting` vista en rojo | ✓ |
| 8 | Política inexistente → no arranca | ✓ (predicción corregida) |
| 9 | El enrutado de `5.1` intacto | ✓ |
| 10 | Arquitectura 17/17 | ✓ |

---

## Pendiente

**`X-Forwarded-For`.** La partición usa la IP de la conexión, que es correcta mientras el Gateway sea el borde. El día que haya un balanceador o un CDN delante, **todos los clientes colapsan en una sola clave** y el límite pasa de per-cliente a global sin que nada avise. `UseForwardedHeaders` es la respuesta y hay que decidir en qué proxies confiar, que no es una línea. **Sin dueño en el roadmap.**

**El límite es por proceso.** Los contadores viven en memoria del Gateway, así que con dos instancias el cupo efectivo se duplica. Un limitador distribuido (Redis) es la respuesta conocida y queda fuera: hoy el Gateway ni siquiera tiene contenedor. Se nombra, no se hace.

**El pico de frontera de la ventana fija** (decisión 1): hasta 2N peticiones a caballo de dos ventanas. Aceptado a cambio de que el límite se explique solo y el `429` sea determinista.

**El cupo global se aplica ADEMÁS del de la ruta.** Está documentado y los números lo respetan, pero nada lo vigila: bajar `Global` por debajo de `CatalogRead` dejaría las dos políticas decorativas y todo seguiría pareciendo correcto. Un candidato claro para `5.4`.

**Ni un test.** La verificación de arriba es a mano y **la recoge `5.4`**, que ya lo pide por escrito.

**Nada limita a quien alcanza un servicio saltándose el Gateway.** Los cinco `.API` siguen escuchando en sus puertos sin límite alguno. Es la misma superficie que `1.5` dejó abierta con `/scalar` y que `8.1` tendrá que cerrar de verdad; el rate limiting centralizado protege a quien entra por la puerta, no cierra las ventanas.
