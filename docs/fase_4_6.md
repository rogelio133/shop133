# Fase 4.6 — Notifications.API consume `OrderConfirmed`/`OrderCancelled`

**Fecha:** 2026-09-03 · **Estado:** completado · **Roadmap:** [punto 4.6](../plan-desarrollo-shop133.md)

---

## Objetivo

Cerrar el último eslabón de la saga por el lado del cliente: que alguien le diga
que su pedido salió bien o que no salió adelante. Hasta hoy los dos desenlaces
—`OrderConfirmed` (4.2) y `OrderCancelled` (4.3)— los consumía **solo Orders.API**,
para mover su propio `Order.Status`. El cliente no se enteraba de nada.

El punto está aquí, después de `4.5` y no antes, porque hasta `4.4` los dos
eventos no cubrían los tres desenlaces reales del sistema: el `OrderCancelled` del
camino del pago no se publicaba hasta que Inventory contestaba al `ReleaseStock`.
Notificar antes habría significado avisar de una cancelación con el stock todavía
reservado.

`Notifications.API` existía desde `0.1` como plantilla `webapi` intacta —sin un
solo archivo propio— y los `///` de los dos contratos llevaban prometiendo desde
`0.3` que este servicio los escuchaba. Es la primera línea de código que ejecuta.

Es además el **quinto servicio en conectarse al broker** y el primero que consume
un evento que otro servicio ya está consumiendo: dos suscriptores del mismo
fanout, que es exactamente lo que la decisión 2 de [4.1](fase_4_1.md) llamó
"la saga observa la coreografía". Hasta hoy eso solo se veía con la saga
duplicando el consumo de `OrderCreated` dentro del mismo servicio.

**Fuera de alcance a propósito:**

- **Envío real de correo.** El punto del roadmap dice "log o mock de email". No
  hay SMTP, ni plantillas, ni cola de reintentos.
- **Dockerfile y servicio de compose.** Solo Catalog tiene imagen (precedente de
  `1.6`); Notifications se ejecuta desde el IDE como Orders, Inventory y Payments.
- **Tests automatizados.** Decidido explícitamente — ver la decisión 6.
- **Endpoint HTTP.** El servicio no expone nada: todo lo que hace, lo hace desde
  sus dos consumers. La carpeta `Controllers/` sigue vacía.

---

## Decisiones

### 1. Notifications gana base de datos, y eso **no** contradice el `///` de los contratos

El `///` de `OrderConfirmed` afirma desde `0.3`, textualmente, que este servicio
*"no tiene base de datos propia y no puede leer OrdersDb"*, y que por eso el
`CustomerEmail` viaja dentro del evento. Aparece `NotificationsDb`, la **quinta**
base del sistema y la primera que nace después de la Fase 0.

**Esa frase sigue siendo cierta en lo que decía.** Lo que afirmaba es que
Notifications no puede *consultar el pedido en ningún sitio*, y eso no cambia:
`notifications_user` no tiene permiso sobre `OrdersDb` y nunca lo tendrá, así que
o el dato llega en el mensaje o el servicio no puede trabajar. La base nueva no le
da acceso a nada ajeno.

Entra por otro motivo, y es el mismo con el que Payments ganó la suya en
[3.5](fase_3_5.md): **sin una fila que consultar, el consumer no puede ser
idempotente de ninguna forma**, y la regla 6 de CLAUDE.md no admite excepciones.
Es la misma estructura de argumento que aquel documento tuvo que escribir —la
decisión 2 de [3.2](fase_3_2.md) había descartado una base para Payments, y `3.5`
la trajo por una razón distinta—, así que conviene decir en voz alta qué se
descartó aquí:

- **Una guarda en memoria** (un `ConcurrentDictionary` de `MessageId` como
  singleton). Cumple la regla 6 mientras el proceso viva y se pierde al reiniciar
  — justo cuando una reentrega es más probable, porque el mensaje que quedó sin
  confirmar vuelve precisamente al arrancar de nuevo. Habría sido la primera
  excepción documentada a una de las siete reglas, y a cambio de nada.
- **No poner guarda y documentar el agujero**, como se hizo con la concurrencia de
  `StockItem` en `3.4`. Aquello era un hueco *conocido y sin dueño*; esto sería
  saltarse una regla que el proyecto existe para enseñar, en el servicio más
  barato de arreglar.

