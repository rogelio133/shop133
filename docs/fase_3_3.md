# Fase 3.3 — Orders.API publica `OrderCreated` en lugar de llamar síncronamente

**Fecha:** 2026-08-27 · **Estado:** completado · **Roadmap:** [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md)

---

## Objetivo

**Borrar la deuda de la Fase 2 y publicar el primer mensaje del proyecto.**

`2.3` metió a propósito una llamada `HttpClient` de Orders a Catalog para que el acoplamiento síncrono doliera: con Catalog caído, `POST /orders` devolvía `502` y el pedido no se creaba. La regla 2 de [CLAUDE.md](../CLAUDE.md) dice que esa deuda se borra en la Fase 3, y `2.3` la encapsuló en una carpeta entera (`Orders.Infrastructure/Catalog/`) precisamente para que al quitarla el compilador señalara los huecos.

Este punto cierra el ciclo: el alta persiste el pedido y publica `OrderCreated`. Es también **la primera publicación real del sistema** — `3.1` midió que MassTransit declara topología de forma perezosa, así que hasta hoy el broker no tenía ni un exchange de `Shop133.Contracts`.

Y resuelve la pregunta que **cuatro documentos** aplazaron hasta aquí ([fase_0_3.md](fase_0_3.md) decisión 6, [fase_2_3.md](fase_2_3.md), [fase_3_1.md](fase_3_1.md), [fase_3_2.md](fase_3_2.md)): si nadie le pregunta a Catalog, **quién rellena `ProductSku`, `ProductName` y `UnitPrice`**.

**Fuera de alcance deliberadamente:**

- **Consumir el evento.** Nadie escucha `OrderCreated` todavía: el exchange existe, no hay ninguna cola atada y el mensaje se descarta. Inventory entra en `3.4`.
- **Afirmar en un test que se publicó.** Necesita el harness en memoria de MassTransit, que es `3.7`.
- **El outbox transaccional.** Es `4.5`; el agujero que deja no tenerlo se documenta abajo en la decisión 3.
- **La máquina de estados.** El pedido sigue naciendo `Pending` y nada lo mueve. Fase 4.

---

## Decisiones

### 1. La foto la manda el cliente — y esto revierte la decisión 4 de `2.3`

`OrderItem` y `OrderLine` exigen los cinco campos, y tres de ellos salían de Catalog. Al borrar la llamada, Orders se queda sin nadie a quien preguntar: no puede leer `CatalogDb` (regla 1) y no hay clave foránea que valga entre bases distintas.

**Elegido:** `CreateOrderItemRequest` crece con `productSku`, `productName` y `unitPrice`. `2.3` había decidido lo contrario con todas las letras —"Catalog es autoritativo sobre precios", y *descartó* explícitamente que el cuerpo declarase el precio— así que esto es una **reversión, y conviene escribirla como tal** en vez de disimularla. El motivo por el que aquella decisión era correcta entonces (existía un servicio al que preguntar) es exactamente el que dejó de valer.

> **⚠️ Matizado el 2026-08-27:** el "Descartado" de abajo llama a la read-model *la respuesta arquitectónicamente superior*. Se descartó bien, pero por el motivo equivocado — ver la decisión **2b**: una copia con retraso no da autoridad sobre el precio, y el planteamiento de "read-model o confiar en el cliente" omitía una tercera salida, que es la que se adopta en `4.8`.

**Descartado:** la otra salida que [fase_0_3.md](fase_0_3.md) dejó anotada — que Orders mantuviera una **read-model del catálogo alimentada por eventos**. Es la respuesta que parece arquitectónicamente superior: conserva la autoridad de precios, permitiría seguir devolviendo `400` ante un producto inexistente y no confía en el cliente. Se descarta por tamaño, no por mérito: exige MassTransit en `Catalog.API` (que hoy no lo tiene, `3.1` solo lo instaló en tres servicios), **tres contratos nuevos** —rompiendo los nueve mensajes que fijó la decisión 1 de `0.3`—, una migración en `OrdersDb`, un consumer, y un problema de arranque en frío en el que la read-model está vacía y **ningún pedido se puede crear hasta que Catalog republique**. Es un punto de roadmap entero, no un apartado de `3.3`.

