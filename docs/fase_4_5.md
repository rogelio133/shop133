# Fase 4.5 — La saga persistida y el outbox transaccional

**Fecha:** 2026-09-02 · **Estado:** completado · **Roadmap:** [4.5](../plan-desarrollo-shop133.md#fase-4--saga-completa-con-compensaciones)

---

## Objetivo

Sacar la instancia de la saga de la memoria del proceso y meterla en `OrdersDb`, y de paso cerrar la doble escritura que el proyecto lleva arrastrando —y anotando— desde `3.3`.

Los dos agujeros que este punto cierra estaban **medidos**, no supuestos:

1. **Un reinicio de Orders.API perdía todas las instancias.** La verificación 7 de [fase_4_1.md](fase_4_1.md) lo dejó escrito: con la saga esperando en `StockPending`, se reiniciaba el servicio, se reenviaba el mismo `OrderCreated` y la saga **arrancaba de cero**. Desde `4.4` la consecuencia era peor y se repartía en dos bases: un pedido en `CompensatingStock` cuando el proceso muere deja el stock **sí liberado** en `InventoryDb` y el pedido en `Pending` para siempre en `OrdersDb`.
2. **`SaveChangesAsync` y `Publish` no eran atómicos.** La decisión 3 de [fase_3_3.md](fase_3_3.md) eligió persistir primero y publicar después, con el precio dicho en voz alta: muerto el proceso entre el `COMMIT` y el `Publish`, el pedido se quedaba en `Pending` para siempre sin evento que arrancara la saga. `3.6` **agrandó** ese agujero al quitar el reenvío curativo que lo tapaba por rebote. Lo mismo pasaba dentro de la propia saga, entre el `TransitionTo` y el `Publish`.

Este punto es además donde vencían **seis preguntas aplazadas por escrito**: la tabla, el token de concurrencia optimista, si comparte `OrdersDbContext` ([fase_2_2.md](fase_2_2.md)), si la PK `uniqueidentifier` sigue siendo *clustered* ([fase_1_2.md](fase_1_2.md)), si los estados terminales pasan a `Finalize()` ([fase_4_2.md](fase_4_2.md) d5) y la comparación entre el `InboxState` de MassTransit y la tabla `ProcessedMessages` propia ([fase_3_6.md](fase_3_6.md) d2). Se responden todas, abajo.

**Fuera de alcance, deliberadamente:**

- **Los tests de la saga.** Son `4.7`, y siguen siendo el mayor hueco de la suite. `OrdersApiFactory` desmonta todo MassTransit, así que en `Orders.Tests` no hay ni saga ni outbox: lo de este punto se verificó a mano contra el compose real. Ver *Pendiente*.
- **Inventory y Payments.** El outbox entra **solo en Orders**, que es lo que la decisión 2 de [fase_3_6.md](fase_3_6.md) reservó para aquí. Sus consumers no se tocan.
- **`Shop133.Contracts`.** Ni una línea. Los diez mensajes de `4.4` siguen siendo diez.
- **La validación de precios de Catalog** (`4.8`/`4.9`) y el **plazo** del pedido atascado en `CompensatingStock`, que sigue sin dueño.

---

## Decisiones

### 1. Comparte `OrdersDbContext`, y no había alternativa razonable

La pregunta la dejó abierta la sección *Pendiente* de [fase_2_2.md](fase_2_2.md): *"habrá que decidir si comparte `OrdersDbContext` o tiene el suyo"*.

**Elegido: compartirlo**, con `r.ExistingDbContext<OrdersDbContext>()`.

**Descartado un `SagaDbContext` aparte**, que sería más limpio de leer —una clase por responsabilidad— y **anularía el punto entero**. Lo que hace valioso al outbox es que el mensaje se escribe en la MISMA transacción que el trabajo, y una transacción es de una conexión y un `SaveChangesAsync`. Con dos contextos volvería exactamente la doble escritura que este punto cierra, solo que dentro de la misma base y por tanto más difícil de ver.

El precio se dice en voz alta y está escrito en el `///` del contexto: `OrdersDbContext` pasa de dos tablas a **cinco, y solo dos son de negocio**. Las otras tres existen porque la entrega es "al menos una vez" y porque no hay transacción distribuida entre SQL Server y RabbitMQ. Es el coste de la mensajería fiable, visible en un `OnModelCreating` en vez de escondido en una librería.

### 2. El paquete va en `Orders.Infrastructure`, no en `Orders.API`

`MassTransit.EntityFrameworkCore` 8.5.10 aporta dos cosas: las extensiones `AddInboxStateEntity()`/`AddOutboxMessageEntity()`/`AddOutboxStateEntity()`, que se llaman desde `OnModelCreating`, y los tipos de configuración que usa el `AddMassTransit` de `Orders.API`.

**Descartado declararlo en `Orders.API`.** Compilaría igual —los tipos llegan por transitividad en el otro sentido— pero pondría una decisión de persistencia en la capa equivocada. El precedente exacto ya existe: `Microsoft.EntityFrameworkCore.SqlServer` se declara en `.Infrastructure` desde `2.2` y `Program.cs` llama a `UseSqlServer` sin declararlo. La regla 5 dice que EF Core es cosa del `.Infrastructure`, y el único paquete de EF que la excepciona es `.Design`, porque las herramientas `dotnet-ef` lo buscan en el *startup project*. Este no es ese caso.

Nótese que **`EfCorePackages_LiveOnlyIn_InfrastructureProjects` no habría enganchado** de todos modos: filtra el prefijo `Microsoft.EntityFrameworkCore` y este paquete empieza por `MassTransit`. O sea que la decisión la sostiene la revisión, no el test. Lo que sí lo vigila es `MassTransitPackages_StayOnMajorVersion8`, y su `///` ya nombraba a `MassTransit.EntityFrameworkCore` "en 4.5" desde `3.1`.

### 3. `OrderState` se mapea a mano, no con `SagaClassMap<T>`

El paquete trae `SagaClassMap<OrderState>`, una clase base que pone la clave y el `ValueGeneratedNever` por ti. Habrían sido tres líneas en vez de un archivo.

**Descartado por dos motivos.** El primero es el que el repositorio aplica desde `1.2`: **todo se declara a mano aunque coincida con una convención**, porque el archivo de configuración es el sitio donde se lee el esquema. Y con la clase base habría que declarar igual las longitudes de las tres columnas de texto y la `rowversion`, así que el archivo acabaría siendo mitad herencia mitad declaración — lo peor de las dos. El segundo: heredar de un tipo de MassTransit ataría el esquema de una tabla de este servicio a las convenciones de una librería.

**Lo que sí se le delega a MassTransit son las tres tablas del outbox**, y la asimetría es la decisión: `OrderState` es un tipo de este repositorio y `InboxState`/`OutboxMessage`/`OutboxState` son estructuras internas de la librería, que las lee y las escribe ella. Escribir su mapeo a mano sería fijar un esquema que no decidimos nosotros.

### 4. Concurrencia optimista, y el `UseMessageRetry` es su otra mitad

Cinco documentos venían nombrando "el token de concurrencia optimista" desde `2.2`. Se entrega: `byte[] RowVersion` en `OrderState`, `IsRowVersion()` en la configuración y `ConcurrencyMode.Optimistic` en el registro.

**Y no es el valor por defecto**: para SQL Server MassTransit usa el modo **pesimista**, que bloquea la fila con `UPDLOCK, ROWLOCK` al leerla y no necesita columna ninguna. Es menos código y funciona.

**Descartado** porque cambia un choque *detectable* por un bloqueo *invisible*, y este proyecto existe para que las carreras se vean; porque `8.2` pide expresamente "persistencia de la Saga en SQL Server con concurrencia optimista"; y porque hacía cinco documentos que estaba prometido.

Lo que hay que no olvidar es que **la columna sola no protege**. Un choque llega como `DbUpdateConcurrencyException`, y sin reintento el mensaje acaba en `order-state_error`: la protección puesta y el resultado idéntico a no tener ninguna. Por eso entra `cfg.UseMessageRetry(retry => retry.Interval(5, 100ms))`, y por eso su orden respecto al outbox importa (decisión 6).

**Descartado también un `int Version` con `[ConcurrencyCheck]`**, la otra forma que admite MassTransit: obligaría a que alguien lo incremente, y el día que un camino se olvide la protección desaparece sin avisar. Una `rowversion` no se puede olvidar porque no la escribe nadie.

### 5. El `Publish` del controller pasa a ir **antes** del `SaveChanges`

Parece que revierte la decisión 3 de [fase_3_3.md](fase_3_3.md), que eligió expresamente "persistir primero, publicar después". **No la revierte: la cumple, porque lo que cambió es qué hace esa línea.**

Aquel orden se eligió para que Inventory no pudiera reservar stock de un pedido que nunca llegó a persistirse —stock reservado que nadie va a liberar, justo lo que la regla 7 existe para impedir—. Con el `UseBusOutbox()`, ese `Publish` **ya no habla con RabbitMQ**: escribe una fila en `OutboxMessage` dentro del `ChangeTracker` del mismo `DbContext`. Así que va antes porque tiene que ir **dentro** del `SaveChanges`. El peligro que motivaba el orden viejo desaparece por construcción: si el commit no confirma, no hay pedido y tampoco hay mensaje que entregar.

Se anota en el propio archivo que **las dos cosas cambian juntas**: quitar el outbox dejando este orden devuelve el fallo que `3.3` evitaba, en su forma peor.

Aquí se cobra por fin la decisión 4 de `3.3` — inyectar `IPublishEndpoint` y no `IBus`. El outbox se engancha al primero, que es *scoped* y comparte ámbito con el `DbContext`; `IBus` es singleton y publicaría directo al broker sin ver ninguna transacción. Por eso esta línea solo hubo que **moverla**. Es la decisión aplazada que mejor ha envejecido del proyecto.

### 6. El outbox de los consumers va en un callback, y el orden con el retry es carga estructural

`UseBusOutbox()` cubre el `IPublishEndpoint` que se inyecta **fuera** de un consumer, o sea el del controller. Lo que se publica **dentro** de un consumer o de la saga necesita la otra mitad, y ahí hay dos trampas seguidas:

**La primera es que `UseEntityFrameworkOutbox` es una extensión de `IReceiveEndpointConfigurator`, no del configurador del bus.** Escrito dentro de `UsingRabbitMq` **no compila** (`CS1929`). Como aquí los endpoints los crea `ConfigureEndpoints` por convención y no se declara ninguno a mano, la vía para alcanzarlos a todos es `x.AddConfigureEndpointsCallback(...)`, que MassTransit invoca una vez por endpoint.

**La segunda es el orden dentro de ese callback: `UseMessageRetry` va primero, o sea por FUERA del outbox.** Un choque de concurrencia optimista exige reejecutar el consumer entero contra un ámbito de outbox **nuevo**. Con el orden invertido, el reintento ocurriría dentro del ámbito que ya falló, releyendo el mismo estado, y el mensaje acabaría en la cola de error igual — la protección puesta y sin protección ninguna.

Esa línea es la que hace atómicos el `Publish(OrderConfirmed)` y el `Send(ReleaseStock)` de `OrderStateMachine` con su propio cambio de estado: el agujero que el `///` de esa clase lleva anotado desde `4.2`. **Y no hizo falta tocar ni una línea de la máquina de estados para cerrarlo** — se cerró en la composición, que es exactamente donde debía cerrarse.

### 7. `Confirmed` y `Cancelled` siguen sin `Finalize()`, ahora con la tabla delante

La decisión 5 de [fase_4_2.md](fase_4_2.md) aplazó la pregunta a este punto con un argumento que ya no vale ("hoy no hay fila que borrar").

**Releída con la fila delante, la respuesta es la misma y el motivo es mejor: el desenlace de un pedido tiene que poder consultarse después.** Es lo que hace verificable esta fase —mirar `CurrentState` es cómo se comprueba que la compensación terminó, y así se hizo en las verificaciones 1 y 4 de abajo—, lo que `4.7` necesitará para afirmar el estado final, y lo que `6.5` querrá para la página de estado del pedido. Con `Finalize()` + `SetCompletedWhenFinalized()`, de un pedido cerrado no queda más rastro que `Order.Status`, que dice *qué* pasó pero no *por dónde* se pasó.

El precio, dicho en voz alta: **la tabla crece sin techo, una fila por pedido para siempre, y nadie la purga.** Es la misma renuncia consciente que `ProcessedMessages` (`3.6`), con la misma condición: el día que aparezca una purga, aparece con su índice sobre `CreatedAt`.

### 8. `InboxState` y `ProcessedMessages` conviven — y la comparación que `3.6` encargó no salió como se esperaba

La decisión 2 de [fase_3_6.md](fase_3_6.md) descartó el inbox de MassTransit porque *"resuelve la regla 6 escondiéndola"*, y prometió que el día que `4.5` trajera el outbox de verdad *"la comparación entre las dos cosas estaría escrita en el repo"*. Está, y **es más incómoda de lo que aquella nota anticipaba**.

Lo medido (verificación 6): el mismo `message_id` entregado dos veces produce **una** ejecución del consumer, una fila de `ProcessedMessages`, una de `InboxState` y ninguna cola de error. Hasta ahí, lo esperable. Lo que no se esperaba es **cuál de las dos guardas habló**: en todo el log **no aparece ni una vez** la línea de la guarda de transporte de `3.6` (*"ya lo procesó …; se descarta"*). El inbox intercepta la segunda entrega **antes** de que el `Consume` llegue a ejecutarse, así que esa guarda ya no la alcanza un duplicado real.

O sea que la tabla propia queda **parcialmente ensombrecida**, y conviene decirlo en vez de fingir que las dos siguen igual de vivas:

| | Reconoce | Se lee | ¿Sigue haciendo falta? |
|---|---|---|---|
| `InboxState` (MassTransit) | la misma **entrega** | en ninguna parte — no hay código que escribir | Sí, y ahora llega antes |
| `ProcessedMessages` (3.6) | la misma **entrega** | 30 líneas explícitas dentro del consumer | Ya no la alcanza un duplicado normal |
| Guarda de negocio (`order.Status == Confirmed`) | el mismo **pedido** | 10 líneas dentro del consumer | **Sí, insustituible** |

**No se borra `ProcessedMessages`, y no por nostalgia.** Primero, el inbox **purga sus filas** pasada una ventana de retención (se ve el `DELETE FROM InboxState … WHERE Delivered < @removeTimestamp` en el log de arranque), así que una reentrega muy tardía sí vuelve a caer en la guarda de `3.6`. Segundo, y más importante: la guarda de **negocio** —la que reconoce el mismo *pedido*, no la misma *entrega*— no la da ninguna tabla de transporte, y es la que sigue evitando que un `OrderConfirmed` reacuñado con `MessageId` nuevo llegue a `Order.Confirm()` sobre un estado final.

Y hay un tercer motivo, que es el que este proyecto valora: **tener las dos en el repo es la comparación**. Borrar la explícita dejaría el argumento en una nota.

### 9. La PK `uniqueidentifier` se queda *clustered*, por tercera vez

La pregunta viene de la sección *Pendiente* de [fase_1_2.md](fase_1_2.md) y de la decisión 6 de [fase_2_2.md](fase_2_2.md), las dos asignadas a este punto "que es cuando `OrdersDb` reciba escrituras de verdad". Ya las recibe: cada pedido escribe una fila en `OrderStates` y la actualiza entre tres y cinco veces.

**Se queda clustered igual**, y el motivo no ha cambiado: la sonda de [fase_1_1.md](fase_1_1.md) midió que SQL Server compara `uniqueidentifier` empezando por los **últimos 6 bytes**, así que el remedio popular —"usa UUID v7 y deja de fragmentar"— es falso en este motor. La alternativa real sigue siendo `IsClustered(false)` más un clustered sobre `CreatedAt`, y sigue siendo optimizar sin haber medido.

**Lo que sí cambia es que ahora la pregunta tiene dónde medirse**, y eso pasa a `8.2` con el resto de la infraestructura real. El razonamiento largo está en `OrderStateConfiguration` y `OrderConfiguration` remite a él, para no tenerlo a medias en dos archivos.

### 10. El bloque `AddMassTransit` **no se extrae**, y la pregunta se cierra sin reprogramarse

`3.1` la aplazó, `3.4` y `3.5` la releyeron y la dejaron, `3.5` dejó escrito que la próxima relectura sería **`4.5` "y esta vez con una divergencia real"**, y `4.1` la adelantó un punto con el `AddSagaStateMachine`.

Llega la divergencia prometida, y es mayor de lo anunciado: el `AddEntityFrameworkOutbox`, el `EntityFrameworkRepository` y el `AddConfigureEndpointsCallback`. **De un bloque de ~10 líneas, Inventory y Payments comparten literalmente dos** —el formatter y el `Host`—, y ninguna de las dos es la que uno abre el archivo para leer. Extraer eso a un método común dejaría fuera todo lo que distingue a este servicio, que es la definición de una mala abstracción.

**No se extrae, y esta vez no queda ningún punto al que reprogramar la pregunta.**

### 11. Dos migraciones, no una

`AddOrderStateSaga` (la tabla de la saga) y `AddTransactionalOutbox` (las tres de MassTransit), por el mismo criterio con el que `1.4` separó el esquema del seed y `3.4` hizo lo propio: **el outbox se puede revertir sin desmontar la tabla de la saga**, que es una decisión distinta y podría querer deshacerse sola. Como no hay ningún flag para conseguirlo, la receta es la de siempre: comentar las tres `Add*Entity()`, generar la primera migración, descomentar y generar la segunda.

### 12. Ninguna regla de arquitectura nueva — la suite se queda en 16

Todas las formas que introduce este punto ya están cubiertas: `MassTransitPackages_StayOnMajorVersion8` vigila el paquete nuevo (y su `///` ya lo nombraba desde `3.1`), `DbContextFiles_LiveOnlyIn_OwningServiceInfrastructure` la ubicación del contexto, `StateMachineFiles_LiveOnlyIn_OrdersDomain` la de la saga y `OrdersDomain_ProjectReferences_ContainOnlyContracts` que `Orders.Domain` no se haya llevado EF Core con el `RowVersion`.

Precedente de `3.3` y `3.5`: se dice por escrito en vez de inventar una regla. Añadir un filtro que nunca engancha para subir el contador es exactamente el fallo del que avisa `3.2`.

---

## Cambios

### `src/Services/Orders/Orders.Domain/`

| Archivo | Rol |
|---|---|
| [Sagas/OrderState.cs](../src/Services/Orders/Orders.Domain/Sagas/OrderState.cs) | `RowVersion` (el token de concurrencia) y las constantes `CurrentStateMaxLength` (64) y `CancellationReasonMaxLength` (4000). Reescrito el `///` de la clase, que decía "en 4.1 no se persiste". |
| [Sagas/OrderStateMachine.cs](../src/Services/Orders/Orders.Domain/Sagas/OrderStateMachine.cs) | **Solo comentarios: ni una línea de comportamiento.** Se releen los tres `///` que prometían este punto — el `OnMissingInstance`, el `Publish` de `OrderConfirmed` y el `Finalize()` de `Confirmed`. |

**`Orders.Domain.csproj` no se tocó**: sigue con su único `ProjectReference` a `Shop133.Contracts` y su único paquete, `MassTransit`. El `byte[]` del token no necesita EF Core, que es justo lo que permite que el mapeo viva una capa más allá.

### `src/Services/Orders/Orders.Infrastructure/`

| Archivo | Rol |
|---|---|
| [Orders.Infrastructure.csproj](../src/Services/Orders/Orders.Infrastructure/Orders.Infrastructure.csproj) | Segundo paquete del proyecto: `MassTransit.EntityFrameworkCore` 8.5.10. |
| [Persistence/Configurations/OrderStateConfiguration.cs](../src/Services/Orders/Orders.Infrastructure/Persistence/Configurations/OrderStateConfiguration.cs) | **Nuevo.** El mapeo de la instancia: PK, `ValueGeneratedNever`, `IsRowVersion`, longitudes, y el párrafo del clustered PK. |
| [Persistence/OrdersDbContext.cs](../src/Services/Orders/Orders.Infrastructure/Persistence/OrdersDbContext.cs) | `DbSet<OrderState>`, su `ApplyConfiguration` y las tres `Add*Entity()` del outbox. Reescrito el `///` que decía "el estado de la saga (4.5) no vive aquí todavía". |
| [Persistence/Configurations/OrderConfiguration.cs](../src/Services/Orders/Orders.Infrastructure/Persistence/Configurations/OrderConfiguration.cs) | Solo el comentario del clustered PK, que remitía a este punto. |
| `Migrations/20260902221840_AddOrderStateSaga.*` | **Nuevas** — generadas. La tabla `OrderStates`. |
| `Migrations/20260902221857_AddTransactionalOutbox.*` | **Nuevas** — generadas. `InboxState`, `OutboxMessage`, `OutboxState` y sus seis índices. |
| `Migrations/OrdersDbContextModelSnapshot.cs` | Regenerado. |

### `src/Services/Orders/Orders.API/`

| Archivo | Rol |
|---|---|
| [Program.cs](../src/Services/Orders/Orders.API/Program.cs) | `AddEntityFrameworkOutbox` + `UseBusOutbox`, el `AddConfigureEndpointsCallback` con retry y outbox, y el `InMemoryRepository()` sustituido por `EntityFrameworkRepository`. Cerrada por escrito la revisión del bloque `AddMassTransit`. |
| [Controllers/OrdersController.cs](../src/Services/Orders/Orders.API/Controllers/OrdersController.cs) | El `Publish` se mueve **antes** del `SaveChangesAsync`, con el bloque de comentario reescrito como reversión razonada. |

**No se tocó:** `Shop133.Contracts`, los dos consumers de `Orders.API`, `Inventory`, `Payments`, `Catalog`, ningún proyecto de `tests/`, ni `Orders.API.csproj`.

---

## Detalles que cuestan tiempo

**Un comentario XML no admite `--`, y muerde exactamente cuando documentas un flag de CLI.** Documentar `dotnet add package … --version` dentro del `.csproj` rompió la build con `MSB4025: An XML comment cannot contain '--'`. Está anotado en CLAUDE.md desde `3.1` y se volvió a pisar aquí, porque es imposible acordarse en el momento en que uno escribe justamente esa frase. El arreglo es no escribir el nombre del flag.

**`UseEntityFrameworkOutbox` no existe en el configurador del bus.** El error es `CS1929 … requires a receiver of type 'IReceiveEndpointConfigurator'`, y es útil porque nombra el tipo correcto — pero no dice cómo llegar a él cuando los endpoints los crea `ConfigureEndpoints`. La respuesta es `AddConfigureEndpointsCallback`, que va en el `x` (el `IBusRegistrationConfigurator`) y no dentro del `UsingRabbitMq`.

**`ReceiveCount` del `InboxState` no es el número de publicaciones.** Dos envíos por mano del mismo `message_id` dejaron la fila con `ReceiveCount = 4`. No se investigó más porque lo que importaba —una sola ejecución del consumer— estaba comprobado por otra vía, pero conviene saberlo antes de escribir una aserción sobre ese campo en `4.7`.

**El bloqueo de Smart App Control no apareció**, y merece anotarse porque era lo esperable: paquete nuevo descargado de nuget.org más ensamblados recién compilados es la combinación que lo dispara en `1.7`, `3.5`, `3.7` y `4.4`. Esta vez ni un `0x800711C7` en toda la sesión, ni en las cinco suites ni en los tres servicios. La escalada documentada en CLAUDE.md sigue siendo la buena; simplemente no hizo falta.

**Los dos mensajes de `order-state_error` son de `4.4`, no de este punto.** Se comprobó antes de dar nada por bueno: son un `StockReserved` y un `PaymentCompleted`, exactamente los que la verificación 9 de [fase_4_4.md](fase_4_4.md) dejó documentados —un `OrderCreated` publicado a mano mientras Orders.API estaba caído por el bloqueo de Smart App Control—. Ninguno de los ocho pedidos de hoy generó un fallo.

**Dos trampas de PowerShell 5.1, las dos ya conocidas y las dos vueltas a pisar.** `Invoke-RestMethod`/`ConvertFrom-Json` contra la API de management entregan **un** objeto que es el array entero, así que `$m.Count` sale vacío y `$m[$i]` no indexa nada; hay que sacar los datos con una expresión regular sobre el texto crudo o con `foreach`. Y pasar un JSON con `-d '{"count":2,…}'` a `curl.exe` funciona unas veces y otras llega mutilado (`{"error":"bad_request","reason":"not_json"}`): lo fiable es escribirlo a un archivo **sin BOM** con `[IO.File]::WriteAllText` y usar `--data-binary "@ruta"`.

**El `max_length` de `sys.columns` sigue siendo en bytes.** `nvarchar(4000)` se lee como `8000` y `nvarchar(64)` como `128`. Anotado desde `3.6` y confundido otra vez durante medio minuto.

**Dos avisos `xUnit1051` en `Orders.Tests` son previos a este punto** y aparecieron en el build solo porque cambió `Orders.Infrastructure` y forzó recompilar el proyecto de test. `git status` confirma que no se tocó ningún archivo de `tests/`. La regla de que el build reporte `0 Warning(s)` está incumplida desde antes; se anota en *Pendiente* en vez de arreglarse aquí de tapadillo.

---

## Verificación

### 1. Compilación y las cinco suites

```
dotnet build   ->  Build succeeded. 2 Warning(s), 0 Error(s)   (los dos avisos son previos, ver arriba)

Shop133.ArchitectureTests  Total: 16, Errors: 0, Failed: 0, Skipped: 0, Time: 0.359s
Orders.Tests               Total: 12, Errors: 0, Failed: 0, Skipped: 0, Time: 60.336s
Catalog.Tests              Total: 19, Errors: 0, Failed: 0, Skipped: 0, Time: 79.036s
Inventory.Tests            Total: 15, Errors: 0, Failed: 0, Skipped: 0, Time: 102.246s
Payments.Tests             Total:  9, Errors: 0, Failed: 0, Skipped: 0, Time: 62.198s
```

**71 tests, los mismos que dejó `4.4`.** Las 16 de arquitectura confirman que el paquete nuevo no rompe ninguna regla. Las 12 de `Orders.Tests` pasan **aunque el `OrdersApiFactory` desmonte el repositorio EF y el outbox junto con el resto de MassTransit** — o sea que no prueban nada de este punto, ver *Pendiente*.

### 2. El esquema

```
tabla                     cols
----------------------    ----
__EFMigrationsHistory        2
InboxState                  11
OrderItems                   7
Orders                       4
OrderStates                  6
OutboxMessage               21
OutboxState                  6
ProcessedMessages            4

--- OrderStates ---
name                 tipo              max_length  is_nullable
CorrelationId        uniqueidentifier          16            0
CurrentState         nvarchar                 128            0
CustomerEmail        nvarchar                 640            0
CreatedAt            datetimeoffset            10            0
CancellationReason   nvarchar                8000            0
RowVersion           timestamp                  8            0

--- default en la PK? ---
defaults_en_OrderStates
                      0
```

Las tres líneas que había que comprobar: `RowVersion` sale como `timestamp` (el nombre interno de `rowversion` en SQL Server), `CorrelationId` **no tiene `DEFAULT`** —el `ValueGeneratedNever()` aguantó, que es lo que `2.2` avisaba que se cobraría aquí— y las longitudes son las declaradas (recordando el factor 2 de los bytes).

### 3. El camino feliz, persistido

```
POST /orders  ->  201, OrderId 0b6f6b6a-9564-4768-8ac0-7d69ac96e810

Saga arrancada para el pedido 0b6f6b6a-… de saga45-feliz@shop133.test; pasa a StockPending.
Pedido 0b6f6b6a-…: stock reservado por 298; pasa a PaymentPending.
Pedido 0b6f6b6a-…: cobro aceptado por 298 (transacción SIM-32C7480AC7374DA6B005DC28B3E6A1E4); pasa a Confirmed y se publica OrderConfirmed.
Pedido 0b6f6b6a-… confirmado en OrdersDb; su estado pasa de Pending a Confirmed.

GET /orders/0b6f6b6a-…  ->  status = Confirmed

CorrelationId                         CurrentState  CustomerEmail               CancellationReason  RowVersion
0B6F6B6A-9564-4768-8AC0-7D69AC96E810  Confirmed     saga45-feliz@shop133.test   (vacio)             0x00000000000007E3
```

La fila **se queda** en `Confirmed`, que es la decisión 7 funcionando: el desenlace se puede consultar. Y el `RowVersion` se movió, o sea que el token está vivo.

### 4. **La inversión de la verificación 7 de `4.1`** — la medición que justifica el punto

Se para Payments.API, se crea un pedido y se comprueba que la saga queda esperando:

```
OrderId: e23b07b7-6414-439e-9498-27d84074c1c4
CorrelationId                         CurrentState
E23B07B7-6414-439E-9498-27D84074C1C4  PaymentPending
```

Se **mata Orders.API** en ese punto y se arranca un proceso nuevo:

```
Orders.API relanzado (proceso NUEVO). Sagas arrancadas en este proceso: 0
```

Se arranca Payments.API y llega el `PaymentCompleted` pendiente:

```
Pedido e23b07b7-…: cobro aceptado por 149 (transacción SIM-4049A62EA96A469390308A9E05227D15); pasa a Confirmed y se publica OrderConfirmed.
Pedido e23b07b7-… confirmado en OrdersDb; su estado pasa de Pending a Confirmed.

GET /orders/e23b07b7-…  ->  status = Confirmed
```

**Cero líneas de "Saga arrancada" en el proceso nuevo**, y sin embargo el pedido termina. En `4.1`, la misma secuencia daba una línea de "Saga arrancada" —la saga no reconocía el pedido y empezaba de cero— y el pedido se quedaba sin nadie que lo moviera. Ese contraste es el punto entero.

### 5. **El agujero de la doble escritura, cerrado**

Con el broker **parado**:

```
OutboxMessage antes de parar el broker: 0
 Container shop133-rabbitmq  Stopped

POST /orders con RabbitMQ PARADO -> 201 en 130 ms, OrderId 8e65f8f9-5f08-4de8-969d-6aaaae7a6b7c

pedidos  outbox  sagas
      1       1      0

MessageType
urn:message:Shop133.Contracts.Events:OrderCreated
```

**201 en 130 ms.** En `3.3` esta misma petición **se colgaba**: un `Publish` sobre el transporte de RabbitMQ espera a que haya conexión en vez de fallar rápido, y eso convirtió a `docker compose up -d` en prerrequisito de `Orders.Tests`. Ahora el evento está en una fila de `OutboxMessage`, confirmada en la misma transacción que el pedido, y la saga todavía no existe.

Se arranca el broker y **no se toca nada más**:

```
 Container shop133-rabbitmq  Started
Tras arrancar RabbitMQ, sin tocar nada mas -> status = Confirmed

outbox_pendiente
               0

CorrelationId                         CurrentState
8E65F8F9-5F08-4DE8-969D-6AAAAE7A6B7C  Confirmed
```

El outbox se vació solo y la saga corrió entera. Eso es lo que la decisión 3 de `3.3` dijo que no se podía hacer "con dos sistemas y sin transacción distribuida", resuelto por la única vía que existe.

### 6. Idempotencia con el inbox delante — y quién habla primero

El mismo `message_id` publicado **dos veces** al exchange `order-confirmed`, con el sobre completo (`content_type: application/vnd.masstransit+json`, `messageType` con URN, `message_id`, JSON sin BOM), contra un pedido que ya estaba en `Confirmed`:

```
envio 1: {"routed":true}
envio 2: {"routed":true}

=== Lineas de OrderConfirmedConsumer para ese pedido ===
El pedido 0b6f6b6a-… ya estaba en Confirmed; no se vuelve a mover.     <- UNA sola

=== Aparece la guarda de TRANSPORTE de 3.6 ("ya lo procesó ... se descarta")? ===
NO aparece ni una vez.

filas_ProcessedMessages_de_ese_MessageId  filas_InboxState  receive_count
                                       1                 1              4
```

Ninguna cola `order-confirmed_error` llegó a crearse. Es la comparación de la decisión 8, medida: el inbox intercepta la segunda entrega **antes** del `Consume`, así que la guarda explícita de `3.6` ya no la alcanza un duplicado normal — la que sí habló fue la de **negocio**.

### 7. La compensación completa, persistida (escenario 3 de la fase)

```
QuantityReserved del producto 41 ANTES: 0
OrderId: 20885df4-2f01-491f-94d8-6d278d51df90 (total 1197.00, por encima del umbral de 1000)

Saga arrancada para el pedido 20885df4-… de saga45-compensacion@shop133.test; pasa a StockPending.
Pedido 20885df4-…: stock reservado por 1197; pasa a PaymentPending.
Pedido 20885df4-…: cobro rechazado (el importe 1197.00 supera el límite autorizado de 1000.00); pasa a CompensatingStock y se envía ReleaseStock a queue:release-stock. El pedido NO se cancela hasta que Inventory conteste StockReleased.
Pedido 20885df4-…: stock liberado por Inventory; pasa a Cancelled y se publica OrderCancelled (el importe 1197.00 supera el límite autorizado de 1000.00). La compensación está completa.
Pedido 20885df4-… cancelado en OrdersDb (…); su estado pasa de Pending a Cancelled.

status final: Cancelled
QuantityReserved del producto 41 DESPUES: 0
ReleasedAt de la reserva: 2026-09-02 22:46:33.4299612 +0

CurrentState  CancellationReason                                          RowVersion
Cancelled     el importe 1197.00 supera el límite autorizado de 1000.00   0x000000000000084E
```

El `CancellationReason` **sobrevive persistido** entre `PaymentFailed` y `StockReleased`, que es la razón por la que ese campo existe (decisión de `4.4`). Cuatro `UPDATE` sobre la fila y el `RowVersion` avanzó en cada uno.

### 8. Topología del broker: sin cambios

```
=== Colas ===                        === Exchanges de Shop133 ===
order-cancelled        messages=0    Shop133.Contracts.Commands:ReleaseStock      fanout
order-confirmed        messages=0    Shop133.Contracts.Events:OrderCancelled      fanout
order-created          messages=0    Shop133.Contracts.Events:OrderConfirmed      fanout
order-created_error    messages=0    Shop133.Contracts.Events:OrderCreated        fanout
order-state            messages=0    Shop133.Contracts.Events:PaymentCompleted    fanout
order-state_error      messages=2    Shop133.Contracts.Events:PaymentFailed       fanout
release-stock          messages=0    Shop133.Contracts.Events:StockRejected       fanout
stock-reserved         messages=0    Shop133.Contracts.Events:StockReleased       fanout
                                     Shop133.Contracts.Events:StockReserved       fanout
```

Las mismas seis colas funcionales y los mismos nueve exchanges que dejó `4.4`. **El outbox no añade ni una cola ni un exchange**: es enteramente del lado de la base de datos, que es justo lo que lo hace transaccional.

### 9. El coste en latencia, medido porque la intuición dice lo contrario

```
pedido 1: 201 en 32 ms  ·  Confirmed en 262 ms
pedido 2: 201 en 28 ms  ·  Confirmed en 252 ms
pedido 3: 201 en  9 ms  ·  Confirmed en 239 ms
media de la saga completa: 251 ms
```

Un outbox que se vacía por sondeo suena a "esto añade un segundo por mensaje", y no: la saga entera —cuatro servicios, tres saltos y ocho escrituras— sigue cerrándose en un cuarto de segundo. MassTransit entrega en cuanto el commit confirma y el sondeo es la red de seguridad, no el camino normal. Sin este número, la decisión parecería cara.

---

## Pendiente

- **`4.7` — los tests de la saga, y ahora con una deuda concreta.** `OrdersApiFactory` borra todo `ServiceDescriptor` cuyo ensamblado empiece por `MassTransit`, así que en la suite desaparecen el repositorio EF, el outbox y la propia saga. Consecuencia inmediata: **nada de este punto está cubierto por un test**, ni siquiera el orden invertido del `Publish` en `OrdersController` — con el harness, ese `Publish` va directo y el test pasaría igual con el orden viejo. Las nueve verificaciones de arriba son a mano y no sobreviven a un refactor. `4.7` tiene que dejar de desmontar la saga.
- **La concurrencia optimista no se ha probado en anger.** Se comprobó que la columna existe, que es una `rowversion` y que avanza en cada `UPDATE`, pero **no se ha forzado un choque real** — hacen falta dos entregas simultáneas del mismo pedido, que es material del harness (`4.7`) o de infraestructura real (`8.2`). Hasta entonces, el `UseMessageRetry` es una defensa razonada y no medida.
- **`CancellationReason` tiene 4000 caracteres y el `Reason` de `StockRejected` puede pasar de 4500.** Hoy no llega a esta columna por ningún camino —solo escribe ahí el camino de `PaymentFailed`, cuyo texto es corto y fijo—, pero si algún día llegara, SQL Server **no trunca: lanza** (error 2628) y la saga acabaría en `order-state_error`. Está escrito en el `///` de la constante en vez de tapado con un `nvarchar(max)`.
- **`OrderStates` crece sin techo y nadie la purga** (decisión 7), igual que `ProcessedMessages` desde `3.6` y que `InboxState`/`OutboxMessage`, que sí se purgan solas. El día que aparezca una purga, aparece con su índice sobre `CreatedAt` — que es además el que haría falta para la consulta "sagas que llevan demasiado sin terminar".
- **El pedido atascado en `CompensatingStock` sigue sin plazo y sin dueño**, tal cual lo dejó `4.4`. Con la saga ya persistida el agujero es distinto: la instancia ya no se pierde, así que el pedido se queda esperando *visiblemente* en una fila que alguien podría consultar. Lo que falta es quien la consulte — un `Schedule` con timeout en la saga.
- **`8.2` — la PK clustered**, que ahora por fin se puede medir con `sys.dm_db_index_physical_stats` sobre una tabla con volumen, en vez de discutirse (decisión 9).
- **Dos avisos `xUnit1051` en `Orders.Tests`**, previos a este punto y no introducidos por él. Incumplen la norma de que el build reporte `0 Warning(s)`; se dejan visibles en vez de arreglarse de tapadillo dentro de un punto que no toca `tests/`.
- **`4.8`/`4.9`** — la validación de la foto de precios sigue sin dueño, exactamente donde la dejó la corrección 2b de [fase_3_3.md](fase_3_3.md).