El segundo motivo, que no es menor: **hace el punto verificable con un `SELECT`**.
Con solo un log, comprobar que un duplicado no mandó dos emails es contar líneas
en una consola.

### 2. Los consumers **no** se llaman como el mensaje que consumen, y es el riesgo real del punto

La convención del proyecto nombra al consumer por su mensaje: `OrderCreatedConsumer`
en Inventory, `StockReservedConsumer` en Payments. Aquí se rompe: las clases son
`OrderConfirmedNotificationConsumer` y `OrderCancelledNotificationConsumer`.

El motivo es que `SetKebabCaseEndpointNameFormatter()` deriva el nombre de la cola
del tipo menos el sufijo `Consumer`, y **Orders.API ya es dueño de `order-confirmed`
y `order-cancelled` desde [4.3](fase_4_3.md)**. Con nombres homónimos los dos
servicios no serían dos suscriptores del fanout: serían **consumidores competidores
de una sola cola**, y cada evento llegaría a uno de los dos al azar. La mitad de
los pedidos se quedaría sin mover su `Order.Status` y la otra mitad sin aviso,
**sin un solo error en ningún log**.

Es el mismo tipo de trampa que `4.4` documentó con `queue:release-stock` —un nombre
de cola que es un acuerdo entre dos servicios y que nada comprueba—, pero al revés:
allí el peligro era renombrar un consumer, aquí es *no* renombrarlo.

Se descartaron dos alternativas:

- **`.Endpoint(e => e.Name = "notifications-order-confirmed")`** en el registro.
  Funciona y agrupa mejor en la UI de RabbitMQ, pero deja dos clases homónimas en
  la solución —confuso en un log, que solo imprime el nombre corto— y hace que el
  nombre de la cola dependa de una línea de `Program.cs` en vez del formatter, que
  es de donde sale en los otros cuatro servicios.
- **Un formatter con prefijo de servicio**,
  `new KebabCaseEndpointNameFormatter("notifications", false)`. Resuelve el choque
  para siempre y para cualquier consumer futuro, pero deja a Notifications con una
  convención de nombres distinta a la de todo el resto, y el problema de fondo —dos
  clases homónimas colisionan— seguiría ahí para el siguiente servicio que lo pise.

**Lo que ningún test puede ver.** Los tests de arquitectura leen `.csproj` y rutas
de archivo; la topología de un broker no deja rastro en ninguno de los dos. Esto se
verifica contra RabbitMQ, mirando que `order-confirmed` siga teniendo **un solo**
consumidor. Es el segundo caso, después del de `3.3` con la regla 2, en el que la
respuesta honesta es "esto descansa en revisión, no en la suite".

### 3. La PK de `Notifications` es `(OrderId, Kind)`

Mismo criterio con el que la PK de `StockReservations` es el `OrderId` (`3.4`) y la
de `Payments` también (`3.5`): la única forma en que alguien va a buscar una
notificación es por su pedido, así que una identidad propia solo añadiría un índice
que mantener.

El `Kind` entra en la clave por el efecto de idempotencia: **un pedido no puede
tener dos confirmaciones**, porque la segunda no cabe en la tabla. Es idempotencia
de negocio **por clave, gratis**, exactamente como la consiguió Inventory en `3.4`
y como Payments tuvo que escribirla a mano.

Y deja pasar lo que tiene que dejar pasar: un pedido con las **dos** filas sería la
saga confirmando y cancelando el mismo pedido. Esa incoherencia es real y esta
tabla no debe taparla — quien la impide es `Order.Confirm()`/`Cancel()` en
`OrdersDb` (`4.3`), que es donde vive esa invariante.

Descartado un `Id` propio (identity) con índice único sobre el par: es la misma
restricción escrita en dos sitios, y aquí no hay ninguna FK apuntando a esta tabla
que agradezca una clave estrecha.

### 4. Dos factorías estáticas, y el texto lo redacta la entidad

`Notification` se construye con `Confirmation(...)` o `Cancellation(...)`, nunca con
un constructor público. Es el precedente literal de `Payment` en `3.5`: son las que
hacen imposible una fila con `Kind = Confirmation` y el texto de una cancelación
dentro.