### 2. Lo que se cede: Orders ya no valida ni precios ni existencia

> **⚠️ Revisado el 2026-08-27, el mismo día que se escribió: esta decisión afirmaba que la comprobación "se muda a Inventory", y eso solo es cierto para la mitad de existencia.** Inventory no conoce precios —`InventoryDb` guarda cantidades—, así que el importe se quedó **sin dueño en todo el roadmap**. La redacción original daba el hueco por reubicado cuando estaba abierto entero, que es peor que no anotarlo. La corrección va debajo y creó los puntos **4.8** y **4.9**.

No es un efecto secundario, es el precio de la decisión 1 y hay que tenerlo escrito.

Un cliente puede pedir el producto `999999` a `0.01` y recibe un `201`. El importe que Payments acabará cobrando en `3.5` sale de ahí, vía `OrderCreated.Total` → `StockReserved.Amount`, **sin que ningún servicio lo contraste contra el catálogo**.

**Son dos huecos distintos y solo uno tenía dueño.**

**Existencia — este sí se muda.** Quien descubre que el producto no existe es Inventory en `3.4`, que no encontrará stock reservable y publicará `StockRejected`. El pedido no se *rechaza*, se **cancela** — una validación síncrona convertida en un estado del pedido, que es literalmente lo que la coreografía cambia de sitio. El cliente se entera después, no en la respuesta.

**Importe — este no se muda a ninguna parte.** Un pedido del producto `1`, que existe y tiene stock, a `0.01`: pasa la reserva de `3.4` sin objeción —Inventory descuenta unidades, no dinero—, llega a Payments en `3.5` y **se cobra un céntimo**. Ningún punto del roadmap lo detectaba, ni siquiera tarde. No es "una validación que ahora ocurre después": es una validación que había desaparecido.

### 2b. La corrección: lo que falta validar es la autenticidad de la foto, no su igualdad

Al separar los dos huecos aparece por qué el arreglo obvio no sirve.

**Descartado: comparar `unitPrice` contra el precio actual de Catalog.** Rompe el comportamiento correcto de un e-commerce — si el precio cambia mientras el cliente está en el checkout, el pedido legítimo se rechaza. Que el precio se congele es **deseable**, no una concesión: el precio que el cliente vio es el que debe pagar. La decisión 1 sigue siendo correcta en eso; lo que sobra no es el campo, es que nadie sepa de dónde salió el número.

**Y esto degrada la alternativa que la decisión 1 descartó.** Una read-model del catálogo alimentada por eventos no devuelve la autoridad sobre el precio: devuelve una **copia con retraso**. Compararse contra ella tiene el mismo modo de fallo que compararse contra Catalog, más la ventana de lag. Su valor real es la existencia del producto y el catálogo para leer, no el importe. Se descartó por tamaño y estaba bien descartada, pero conviene dejar escrito que además **resolvía peor** el problema por el que se echaba de menos.

**Elegido: la validación del precio pasa a ser un paso de la saga, en `4.8`/`4.9`.** Catalog.API estrena MassTransit, consume `OrderCreated` y contesta `OrderPricingValidated` / `OrderPricingRejected`; la máquina de estados gana un `PricingPending` **antes** de `StockPending`, así que un rechazo cancela el pedido sin nada que compensar. Lo que lo hace la respuesta correcta aquí:

- Devuelve la autoridad del precio a su único dueño **sin llamada síncrona y sin read-model**. La decisión 1 planteó el dilema como si solo hubiera esas dos salidas, y había una tercera.
- Convierte la validación en un estado del pedido — que es exactamente lo que esta decisión afirmaba y no cumplía.
- Catalog vuelve al camino crítico, pero **asíncronamente**: con Catalog caído el `POST` sigue devolviendo `201` y el pedido espera en `PricingPending`. Eso es un retraso, no un `502`; el entregable de la Fase 3 se conserva entero.
- Cuesta dos contratos y un consumer, no un punto de roadmap entero. Rompe los nueve mensajes de la decisión 1 de [fase_0_3.md](fase_0_3.md), sí — pero `3.2` ya sentó el precedente de que un contrato se revisa cuando aparece el consumidor que lo necesita.