Lo que aquí se añade sobre aquel precedente es que **el `Subject` y el `Body` los
compone la factoría, no el consumer**. Dejarlos como parámetros habría sido dar por
válida la única incoherencia que esta tabla puede tener: que el texto no
corresponda con el `Kind`. Con la redacción dentro, el consumer no puede
equivocarse porque no tiene dónde.

**El `Truncate()` del cuerpo es la única guarda del archivo que no lanza**, y es
deliberado: el `Reason` viene de `StockRejected`, que acumula un motivo por cada
línea que no se pudo servir (`3.4`), así que su longitud la decide el tamaño del
pedido y no nadie de este lado. Lanzar dejaría el mensaje en la cola de error y **al
cliente sin aviso alguno** por haber hecho un pedido grande — peor resultado que un
email con el motivo cortado. No se le ponen puntos suspensivos a propósito: quien
lea la fila y vea exactamente 2000 caracteres sabe que hubo recorte.

### 5. Un solo `NotificationKind.Cancellation` para los dos caminos de error

El consumer **no distingue** si el pedido cayó por falta de stock (`StockRejected`)
o por un pago rechazado (`PaymentFailed`, con su compensación de `4.4` ya
ejecutada). Es exactamente lo que dice el `///` de `OrderCancelled` desde `0.3`:
*"el consumidor no distingue por qué falló: para eso está Reason"*.

Descartado partir el enum en dos valores según el camino: obligaría a deducir el
motivo de un texto libre, que es justo lo que ese campo prohíbe. Medido, y se ve
bien en la verificación 5: los dos caminos producen la misma clase de fila con
motivos distintos dentro.

### 6. El "envío" es un log, sin `IEmailSender`

No hay una segunda implementación y no la habrá en este roadmap. Una interfaz con
un único implementador es la abstracción que `4.2` y `4.3` rechazaron dos veces
para `IOrderWriter`, y el precedente directo es Payments, que simula el cobro
dentro de su consumer sin inventar un `IPaymentGateway`. El día que haya un SMTP de
verdad, ese día aparece la interfaz con sus dos implementaciones delante.

El log va **después** del `SaveChangesAsync`, y eso sí es una decisión: es la única
parte de esto que no se puede deshacer. Al revés que en `3.3`/`3.5`, aquí no hay
agujero de doble escritura — lo de después del commit no es un `Publish` a otro
sistema, es una línea de consola. Si el proceso muere justo ahí, la fila consta y lo
único perdido es su rastro.

### 7. Sin tests, y el hueco se anota en vez de disimularse

`4.6` no trae suite. Se verifica a mano contra el compose real, como hicieron `3.4`
y `3.5` en su momento.

Lo que hace esto distinto de aquellos dos es que **allí había un punto que recogía
la deuda**: `3.7` automatizó lo que `3.4` y `3.5` habían comprobado a mano. Aquí no
lo hay — `4.7` es la máquina de estados, no Notifications —, así que el hueco no se
cierra solo. Queda escrito en *Pendiente* y en CLAUDE.md.

Se descartó añadir `tests/Services/Notifications/Notifications.Tests`: el proyecto
no está en el layout objetivo, habría sido la primera suite de servicio del proyecto
y el punto ya trae un proyecto nuevo, una base de datos nueva y una migración. La
alternativa —hacerlo aquí— sigue siendo razonable y es la primera candidata si el
servicio crece.

### 8. Sin outbox en `NotificationsDbContext`, al revés que en Orders

`4.5` puso las tres tablas de MassTransit en `OrdersDb` para cerrar el agujero entre
el `COMMIT` y el `Publish`. Aquí no hacen falta y no se ponen: **Notifications no
publica nada**. Es el final de la coreografía, el único servicio del sistema que
solo consume. Sin publicación no hay doble escritura que cerrar.

Es la primera vez que esa asimetría se puede explicar por lo que hace el servicio y
no por en qué punto del roadmap está.

### 9. El bloque `AddMassTransit` sigue sin extraerse, quinta copia

La revisión se cerró en `3.5` y se reconfirmó en `4.5`. Con la quinta copia delante
la respuesta no cambia: lo único que diverge entre servicios son los `AddConsumer`,
que es justo lo que no se puede compartir. Y esta copia **es estructuralmente
distinta de la de Orders** por la decisión 8, así que lo compartible entre las cinco
se ha reducido, no aumentado: el `Host` y el formatter, que no es lo que uno abre el
archivo a leer.

---

## Cambios