Su coste honesto, para no descubrirlo en la Fase 4: Catalog compara contra su precio *actual*, así que un cambio de precio a mitad del checkout produce un **rechazo falso**. Cerrarlo pide vigencia temporal de precios, que no vale lo que cuesta aquí.

**Descartado: firmar la foto (HMAC) en origen.** Catalog emite el snapshot firmado y Orders lo verifica sin llamar a nadie — cierra el hueco con cero ida y vuelta, y es el patrón *quote* de verdad. Fuera por distribución de clave compartida, semántica de caducidad y criptografía que no enseña nada sobre sagas en un proyecto que existe para enseñar sagas.

**Y queda una tercera capa, que es la que cierra la confianza de verdad.** En el sistema terminado quien llama a `POST /orders` no es un navegador, es `Shop133.Web`: si el carrito de `6.3` vive en **sesión de servidor**, es el BFF quien acuña la foto al añadir al carrito leyendo Catalog por el Gateway —que la regla 3 permite—, y el cliente deja de dictar el precio sin que Orders cambie una línea. Con la auth de `8.1` y el endpoint alcanzable solo desde el Gateway, el `curl` arbitrario desaparece. Por eso `6.3` deja de decir "sesión o cookie" en el roadmap: **con cookie el precio vuelve a manos del cliente y esta capa no cierra nada**.

### 3. `SaveChangesAsync` primero, `Publish` después — y el agujero que eso deja

**Descartado:** publicar antes de persistir. Parece más rápido y es peor: Inventory podría reservar stock de un pedido que nunca llegó a la base, y eso es **stock reservado que nadie va a liberar** — justo lo que la regla 7 existe para impedir.

**Elegido:** commit y luego publicación. El precio es el problema clásico de la **doble escritura**: si el proceso se cae entre el `COMMIT` y el `Publish`, el pedido queda `Pending` para siempre y nadie se entera, porque no hay evento que arranque la saga.

No hay forma de cerrarlo con dos sistemas y sin transacción distribuida. La solución es el **outbox transaccional** —escribir el mensaje en `OrdersDb` dentro de la misma transacción del pedido y entregarlo después—, que entra en `4.5` con `MassTransit.EntityFrameworkCore`. Hasta entonces el agujero está ahí, abierto y anotado en el propio `OrdersController`. Es de las cosas que este proyecto existe para enseñar, así que se deja visible en vez de resolverse a medias.

### 4. `IPublishEndpoint`, no `IBus`

Parece cuestión de estilo y no lo es. El outbox de `4.5` se engancha a `IPublishEndpoint` —que MassTransit registra con ámbito de petición— y **no ve nada de lo que se publique por `IBus`**, que es singleton. Elegirlo hoy es lo que evita reescribir el controller entonces.

### 5. El `Publish` no recibe el `CancellationToken` de la petición

Va con `CancellationToken.None`. Una vez commiteado el pedido, el evento **tiene** que salir: pasando el token, un navegador cerrado a destiempo dejaría un pedido persistido sin saga que lo mueva — el mismo desenlace que el agujero de la decisión 3, pero provocado por algo tan corriente como cerrar una pestaña.

### 6. Líneas repetidas con foto incoherente → `400`

Agrupar por `ProductId` sigue siendo obligatorio: el constructor de `Order` rechaza duplicados porque esas líneas viajan dentro de `ReserveStock` y un Inventory que reciba dos entradas del mismo producto tendría que adivinar. Lo que `2.3` no tenía que decidir es qué pasa si las dos entradas **discrepan** en sku, nombre o precio — antes no podían, la foto la ponía Catalog una sola vez por producto.

**Descartado:** quedarse con la primera y seguir, que es lo que sale gratis (`group.First()` ya estaba escrito). Resolvería la ambigüedad por sorteo y el cliente pagaría un importe que no eligió sin enterarse.

**Descartado:** tratarlas como dos líneas distintas. Rompe la invariante de `Order` y traslada el problema a Inventory.

**Elegido:** `400`, reutilizando la forma de error del producto desconocido que este punto se llevó — clave `Items[0].ProductId`, índice de la primera aparición. Es la ruta de error que **ocupa el hueco** que deja la decisión 2, y mantiene viva la maquinaria de `ValidationProblemDetails` que `2.3` midió.

### 7. El `[MaxLength(50)]` de `Items` se relee y se queda — con otro motivo

`2.3` dejó escrito que el tope "se puede releer en `3.3`", porque su justificación era el coste del acoplamiento: cada línea distinta costaba una ida y vuelta HTTP, así que el tamaño del cuerpo *era* el precio de la deuda. Publicar `OrderCreated` cuesta lo mismo con 1 línea que con 200, de modo que ese argumento ya no sostiene nada.

**Elegido:** mantener el 50, con otro fundamento — el cuerpo se convierte en un mensaje de RabbitMQ, y desde hoy cada línea lleva además sku y nombre, así que un pedido de 200 líneas es un payload **mayor** que antes, no menor. Se anota porque el caso interesante es este: **el número sobrevive y su razón cambia**, y un tope heredado en silencio es el que nadie sabe justificar tres fases después.

### 8. Los tests de `2.4` se borran aquí, no en `3.7`

El roadmap sitúa el borrado en `3.7`. No hubo forma de esperar: en cuanto `OrdersApiFactory` perdió su parámetro `catalogBaseUrl`, `CatalogUnavailableTests` y `CatalogStub` **dejaron de compilar**.

**Descartado:** conservar una sobrecarga del constructor para que compilaran. Los seis tests fallarían igual —no queda ningún `502` que afirmar—, así que habría que dejarlos en `Skip`: código muerto y en rojo durante toda la fase para respetar un número.

**Elegido:** borrarlos ahora, junto con `WireMock.Net` y la carpeta `Catalog/`. Es además lo que `2.4` pedía por escrito: *"el diff que lo elimina documenta el cambio de arquitectura mejor que un párrafo"*, y ese diff sale mejor entero y en un solo commit. `3.7` conserva lo suyo: los consumers de Inventory y Payments, y el test de idempotencia.

### 9. No se añade regla de arquitectura, y se explica por qué

CLAUDE.md pide considerarlo en cada punto. Se consideró hacer ejecutable la regla 2 —"ningún servicio llama a otro por HTTP"— y **no se puede** con la maquinaria de `0.6`: `ProjectGraph` lee los `.csproj`, y `HttpClient` vive en el framework compartido, así que un servicio puede volver a llamar a otro sin dejar rastro en ninguna referencia. Detectarlo exigiría analizar IL o código fuente, que es otra herramienta y otro punto.

La suite se queda en **14**.

---

## Cambios

**Ningún `.csproj` de `src/` se tocó y no se añadió ningún paquete.** `Orders.API` ya traía `MassTransit.RabbitMQ` 8.5.10 desde `3.1` y su `ProjectReference` a `Shop133.Contracts` desde `0.1`. `Shop133.Contracts` tampoco se tocó: `3.2` lo dejó revisado a propósito para que este punto no tuviera que abrirlo.

### Borrado

| Archivo | Motivo |
|---|---|
| `src/Services/Orders/Orders.Infrastructure/Catalog/CatalogClient.cs` | La deuda de la regla 2, borrada entera |
| `src/Services/Orders/Orders.Infrastructure/Catalog/CatalogProduct.cs` | Ídem |
| `src/Services/Orders/Orders.Infrastructure/Catalog/CatalogUnavailableException.cs` | Ídem |
| `tests/Services/Orders/Orders.Tests/CatalogUnavailableTests.cs` | 6 tests; decisión 8 |
| `tests/Services/Orders/Orders.Tests/Infrastructure/CatalogStub.cs` | Ídem |

### Modificado