### Proyecto nuevo — `src/Services/Notifications/Notifications.Infrastructure/`

Copia estructural de `Inventory.Infrastructure`: **cero `ProjectReference`** —ni
siquiera a `Shop133.Contracts`, porque quien traduce un evento en una fila es el
consumer y el consumer vive en la API— y un solo paquete.

| Archivo | Rol |
|---|---|
| `Notifications.Infrastructure.csproj` | `net10.0` + `Microsoft.EntityFrameworkCore.SqlServer` **10.0.8** (la misma que los otros tres `.Infrastructure`, la de la herramienta global `dotnet-ef`). |
| `Entities/Notification.cs` | La entidad de negocio. Dos factorías, PK `(OrderId, Kind)`, `Truncate()` del cuerpo. |
| `Entities/NotificationKind.cs` | El enum con valores explícitos (`Confirmation = 1`, `Cancellation = 2`). |
| `Entities/ProcessedMessage.cs` | **Cuarta copia literal** de la de Inventory/Payments/Orders. |
| `Persistence/NotificationsDbContext.cs` | Los dos `DbSet`, `ApplyConfiguration` explícito. Sin las tablas del outbox. |
| `Persistence/Configurations/NotificationConfiguration.cs` | PK compuesta, `ValueGeneratedNever()`, `HasConversion<int>()` en el enum, longitudes desde las constantes. |
| `Persistence/Configurations/ProcessedMessageConfiguration.cs` | Copia de la de Inventory. |
| `Migrations/20260903182526_InitialCreate.*` | Las dos tablas. Generada, aplicada y verificada. |

### `src/Services/Notifications/Notifications.API/`

| Archivo | Cambio |
|---|---|
| `Notifications.API.csproj` | `UserSecretsId` nuevo, `MassTransit.RabbitMQ` **8.5.10**, `Microsoft.EntityFrameworkCore.Design` **10.0.8** con `PrivateAssets="all"`, `ProjectReference` a `Notifications.Infrastructure`. |
| `Program.cs` | Reescrito: dos guardas de configuración, `AddDbContext`, el bloque `AddMassTransit` con los dos consumers. Sin `Migrate()` al arrancar y sin `public partial class Program { }`. |
| `Consumers/OrderConfirmedNotificationConsumer.cs` | Nuevo. Cola `order-confirmed-notification`. |
| `Consumers/OrderCancelledNotificationConsumer.cs` | Nuevo. Cola `order-cancelled-notification`. |

### Infraestructura

| Archivo | Cambio |
|---|---|
| `db/init/01-create-databases.sql` | Bloque `Notifications` (base + login + usuario + `db_owner`) y `'NotificationsDb'` añadido al `WHERE d.name IN (...)` del resumen. |
| `docker-compose.yml` | `NOTIFICATIONS_DB_PASSWORD` en el `environment` de `db-init`; el comentario pasa de "las 4 bases" a "las 5". |
| `.env.example` | La quinta contraseña. |
| `shop133.slnx` | `Notifications.Infrastructure` dentro de `/src/Services/Notifications/`. |

**No se tocó `Shop133.Contracts`.** Los dos eventos existen desde `0.3` con el
`CustomerEmail` ya dentro — el punto no necesitó ni un campo nuevo, que es lo que
esas dos declaraciones venían prometiendo.

---

## Detalles que cuestan tiempo

**La colisión de nombres de cola no la ve nada.** Es el gotcha del punto y está en
la decisión 2. Añadido: la comprobación que la detecta no es "existen las colas" —
existirían igual— sino **cuántos consumidores tiene cada una**. Con la colisión,
`order-confirmed` habría aparecido con `consumers = 2` y ninguna cola
`order-confirmed-notification`. Mirar solo la lista de nombres no basta.

**La guarda de configuración se dispara antes que `dotnet ef`.** El primer
`migrations add` falló con *"Falta la configuración 'ConnectionStrings:NotificationsDb'"*,
seguido de un `Unable to create a 'DbContext'` que culpa al `DbContextOptions`. La
guarda funcionaba perfectamente; lo que faltaba era el `UserSecretsId` y los dos
secretos. **El orden correcto es: `UserSecretsId` en el `.csproj` → `user-secrets
set` → `migrations add`.** El segundo mensaje de error es el que se lee primero y es
el que despista.