| Archivo | Rol |
|---|---|
| [Orders.API/Program.cs](../src/Services/Orders/Orders.API/Program.cs) | Fuera el `using`, la guarda de `Services:CatalogBaseUrl` y el `AddHttpClient<CatalogClient>`. Reescrito el `Description` del documento OpenAPI. El bloque `AddMassTransit` **no se tocó**. |
| [Orders.API/Controllers/OrdersController.cs](../src/Services/Orders/Orders.API/Controllers/OrdersController.cs) | El grueso del punto: `IPublishEndpoint` en lugar de `CatalogClient`, el `Publish`, el mapeo `Order → OrderCreated`, la nueva rama de `400` y el `502` retirado. |
| [Orders.API/Models/CreateOrderItemRequest.cs](../src/Services/Orders/Orders.API/Models/CreateOrderItemRequest.cs) | Los tres campos congelados, con sus `[MaxLength]` sacados de las constantes de `OrderItem`. |
| [Orders.API/Models/CreateOrderRequest.cs](../src/Services/Orders/Orders.API/Models/CreateOrderRequest.cs) | Relectura del `[MaxLength(50)]` (decisión 7) y del `<summary>` de la clase. |
| [Orders.API/appsettings.json](../src/Services/Orders/Orders.API/appsettings.json) | Fuera la sección `Services` entera: `CatalogBaseUrl` era su única clave. |
| [Orders.API/Orders.API.http](../src/Services/Orders/Orders.API/Orders.API.http) | Sin `@CatalogApi_HostAddress`; cuerpos nuevos y bloques de comprobación del broker. |
| [Orders.Tests/Infrastructure/OrdersApiFactory.cs](../tests/Services/Orders/Orders.Tests/Infrastructure/OrdersApiFactory.cs) | De tres `UseSetting` a dos, y el comentario del de RabbitMQ reescrito: ahora **sí** es una dependencia real. |
| [Orders.Tests/CreateOrderTests.cs](../tests/Services/Orders/Orders.Tests/CreateOrderTests.cs) | De 11 tests a 10, con la tesis invertida. |
| [Orders.Tests/Orders.Tests.csproj](../tests/Services/Orders/Orders.Tests/Orders.Tests.csproj) | Fuera `WireMock.Net`. |

### Los tests, uno a uno

| Test de `2.4` | Qué pasó |
|---|---|
| `..._Returns201WithTheSnapshotCatalogDictated` | Renombrado a `...TheSnapshotTheClientSent`. **Afirma lo contrario que antes.** |
| `Create_ValidRequest_IsRetrievableByGetById` | Sobrevive, sin stub |
| `..._GroupsLinesAndQueriesCatalogOnce` | Renombrado a `...GroupsLinesSummingQuantities`; se cae la mitad que contaba peticiones |
| `Create_UnknownProduct_Returns400NamingTheLine` | **Borrado** y sustituido por su inverso (abajo) |
| `Create_SeveralUnknownProducts_ReturnsThemAllInOneProblem` | **Borrado** |
| `Create_UnknownProduct_DoesNotPersistAnything` | **Borrado** |
| `Create_MissingRequiredField_Returns400WithoutCallingCatalog` | Renombrado; el assert de "no llamó a Catalog" pasa a ser "no tocó la base" |
| `Create_InvalidEmail_Returns400` | Sobrevive |
| `Create_EmptyItems_Returns400` | Sobrevive |
| `Create_CatalogReturnsOversizedSku_Returns400AndNot500` | Renombrado a `Create_OversizedSku_Returns400`; **cambia de dueño**, ver *Detalles* |
| `GetById_UnknownId_Returns404` | Sobrevive intacto |
| **nuevo** `Create_ProductThatCatalogDoesNotKnow_Returns201Anyway` | El mismo escenario que antes daba `400`, ahora `201`. Es el punto entero en un test. |
| **nuevo** `Create_InconsistentSnapshotForSameProduct_Returns400` | La rama de la decisión 6 |

`Orders.Tests` pasa de **17 a 10**. La caída es real y es el resultado esperado: siete de los que faltan probaban una deuda que ya no existe.

---

## Detalles que cuestan tiempo