**`db-init` no vuelve a correr con `docker compose up -d` si nada cambió.** Aquí sí
lo hizo porque el `environment` del servicio cambió (la variable nueva), y Compose
lo recreó — se ve en la salida como `db-init Recreate` → `Recreated`. Si solo se
hubiera tocado el `.sql`, que va montado como volumen, **el contenedor no se habría
recreado y la quinta base no existiría**, con el script perfectamente escrito. En
ese caso hace falta `docker compose up -d --force-recreate db-init`.

**El resumen final del script funciona por casualidad y conviene saberlo.** El
`LEFT JOIN` reconstruye el nombre del login con
`LOWER(REPLACE(d.name, 'Db', '')) + '_user'`, así que `NotificationsDb` →
`notifications_user` sale bien. Los dos `CAST` cosméticos también aguantan por poco:
`varchar(16)` para un nombre de 15 caracteres y `varchar(20)` para un login de 18.
Una base con un nombre más largo saldría cortada en el log sin que nada avise.

**Los acentos en el log redirigido son un artefacto de la consola, no de los datos.**
`Email enviado ... está confirmado` se lee como `estÃ¡` en el archivo al que se
redirigió la salida, pero la fila en SQL Server tiene el texto correcto —
comprobado con un `SELECT`. Es la diferencia entre la página de códigos de la
consola de Windows y el `nvarchar` de la columna; no hay nada que arreglar.

**`ProcessedMessages` sube y `Notifications` no, y eso es la prueba.** En el test de
la guarda de negocio (verificación 7) los dos contadores divergen a propósito: el
mensaje con `message_id` nuevo **sí** se marca como procesado —pasó por el consumer—
pero no crea fila de negocio. Si subieran los dos, o ninguno, la guarda estaría mal.
Contar solo los emails no distingue *saltado* de *reventado*, que es la trampa 3 que
`3.7` midió para los tests y que aplica igual a la verificación a mano.

**Smart App Control no saltó ni una vez**, pese a que hubo paquete recién restaurado
más assemblies nuevos —la combinación que lo disparó en `1.7`, `3.5`, `3.7` y `4.4`.
La escalada documentada sigue vigente; simplemente no hizo falta.

---

## Verificación

### 1. Compilación

```
> dotnet build src/Services/Notifications/Notifications.API/Notifications.API.csproj

  Shop133.Contracts -> ...\Shop133.Contracts.dll
  Notifications.Infrastructure -> ...\Notifications.Infrastructure.dll
  Notifications.API -> ...\Notifications.API.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### 2. La quinta base, creada por `db-init`

```
> docker compose up -d
 Container shop133-db-init Recreate
 Container shop133-db-init Recreated
 Container shop133-db-init Started
 Container shop133-db-init Exited

> docker compose logs db-init
Changed database context to 'NotificationsDb'.
database         login
---------------- --------------------
CatalogDb        catalog_user
InventoryDb      inventory_user
NotificationsDb  notifications_user
OrdersDb         orders_user
PaymentsDb       payments_user
```

### 3. El esquema, aplicado como `notifications_user`

La migración generada, con las dos claves compuestas y **sin `DEFAULT` en el
`uniqueidentifier`** — o sea, `ValueGeneratedNever()` tomó:

```csharp
OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
Kind    = table.Column<int>(type: "int", nullable: false),
...
table.PrimaryKey("PK_Notifications", x => new { x.OrderId, x.Kind });
...
table.PrimaryKey("PK_ProcessedMessages", x => new { x.MessageId, x.ConsumerName });
```

```
> dotnet ef database update --project ...Notifications.Infrastructure --startup-project ...Notifications.API
      INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
      VALUES (N'20260903182526_InitialCreate', N'10.0.8');