**El exchange no existe hasta que publicas, y "no hay colas" no significa nada.** `3.1` lo midió y aquí se confirma del otro lado: antes del primer `POST`, `/api/exchanges` no tenía **ni un solo** exchange fuera de los `amq.*`; después apareció `Shop133.Contracts.Events:OrderCreated`, de tipo **fanout**. Lo que ya estaba desde `3.1` era la *conexión*, visible en `/api/connections` como `Orders.API`.

**Y sigue habiendo cero colas, que es lo correcto.** Un fanout sin bindings descarta lo que recibe: en `3.3` el mensaje se publica **al vacío**. No es un fallo — la cola la crea Inventory al registrar su consumer en `3.4`. Si se quiere ver el payload antes de eso hay que atar una cola a mano (ver *Verificación*).

**`Invoke-RestMethod http://localhost:15672/api/exchanges` devuelve algo que el filtro de `Where-Object` no atraviesa.** Con el `| Where-Object { $_.name -notlike 'amq.*' }` que sugiere CLAUDE.md para las colas, la lista de exchanges sale **vacía aunque el exchange exista** — que es exactamente el resultado que hace pensar que la publicación falló. La consulta que funciona lleva el vhost en la ruta: `/api/exchanges/%2F`.

**RabbitMQ 4.x rechaza crear una cola transitoria no exclusiva.** Al montar la cola espía, el `PUT` con `{"durable":false}` responde `bad_request` con `Feature 'transient_nonexcl_queues' is deprecated`. Hay que pedirla `durable:true`.

**El `decimal` viaja como cadena JSON, y ahora está medido contra un broker de verdad.** `3.2` lo dedujo del serializador; el mensaje real dice `"total": "587.5"` y `"unitPrice": "249"`. Nótese que además **se pierden los ceros a la derecha**: se publicó `249.00` y viaja `"249"`. Entre servicios .NET el round-trip es exacto, pero cualquiera que lea la cola a ojo verá un importe con otra pinta.

**El sobre confirma dos decisiones de `0.3` que llevaban un año escritas sin comprobar.** El mensaje real trae `messageId` y `conversationId` propios y **`correlationId: null`** — la correlación por `OrderId` es la línea que la saga configura en `4.1` (decisión 5 de `0.3`), y el `messageId` del sobre es el que usará la idempotencia de `3.6`. Ninguno de los dos es un campo del contrato, como se decidió.

**`Create_OversizedSku_Returns400` cambió de dueño sin cambiar de nombre significativo.** En `2.4` el sku largo lo devolvía Catalog, así que ninguna DataAnnotation podía verlo: lo paraba el guard de la entidad y el `catch (ArgumentException)` del controller lo convertía en `400` — sin ese catch habría sido un `500`, y **eso** era lo que el test afirmaba. Ahora el valor viene en el cuerpo, lo corta el `[MaxLength]` del DTO antes de que la acción se ejecute, y la clave del error ya no es el `ParamName` de la excepción sino la ruta del modelo. El catch sigue en el controller como defensa en profundidad, pero **ya no lo ejerce ningún test**. Merece la pena saberlo antes de "limpiarlo" por parecer código muerto.

**Cada guarda que sale de un `Program.cs` saca una línea de la fábrica de tests.** `3.1` lo dejó escrito prometiendo que `3.3` debía la operación inversa, y así fue: quitar `Services:CatalogBaseUrl` obligó a quitar su `UseSetting`. Nada más que esa suite detecta el desajuste, porque `ConfigureTestServices` nunca llega a correr — las guardas lanzan antes de `app.Build()`.

**El `UseSetting` de RabbitMQ dejó de ser decorativo.** En `3.1` bastaba con que la clave existiera: nadie publicaba, y un bus sin broker se limita a avisar y reintentar, así que la suite pasaba con RabbitMQ parado. Desde hoy `POST /orders` publica de verdad, y un `Publish` sobre el transporte de RabbitMQ **espera a que haya conexión en vez de fallar rápido**: con el broker caído la petición no da error, se queda colgada. `docker compose up -d` pasa a ser prerrequisito de `Orders.Tests`.

---

## Verificación

Ejecutado el 2026-08-27. Salidas reales.