Done.
```

### 4. Arranque: los dos endpoints, antes de `Bus started`

```
Configured endpoint order-confirmed-notification, Consumer: Notifications.API.Consumers.OrderConfirmedNotificationConsumer
Configured endpoint order-cancelled-notification, Consumer: Notifications.API.Consumers.OrderCancelledNotificationConsumer
Now listening on: http://localhost:5043
Application started. Press Ctrl+C to shut down.
Bus started: rabbitmq://localhost/
```

### 5. **Sin colisión de colas** — la comprobación que ningún test puede hacer

```
QUEUE                                  messages   consumers
order-cancelled                        0          1
order-cancelled-notification           0          1
order-confirmed                        0          1
order-confirmed-notification           0          1
order-created                          0          1
order-state                            0          1
release-stock                          0          1
stock-reserved                         0          1
```

**Ocho colas, y `order-confirmed`/`order-cancelled` siguen con UN solo consumidor**
— el de Orders. Con la colisión habrían salido con `consumers = 2` y las dos colas
de Notifications no existirían.

Y los dos exchanges reparten a dos colas cada uno, que es la coreografía en su
forma más visible de todo el proyecto:

```
Shop133.Contracts.Events:OrderCancelled  -> order-cancelled
Shop133.Contracts.Events:OrderCancelled  -> order-cancelled-notification
Shop133.Contracts.Events:OrderConfirmed  -> order-confirmed
Shop133.Contracts.Events:OrderConfirmed  -> order-confirmed-notification
```

### 6. Camino feliz

```
POST /orders  { ana.torres@example.com, 1 x TAZA-001 @ 12.50 }
OrderId : 60189911-fef5-4793-97dd-e88d2a620c0b   Total: 12.5   Status: Pending

(3 s después)
GET /orders/60189911-... -> status = Confirmed
```

El email, en el log de Notifications:

```
Email enviado a ana.torres@example.com | Asunto: Tu pedido 60189911-... está confirmado
Hola,

Tu pedido 60189911-... se ha confirmado: hemos reservado el stock y el pago se ha completado correctamente.

Gracias por comprar en shop133.
```

Y la fila, que es lo que hace el punto comprobable:

```
OrderId                             |Kind|Recipient             |SentAt
60189911-FEF5-4793-97DD-E88D2A620C0B|1   |ana.torres@example.com|2026-09-03 18:50:08.74 +00:00
```

### 7. Camino de cancelación por pago rechazado (con su compensación)

Stock del producto 1 **antes**: `QuantityOnHand=42, QuantityReserved=11`.

```
POST /orders  { bruno.diaz@example.com, 3 x TAZA-001 @ 399.00 }
OrderId : 4cda3b1c-aefb-4101-8e81-1f399b95c0f4   Total: 1197   Status: Pending

(4 s después)
GET /orders/4cda3b1c-... -> status = Cancelled
```

Stock **después**: `QuantityOnHand=42, QuantityReserved=11` — las 3 unidades
reservadas volvieron solas (la compensación de `4.4`), y el aviso salió **después**
de que Inventory contestara.

El cuerpo del email lleva dentro el `Reason` que arrastró la saga desde Payments:

```
Hola,

Lo sentimos: tu pedido 4cda3b1c-... se ha cancelado y no se te ha cobrado nada.

Motivo: el importe 1197.00 supera el límite autorizado de 1000.00

Si crees que ha sido un error, vuelve a intentarlo desde la tienda.
```

### 8. Camino de cancelación por falta de stock — mismo `Kind`, otro motivo

```
POST /orders  { carla.ruiz@example.com, 1 x producto 999999 }
(4 s después) -> status = Cancelled
```

```
carla.ruiz@example.com -> Motivo: el producto 999999 no existe en el inventario
bruno.diaz@example.com -> Motivo: el importe 1197.00 supera el límite autorizado de 1000.00
```

Los dos caminos de error producen la misma clase de notificación con motivos
distintos, que es literalmente lo que el `///` de `OrderCancelled` prometía.

### 9. Los dos consumers en la misma tabla — la clave compuesta, estrenada el primer día

```
MessageId                           |ConsumerName                      |MessageType
60350000-DCE1-6046-0B8C-08DF09EC2C6D|OrderConfirmedNotificationConsumer|...OrderConfirmed
60350000-DCE1-6046-9369-08DF09ED434B|OrderCancelledNotificationConsumer|...OrderCancelled
60350000-DCE1-6046-AE4C-08DF09ED921A|OrderCancelledNotificationConsumer|...OrderCancelled
```

### 10. Idempotencia de **transporte** — mismo `message_id`

Republicado a mano el `OrderConfirmed` del pedido feliz con su `MessageId` original:

```
> curl -X POST .../api/exchanges/%2F/Shop133.Contracts.Events:OrderConfirmed/publish
{"routed":true}

El mensaje 60350000-dce1-6046-0b8c-08df09ec2c6d ya lo procesó
OrderConfirmedNotificationConsumer (pedido 60189911-...); se descarta.

filas de Notifications: 3   (sin cambio)
```

### 11. Idempotencia de **negocio** — `message_id` nuevo

El mismo evento con un `message_id` recién acuñado pasa por delante de la guarda
anterior sin enterarse, y lo para la PK:

```
message_id nuevo: 1166f2f5-e588-4021-9cae-9e591ec13228
{"routed":true}

El pedido 60189911-... ya tenía su aviso de confirmación; no se manda un segundo email.

notifs|processed
3     |4
```

**Los dos contadores divergen a propósito**: la notificación no se duplica, pero el
mensaje sí queda marcado como procesado. Y las dos colas siguen a 0 mensajes, sin
ninguna `_error` creada:

```
order-cancelled-notification           messages=0
order-confirmed-notification           messages=0
```

### 12. Suite de arquitectura — **16/16**, sin regla nueva

```
> dotnet tests\Shop133.ArchitectureTests\bin\Debug\net10.0\Shop133.ArchitectureTests.dll
=== TEST EXECUTION SUMMARY ===
   Shop133.ArchitectureTests  Total: 16, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.604s
```

**No se añade ninguna regla, y se dice por escrito** — precedente de `3.3` y `3.5`:
inventar un filtro que nunca casa es peor que no tenerlo. Tres reglas existentes sí
gobiernan este código y lo cubrieron sin cambios:

| Regla | Qué obligó aquí |
|---|---|
| `ConsumerFiles_LiveOnlyIn_ServiceApiConsumersFolder` | Los dos `*Consumer.cs` en `Notifications.API/Consumers/`. |
| `MassTransitPackages_StayOnMajorVersion8` | `MassTransit.RabbitMQ` con `Version="8.5.10"` explícita. |
| `EfCorePackages_LiveOnlyIn_InfrastructureProjects` | `…SqlServer` en el `.Infrastructure`; en el `.API`, solo `.Design`. |

La cuarta, `DbContextFiles_LiveOnlyIn_OwningServiceInfrastructure`, es la que
obligó a que existiera el proyecto nuevo.

### 13. Regresión — las cuatro suites siguen en 71

```
Catalog.Tests    Total: 19, Failed: 0
Orders.Tests     Total: 12, Failed: 0
Inventory.Tests  Total: 15, Failed: 0
Payments.Tests   Total:  9, Failed: 0
```

Ninguna toca Notifications, así que el número no debía moverse — y no se movió.

---

## Pendiente

- **Notifications no tiene ni un test, y no hay punto que lo recoja.** Es el hueco
  más grande que abre este punto: `4.7` es la máquina de estados, no este servicio.
  El patrón a copiar cuando se cierre es el de `Inventory.Tests`/`Payments.Tests` —
  un `ServiceCollection` alrededor del consumer con `AddMassTransitTestHarness`, sin
  `WebApplicationFactory` — y la suite sería `Category=Docker` como las otras, por
  la base de datos. El caso mínimo: mismo `MessageId` dos veces → un solo email, con
  `Assert.Empty(Published<Fault<OrderConfirmed>>())` para distinguir *saltado* de
  *reventado* (trampa 3 de `3.7`).
- **Nada comprueba que `Program.cs` registre los consumers**, igual que en Inventory
  y Payments desde `3.7`. Ese hueco es de `8.2`.
- **Nada comprueba la colisión de nombres de cola entre servicios.** No es
  automatizable con `ProjectGraph`; el sitio natural sería `8.2`, que ya tiene
  asignada la topología de exchanges contra un RabbitMQ real.
- **La tabla `Notifications` crece sin techo y nadie la purga**, exactamente como
  `ProcessedMessages` (las cuatro copias) y `OrderStates` desde `4.5`. Cuando
  aparezca la purga, aparece con su índice sobre la fecha.
- **Sin Dockerfile ni servicio de compose.** El sitio natural es `8.4`, junto con el
  `/health`, o antes si la Fase 6 necesita el servicio levantado por Compose.
- **Un fallo al notificar deja el pedido perfectamente terminado**, con el mensaje en
  `order-confirmed-notification_error` y el cliente sin enterarse. Es correcto —la
  notificación no debe poder tumbar una compra ya cobrada— pero nadie vigila esa
  cola. Mismo tipo de hueco que el `CompensatingStock` sin timeout de `4.4`.