| Check | Resultado |
|---|---|
| `dotnet build shop133.slnx` | **Build succeeded. 0 Warning(s), 0 Error(s)** — los 14 proyectos |
| `Shop133.ArchitectureTests.exe` | **Total: 14, Failed: 0** — sin cambios, como preveía la decisión 9 |
| `POST /orders` con **Catalog parado** | **HTTP 201 en 420 ms** |
| Exchanges no-`amq.*` **antes** del primer POST | **ninguno** |
| Exchanges no-`amq.*` **después** | `Shop133.Contracts.Events:OrderCreated`, fanout |
| Colas | **0**, antes y después — nadie consume todavía |
| Foto incoherente (mismo producto, dos precios) | **HTTP 400**, clave `Items[0].ProductId`, con `traceId` |
| `Orders.Tests.exe` / `Catalog.Tests.exe` | **No se pudieron ejecutar** — ver el aviso al final |

**El contraste que define el punto.** Con `catalog-api` parado:

```
shop133-catalog-api     exited
--- POST /orders with Catalog DOWN ---
HTTP 201 in 420 ms
Location: http://localhost:5189/orders/dbe0562b-bf92-469c-aeea-bd5c57e86b6a
{"id":"dbe0562b-...","customerEmail":"cliente@shop133.test","status":"Pending",
 "total":587.5,"items":[{"productId":1,"productSku":"TAZA-001",...}]}
```

En `2.3` esa misma petición, en esas mismas condiciones, devolvía `502` tras ~5 s de timeout. Es el entregable de la fase en dos líneas.

**El exchange, antes y después.** El filtro que funciona lleva el vhost:

```powershell
$c = New-Object System.Management.Automation.PSCredential('guest', `
       (ConvertTo-SecureString 'guest' -AsPlainText -Force))
Invoke-RestMethod "http://localhost:15672/api/exchanges/%2F" -Credential $c |
  ForEach-Object { "{0,-45} {1}" -f $_.name, $_.type }
```

```
ANTES:  (ningún exchange fuera de amq.*; 0 colas)

DESPUÉS:
                                              direct
Shop133.Contracts.Events:OrderCreated         fanout      <-- nuevo
amq.direct                                    direct
amq.fanout                                    fanout
...
```

**El mensaje real.** Como no hay consumidor, se ató una cola espía al exchange (`durable:true`, ver *Detalles*) y se leyó por la API de gestión:

```json
{
  "messageId": "106b0000-dce1-6046-f095-08df049b498e",
  "correlationId": null,
  "conversationId": "106b0000-dce1-6046-f444-08df049b498e",
  "destinationAddress": "rabbitmq://localhost/Shop133.Contracts.Events:OrderCreated",
  "messageType": [ "urn:message:Shop133.Contracts.Events:OrderCreated" ],
  "message": {
    "orderId": "de529b53-1f76-4339-961e-eec911fea219",
    "customerEmail": "cliente@shop133.test",
    "lines": [
      { "productId": 1, "productSku": "TAZA-001", "productName": "Taza Talavera Puebla",
        "quantity": 2, "unitPrice": "249" },
      { "productId": 2, "productSku": "LLAV-001", "productName": "Llavero Alebrije Oaxaca",
        "quantity": 1, "unitPrice": "89.5" }
    ],
    "total": "587.5"
  },
  "sentTime": "2026-08-28T00:28:31.0446229Z",
  "host": { "assembly": "Orders.API", "massTransitVersion": "8.5.10.0" }
}
```

Propiedades AMQP: `content_type: application/vnd.masstransit+json`, `delivery_mode: 2`. La cola espía se borró después.

**La nueva rama de `400`**, salida literal:

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1",
 "title":"One or more validation errors occurred.","status":400,
 "errors":{"Items[0].ProductId":["El producto 1 aparece en varias líneas con sku, nombre
   o precio distintos. Las líneas repetidas se agrupan, así que las tres deben coincidir."]},
 "traceId":"00-94d19cd4c4491565296d3df483982659-a8096cc4b7c4cda7-00"}
```

### ⚠️ Las suites de componente no se pudieron ejecutar, y no es por este punto

`Orders.Tests` y `Catalog.Tests` fallan **todas** al arrancar el fixture de Testcontainers:

```
System.TypeInitializationException : The type initializer for
'DotNet.Testcontainers.Configurations.TestcontainersSettings' threw an exception.
---- System.IO.FileLoadException : Could not load file or assembly
     '...\bin\Debug\net10.0\Docker.DotNet.Handler.Abstractions.dll'.
     An Application Control policy has blocked this file. (0x800711C7)

Orders.Tests   Total: 10, Failed: 10
Catalog.Tests  Total: 19, Failed: 19
```

Es el bloqueo de **Smart App Control** que CLAUDE.md ya documenta. Dos hechos que lo sitúan:

- **`Catalog.Tests` falla igual, y `3.3` no lo tocó.** Esa es la prueba de que el problema es del entorno y no de este punto. Los 10 tests de `Orders.Tests` **se descubren correctamente** —el recuento es el esperado—; ninguno llega a ejecutar su cuerpo.
- **El remedio documentado no funcionó esta vez.** CLAUDE.md dice "vuelve a ejecutarlo"; se reintentó **doce veces a lo largo de ~10 minutos** y el bloqueo persistió. La DLL no es de una restauración reciente (fecha de creación 2026-08-20) y no está firmada, que es el perfil que SAC bloquea mientras el Intelligent Security Graph decide.

**No se desactivó Smart App Control**: es irreversible sin reinstalar Windows, y CLAUDE.md lo prohíbe explícitamente. Lo que sí se hizo fue verificar el punto **a mano y de extremo a extremo** —las siete filas de la tabla de arriba—, que cubre el camino feliz, la rama de `400` nueva, el `201` con Catalog caído y el mensaje real en el broker.

Queda pendiente relanzarlas cuando el ISG resuelva. La corrección de `dotnet test` ya estaba pendiente para antes de `8.3`; esto se suma a la misma casilla.

---

## Pendiente

- **`3.4`** — Inventory consume `OrderCreated`. Es quien creará la primera **cola** del sistema y quien atará un binding al exchange que este punto acaba de crear; hasta entonces el mensaje se publica al vacío. Allí se decide además, con el consumidor delante, si `ReserveStock`/`ReleaseStock` se quedan con `OrderLine` o estrenan un `StockLine` — la pregunta que `0.3` aplazó dos veces.
- **`3.4`, otra vez** — es quien detectará que un `ProductId` no existe, con `StockRejected`, y quien cierra **la mitad de existencia** del hueco que abrió la decisión 2. La otra mitad no le toca: Inventory no conoce precios. **Si olvida reenviar `OrderCreated.Total` a `StockReserved.Amount`, el pedido se cobra 0 y nada falla de forma visible** (`3.2`).
- **`4.8` / `4.9`** — la validación del importe, que la decisión 2b creó en el roadmap después de encontrar que no tenía dueño. Catalog.API consume `OrderCreated` y contesta `OrderPricingValidated`/`OrderPricingRejected`; la saga gana un `PricingPending` previo a `StockPending`. **`4.9` obliga a releer `4.2`**: la lista de estados que el roadmap fijó no incluye el nuevo.
- **`3.7`** — el harness en memoria de MassTransit (`MassTransit.TestFramework`, que debe ser **8.5.10**). Es lo que permitirá afirmar en un test que `OrderCreated` se publicó, y lo que quitará la dependencia del broker real que `OrdersApiFactory` acaba de estrenar. `3.3` ya adelantó el borrado de los tests de `2.4` que `3.7` tenía asignado.
- **`4.5`** — el outbox transaccional cierra el agujero de la doble escritura de la decisión 3.
- **`6.3` / `8.1`** — la tercera capa de la decisión 2b: el carrito en **sesión de servidor** hace que la foto la acuñe `Shop133.Web` y no el navegador, y la auth de `8.1` quita del mapa al llamador arbitrario. Ni una ni otra sustituyen a `4.8`: son quién puede mandar la foto, no si la foto es cierta.
- **Entorno** — relanzar `Orders.Tests` y `Catalog.Tests` cuando Smart App Control deje de bloquear `Docker.DotNet.Handler.Abstractions.dll`.
