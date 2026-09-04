# Plan de Desarrollo — shop133 (Microservicios .NET)

## Stack definido

| Capa | Tecnología |
|---|---|
| Backend | .NET 10, ASP.NET Core Web API (controllers) |
| Frontend | ASP.NET Core MVC / Razor Pages + Bootstrap 5 |
| Mensajería | RabbitMQ + MassTransit (Saga State Machine) |
| Base de datos | SQL Server (una instancia/DB por microservicio) |
| ORM | EF Core |
| Gateway | YARP |
| Contenedores | Docker + Docker Compose |
| Observabilidad | OpenTelemetry + Jaeger + Serilog |

**Nota de arquitectura:** el Frontend NO es un microservicio más de negocio — es un cliente web (BFF opcional) que consume el API Gateway. No debe acceder directamente a las bases de datos de los servicios.

---

## Estructura de solución

```
shop133/
├── src/
│   ├── Services/
│   │   ├── Catalog/
│   │   │   ├── Catalog.API/
│   │   │   └── Catalog.Infrastructure/
│   │   ├── Orders/
│   │   │   ├── Orders.API/
│   │   │   ├── Orders.Domain/          (Saga State Machine aquí)
│   │   │   └── Orders.Infrastructure/
│   │   ├── Payments/
│   │   │   └── Payments.API/
│   │   ├── Inventory/
│   │   │   └── Inventory.API/
│   │   └── Notifications/
│   │       └── Notifications.API/
│   ├── Gateway/
│   │   └── Shop133.Gateway/             (YARP)
│   ├── Frontend/
│   │   └── Shop133.Web/                 (MVC + Bootstrap 5)
│   └── Shared/
│       └── Shop133.Contracts/           (eventos compartidos: OrderCreated, StockReserved, etc.)
├── tests/
│   ├── Shop133.ArchitectureTests/       (reglas de CLAUDE.md ejecutables)
│   └── Services/
│       ├── Catalog/
│       │   └── Catalog.Tests/
│       ├── Orders/
│       │   └── Orders.Tests/            (saga + consumers)
│       ├── Inventory/
│       │   └── Inventory.Tests/
│       └── Payments/
│           └── Payments.Tests/
├── db/
│   └── init/                            (scripts SQL que ejecuta el servicio db-init)
├── docs/                                (un .md por subfase completada — ver docs/README.md)
├── docker-compose.yml
└── docker-compose.override.yml
```

`Shop133.Contracts` es clave: contiene solo los DTOs/eventos que viajan por RabbitMQ, referenciado por todos los servicios para mantener consistencia de contratos sin acoplar lógica.

---

## Estrategia de tests

Los tests no son una fase — están repartidos por el roadmap, en el punto donde el código que prueban empieza a existir. La razón: lo que este proyecto existe para enseñar (la saga, las compensaciones, la idempotencia) es justo lo que no se puede verificar a mano de forma fiable. Comprobar una compensación con curl y la UI de RabbitMQ funciona una vez; no te avisa cuando la Fase 5 la rompe.

**Qué tipo de test cubre qué:**

| Tipo | Herramienta | Qué verifica | Coste |
|---|---|---|---|
| Arquitectura | NetArchTest | Reglas estructurales: Contracts sin dependencias, dirección de referencias, eventos inmutables | Milisegundos |
| Saga y consumers | Harness de MassTransit (transporte en memoria) | Transiciones de estado, caminos de compensación, idempotencia | Milisegundos |
| Componente | `WebApplicationFactory` + Testcontainers | API + EF Core contra SQL Server real, de extremo a extremo dentro de un servicio | Segundos |
| Integración real | Testcontainers (SQL Server + RabbitMQ) | Persistencia de la saga, topología de exchanges, migraciones EF | Decenas de segundos |

**Qué se deja fuera deliberadamente:**

- **Contract testing (Pact).** Tiene sentido cuando productor y consumidor viven en repos y equipos distintos. Aquí `Shop133.Contracts` compartido *es* el contrato, y el compilador ya hace ese trabajo: renombrar una propiedad rompe la build de todos los servicios.
- **Tests unitarios de controllers CRUD.** Los controllers son delgados por convención (bind, delega, devuelve); no hay lógica que probar y el test solo duplicaría el mapeo.
- **E2E extensivo.** Caro de mantener y frágil. Solo un par de smoke tests en 8.6.

**El conjunto mínimo que rinde** a ritmo de side project: tests de arquitectura + saga con harness + una rebanada de componente por servicio. Todo lo demás es opcional.

---

## Fase 0 — Setup base (3-4 días)

- [x] **0.1** Crear solución y estructura de carpetas
- [x] **0.2** Configurar `docker-compose.yml` con: SQL Server, RabbitMQ, Jaeger — [doc](docs/fase_0_2.md)
- [x] **0.3** Crear proyecto `Shop133.Contracts` con eventos base — [doc](docs/fase_0_3.md)
- [x] **0.4** Configurar SQL Server con una base de datos por servicio (`CatalogDb`, `OrdersDb`, `InventoryDb`, `PaymentsDb`) — [doc](docs/fase_0_4.md)
- [x] **0.5** Repositorio Git con convención de branches (`main`, `develop`, `feature/*`) — [doc](docs/fase_0_5.md)
- [x] **0.6** Proyecto `tests/Shop133.ArchitectureTests` con NetArchTest: `Shop133.Contracts` sin dependencias externas, eventos como `record` inmutables, `Orders.Domain` sin referencias a otros proyectos, ningún servicio referencia el `DbContext` de otro — [doc](docs/fase_0_6.md)

**Por qué 0.6 va aquí y no más tarde:** los proyectos ya existen vacíos y `Shop133.Contracts` ya tiene sus 9 mensajes. Fijar las reglas *antes* de escribir código de servicio es lo que las convierte en una barrera; añadirlas después es un ejercicio de arqueología.

**Docker Compose — servicio SQL Server:**
```yaml
sqlserver:
  image: mcr.microsoft.com/mssql/server:2022-latest
  environment:
    - ACCEPT_EULA=Y
    - MSSQL_SA_PASSWORD=YourStrong@Passw0rd
  ports:
    - "1433:1433"
  volumes:
    - sqlserver-data:/var/opt/mssql
```

---

## Fase 1 — Catalog.API (síncrono, base) (1 semana)

- [x] **1.1** Modelo `Product` (Id, SKU, Nombre, Descripción, Precio, Stock inicial, ImagenUrl) — [doc](docs/fase_1_1.md)
- [x] **1.2** EF Core + migraciones contra SQL Server (`CatalogDb`) — [doc](docs/fase_1_2.md)
- [x] **1.3** Endpoints: `GET /products`, `GET /products/{id}`, `POST /products`, `PUT`, `DELETE` — [doc](docs/fase_1_3.md)
- [x] **1.4** Seed de datos de prueba — [doc](docs/fase_1_4.md)
- [x] **1.5** Swagger/OpenAPI habilitado — [doc](docs/fase_1_5.md)
- [x] **1.6** Dockerfile del servicio — [doc](docs/fase_1_6.md)
- [x] **1.7** `Catalog.Tests`: tests de componente con `WebApplicationFactory` + Testcontainers (SQL Server) sobre los endpoints de 1.3 — [doc](docs/fase_1_7.md)

**Objetivo de la fase:** tener un servicio funcional end-to-end (DB → API → Docker) antes de meter mensajería.

**Sobre 1.7:** es la primera infraestructura de test del proyecto (fixture del contenedor, reset de datos entre tests) y se monta sobre el servicio más simple, para reutilizarla después en los demás. Nada de provider InMemory de EF Core: no aplica constraints ni transacciones reales y deja pasar bugs que en SQL Server explotan.

---

## Fase 2 — Orders.API + comunicación síncrona inicial (1 semana)

- [x] **2.1** Modelo `Order`, `OrderItem` (estado: Pending, Confirmed, Cancelled) — [doc](docs/fase_2_1.md)
- [x] **2.2** EF Core contra `OrdersDb` — [doc](docs/fase_2_2.md)
- [x] **2.3** `POST /orders` que llama síncronamente (HttpClient) a Catalog.API para validar productos/precios — [doc](docs/fase_2_3.md)
- [x] **2.4** `Orders.Tests`: test del acoplamiento síncrono con WireMock.Net — camino feliz **y** "Catalog caído → Orders falla" — [doc](docs/fase_2_4.md)

**Objetivo de la fase:** aquí **sentirás el acoplamiento** — si Catalog.API está caído, Orders falla. Este dolor es intencional; es el que resuelves en la Fase 3. No es una tarea entregable, por eso no lleva número.

**Sobre 2.4:** estos tests son deuda igual que el código que prueban — márcalos `// PHASE-2 DEBT` y bórralos en 3.7. *(Se borraron antes, en **3.3**: dejaron de compilar en cuanto `OrdersApiFactory` perdió su parámetro `catalogBaseUrl`, y mantenerlos vivos habría sido dejarlos en `Skip` toda la fase. Ver la decisión 8 de [fase_3_3.md](docs/fase_3_3.md).)* El objetivo no es cobertura, es hacer el fallo **reproducible**: un test que afirma "Catalog caído ⇒ el pedido no se crea" y que en la Fase 3 deja de tener sentido. El diff que lo elimina documenta el cambio de arquitectura mejor que un párrafo.

---

## Fase 3 — Mensajería con MassTransit + RabbitMQ (1-1.5 semanas)

- [x] **3.1** Instalar MassTransit + RabbitMQ transport en Orders, Inventory, Payments — [doc](docs/fase_3_1.md)
- [x] **3.2** Definir eventos en `Shop133.Contracts`: `OrderCreated`, `StockReserved`, `StockRejected`, `PaymentCompleted`, `PaymentFailed` — [doc](docs/fase_3_2.md)
- [x] **3.3** Orders.API publica `OrderCreated` en lugar de llamar síncronamente — [doc](docs/fase_3_3.md)
- [x] **3.4** Inventory.API consume `OrderCreated`, valida y reserva stock contra `InventoryDb`, publica `StockReserved`/`StockRejected` — [doc](docs/fase_3_4.md)
- [x] **3.5** Payments.API consume `StockReserved`, simula cobro, publica `PaymentCompleted`/`PaymentFailed` — [doc](docs/fase_3_5.md)
- [x] **3.6** Implementar **idempotencia**: guardar `MessageId` procesados para evitar duplicados — [doc](docs/fase_3_6.md)
- [x] **3.7** Tests de consumers con `MassTransit.TestFramework` (`AddMassTransitTestHarness`): Inventory y Payments publican el evento correcto ante cada entrada, más el **test de idempotencia** (mismo `MessageId` dos veces → un solo efecto). ~~Incluye borrar los tests de 2.4~~ — ya borrados en 3.3. Queda además quitarle a `Orders.Tests` la dependencia del broker real que 3.3 estrenó — [doc](docs/fase_3_7.md)

**Sobre 3.7:** el harness usa transporte en memoria — sin Docker, sin RabbitMQ, milisegundos por test. RabbitMQ real se prueba en 8.2, y solo para lo que el harness no puede cubrir (topología de exchanges). El test de idempotencia es la única verificación fiable de 3.6: a mano habría que republicar el mismo mensaje y comparar estado de base de datos.

*Lo de "sin Docker, milisegundos por test" era cierto cuando se escribió y dejó de serlo con `3.4`, `3.5` y `3.6`: los consumers que había que probar acabaron teniendo **base de datos**. El harness quita el **broker**, no el SQL Server, y la regla 1 de la estrategia de tests prohíbe el provider InMemory — así que `Inventory.Tests` y `Payments.Tests` son `Category=Docker` como las otras dos. Lo que sí se cumplió es lo de RabbitMQ: ninguna de las cuatro suites lo necesita ya, y `Orders.Tests` pasa en verde con el broker parado. Ver [fase_3_7.md](docs/fase_3_7.md).*

---

## Fase 4 — Saga completa con compensaciones (1-1.5 semanas)

Este es el núcleo del aprendizaje.

- [x] **4.1** Crear `OrderStateMachine` en Orders.Domain con MassTransit Saga — [doc](docs/fase_4_1.md)
- [x] **4.2** Estados: `Submitted → StockPending → StockReserved → PaymentPending → Confirmed` — [doc](docs/fase_4_2.md)
- [x] **4.3** Camino de error: `StockRejected → Cancelled` / `PaymentFailed → CompensatingStock → Cancelled` — [doc](docs/fase_4_3.md)
- [x] **4.4** Implementar evento de compensación `ReleaseStock` cuando el pago falla después de reservar — [doc](docs/fase_4_4.md)
- [x] **4.5** Persistir el estado de la Saga en `OrdersDb` (SQL Server como Saga repository) — [doc](docs/fase_4_5.md)
- [x] **4.6** Notifications.API consume `OrderConfirmed`/`OrderCancelled` y "envía" notificación (log o mock de email) — [doc](docs/fase_4_6.md)
- [x] **4.7** Automatizar los 4 escenarios obligatorios contra `OrderStateMachine` con el harness de MassTransit — [doc](docs/fase_4_7.md)
- [ ] **4.8** Catalog.API estrena MassTransit: consume `OrderCreated` y valida la foto de precios contra `CatalogDb`, publicando `OrderPricingValidated` / `OrderPricingRejected`
- [ ] **4.9** La saga gana `PricingPending` **antes** de `StockPending`: `OrderPricingRejected → Cancelled` sin nada que compensar, con su caso en el harness

**Escenarios de prueba obligatorios:**
1. Compra exitosa (feliz)
2. Sin stock disponible
3. Stock reservado pero pago rechazado (compensación)
4. Evento duplicado (verificar idempotencia)

Esta lista es la **especificación del punto 4.7**. Escribir esos tests *antes* que la máquina de estados te obliga a decidir los estados finales y qué mensajes salen en cada camino, que es el diseño de la saga. El caso 3 en concreto afirma que se publica **exactamente un** `ReleaseStock` y que el estado final es `Cancelled` — la regla de que el stock reservado nunca se filtra, en forma ejecutable.

**Sobre 4.1 — la saga observa la coreografía, no la orquesta (decidido el 2026-09-01).** La lista de arriba no dice quién manda `ReserveStock`, y la respuesta condiciona 4.2 y 4.4: la saga consume los mismos eventos que ya vuelan desde la Fase 3 y solo **emite** el comando de compensación (4.4) y los eventos terminales (4.3/4.6). Inventory sigue consumiendo `OrderCreated` por su cuenta y Payments `StockReserved` — dos consumidores del mismo exchange fanout, no un relevo. La orquestación pura obligaría a cambiar el consumer de Inventory, que esta fase no contempla tocar, y contradiría la decisión 2 de [fase_3_2.md](docs/fase_3_2.md) y la 6 de [fase_3_4.md](docs/fase_3_4.md). **El precio, dicho en voz alta: `ReserveStock` se queda sin usar**, y puede acompañarlo el `Lines` de `ReleaseStock` según lo que decida 4.4. Ver la decisión 2 de [fase_4_1.md](docs/fase_4_1.md).

**Sobre 4.2 — se entregaron tres estados de los cinco del título (decidido el 2026-09-01).** `Submitted` y `StockReserved` no llegaron a existir, y no es un recorte: son consecuencia directa de la decisión de arriba. Un estado tiene sentido cuando la saga **espera** una respuesta, y lo que crea esa espera es haber mandado un comando; aquí no se manda ninguno, así que los dos se entrarían y se saldrían en la misma transición. La regla que queda: *en una saga que observa, hay un estado por cada respuesta que se espera, no por cada hecho que ocurre.* `4.9` encaja sin discusión —`PricingPending` **sí** es una espera real, la de Catalog— y no resucita a `Submitted`. Lo que sí se entregó y el título no menciona es el `OrderConfirmed`: es **el primer mensaje que emite la saga** en todo el proyecto. `Order.Status` sigue en `Pending` a propósito y se mueve en `4.3`. Ver las decisiones 1 y 2 de [fase_4_2.md](docs/fase_4_2.md).

**Sobre 4.3 — `CompensatingStock` tampoco llegó a existir (decidido el 2026-09-02).** Es la misma regla de arriba aplicada por segunda vez, y por eso conviene fijarse: la saga no manda `ReleaseStock` hasta `4.4` y, aunque lo mandara hoy, **no hay ningún `StockReleased` en `Shop133.Contracts`** con el que Inventory pudiera contestar. Sin respuesta que esperar, el estado se entraría y se saldría en la misma transición. Se entregó `PaymentFailed → Cancelled` directo, y `4.4` lo relee con el comando delante: si decide que Inventory conteste, el estado aparece allí con su espera de verdad. Lo que sí distingue a los dos caminos de error no es el estado, es **lo que queda por deshacer** — y está medido: un pedido cancelado por pago rechazado deja sus unidades reservadas.

**Sobre 4.4 — `CompensatingStock` resucita, y eso corrige la nota de arriba (decidido el 2026-09-02).** El párrafo de `4.3` decía que el estado no llegó a existir por no haber ninguna respuesta que esperar, y anunciaba que `4.4` lo relee con el comando delante. Releído: **sí existe**. `4.4` quita las dos condiciones a la vez —la saga manda `ReleaseStock` y se añade `StockReleased`, el décimo contrato, para que Inventory conteste—, así que la espera es real y el estado se gana su sitio por la misma regla que se lo negaba. La regla no cambió; cambió el mundo que describe, que es el motivo de haberla escrito. Lo que decidió que Inventory contestara no fue la simetría: el `///` de `OrderCancelled` afirma desde `0.3` que en ese camino "el stock ya se soltó con `ReleaseStock`", y sin respuesta esa frase es una promesa que la saga no puede cumplir.

Las otras dos preguntas abiertas también se cierran. **`ReleaseStock` pierde sus `Lines`** —aplazada desde `0.3` y prometida a este punto por `3.2`— porque la PK de `StockReservations` *es* el `OrderId`: el consumer lee de su propia tabla, y así `OrderState` no se lleva una colección que `4.5` tendría que persistir. Y **se manda con `Send`, no con `Publish`**: es el único comando que el proyecto llega a mandar (`ReserveStock` sigue sin llamante) y el único `Send`, porque un fanout dejaría que un segundo consumidor soltara el stock dos veces. Medido, y es el punto entero: un pedido de `1197.00` reserva 3 unidades, se le rechaza el cobro y vuelve a 0 sin intervención manual, con el pedido en `Cancelled` solo *después* de que Inventory conteste. En `4.3` esas 3 unidades se quedaban reservadas para siempre. Ver [fase_4_4.md](docs/fase_4_4.md).

Lo que el título de `4.3` no menciona y también se entregó: **Orders.API estrena sus dos primeros consumers** (`OrderConfirmedConsumer`, `OrderCancelledConsumer`) y con ellos la tabla `ProcessedMessages` de `3.6` en `OrdersDb`, así que **`Order.Status` por fin se mueve** — `GET /orders/{id}` ya contesta `Confirmed` o `Cancelled` en vez de quedarse en `Pending` para siempre. El rodeo de que un servicio consuma un mensaje que él mismo publica es el precio de la regla 5: la saga vive en `Orders.Domain` y no ve `OrdersDbContext`. Ver las decisiones 1, 4 y 6 de [fase_4_3.md](docs/fase_4_3.md).

**Sobre 4.5 — el título dice "persistir la saga" y se entregó también el outbox transaccional (decidido el 2026-09-02).** No es alcance inventado: el outbox estaba asignado a este punto **por escrito en cuatro documentos** (`3.3` d3/d4, `3.5` d7, `3.6` d2/d3, `4.3` Pendiente), porque los dos agujeros son el mismo problema visto desde dos sitios — no hay transacción entre SQL Server y RabbitMQ. Con la saga persistida y el mensaje escrito en la misma transacción que el trabajo, **las dos mediciones que justificaban el punto se invierten**: reiniciar Orders.API con un pedido en `PaymentPending` ya no pierde la instancia (cero líneas de "Saga arrancada" en el proceso nuevo, y el pedido termina igual), y un `POST /orders` con **RabbitMQ parado** devuelve `201` en 130 ms —en `3.3` se colgaba— con el evento esperando en `OutboxMessage` y saliendo solo cuando el broker vuelve.

Se cierran además las seis preguntas que otros puntos habían aplazado aquí: comparte `OrdersDbContext` (sin alternativa razonable: dos contextos son dos transacciones), concurrencia **optimista** con `RowVersion` —y su `UseMessageRetry`, que es la mitad fácil de olvidar—, la PK sigue *clustered* por tercera vez, los estados terminales **no** pasan a `Finalize()` (la fila del desenlace tiene que poder consultarse, y `4.7` y `6.5` la van a querer), y la revisión del bloque `AddMassTransit` se cierra **sin reprogramarse**. La comparación `InboxState` vs `ProcessedMessages` que encargó `3.6` salió más incómoda de lo previsto y se documenta como tal: el inbox intercepta el duplicado **antes** del `Consume`, así que la guarda explícita de `3.6` ya no la alcanza una reentrega normal — la que sigue siendo insustituible es la de **negocio**. Ver [fase_4_5.md](docs/fase_4_5.md).

Lo que este punto **no** trae y conviene saber antes de `4.7`: ni una sola de sus nueve verificaciones es un test. `OrdersApiFactory` desmonta todo MassTransit, así que en la suite no hay saga, ni outbox, ni orden invertido del `Publish` — los 71 tests pasan exactamente igual con el código de `4.4`.

**Sobre 4.6 — la coreografía por fin se ve, y el punto trae una quinta base de datos (decidido el 2026-09-03).** Notifications.API era la plantilla `webapi` intacta desde `0.1`; ahora estrena MassTransit, dos consumers y `NotificationsDb`. Es el **primer servicio que consume un evento que otro servicio ya consume** —`OrderConfirmed` y `OrderCancelled` tienen desde hoy **dos colas ligadas cada uno**, la de Orders y la de Notifications—, o sea la decisión 2 de `4.1` hecha visible entre dos servicios distintos y no dentro de uno. `Shop133.Contracts` no se tocó: los dos eventos existen desde `0.3` con su `CustomerEmail` dentro, que es justo lo que aquellas declaraciones venían prometiendo.

La base de datos **no reversa el `///` de los contratos**, que decía que este servicio no tiene una. Lo que aquella frase afirmaba —que Notifications no puede consultar el pedido en ningún sitio, y por eso el email viaja en el evento— sigue siendo cierto palabra por palabra. La base entra por el motivo exacto por el que Payments ganó la suya en `3.5`: **sin una fila que consultar, el consumer no puede ser idempotente de ninguna forma**, y la regla 6 no admite excepciones. Descartada una guarda en memoria (se pierde al reiniciar, que es cuando una reentrega es más probable) y descartado documentar el agujero (eso vale para un hueco sin dueño como la concurrencia de `StockItem`, no para saltarse una regla en el servicio más barato de arreglar). La tabla `Notifications` que sale de ahí convierte "mock de email" en algo que se comprueba con un `SELECT` y no contando líneas en una consola.

**El riesgo real del punto no era ninguno de los dos, y ningún test puede verlo.** Los consumers **no** se llaman como el mensaje que consumen —son `OrderConfirmedNotificationConsumer` y `OrderCancelledNotificationConsumer`—, rompiendo la convención a propósito: el formatter deriva la cola del nombre del tipo, y Orders.API ya es dueño de `order-confirmed` y `order-cancelled` desde `4.3`. Con nombres homónimos los dos servicios no serían dos suscriptores del fanout sino **consumidores competidores de una sola cola**, y cada evento llegaría a uno de los dos al azar: la mitad de los pedidos sin mover su `Order.Status` y la otra mitad sin aviso, **sin un solo error en ningún log**. Es la trampa de `queue:release-stock` de `4.4` al revés — allí el peligro era renombrar un consumer, aquí es no renombrarlo. Los tests de arquitectura leen `.csproj` y rutas de archivo, no la topología de un broker: se verifica mirando que `order-confirmed` siga teniendo **un solo** consumidor, no que las colas existan.

Medido: un pedido barato acaba `Confirmed` con su fila `Kind=Confirmation`; uno de `1197.00` acaba `Cancelled`, devuelve sus 3 unidades y el email lleva dentro *"el importe 1197.00 supera el límite autorizado"*; uno de un producto inexistente produce **la misma clase de aviso** con *"el producto 999999 no existe en el inventario"* — los dos caminos de error dan un solo `NotificationKind.Cancellation`, que es lo que el `///` de `OrderCancelled` prometía. La suite de arquitectura se queda en **16** y no se añade regla, dicho por escrito (precedente de `3.3`/`3.5`).

Lo que este punto **no** trae: ni un test. A diferencia de `3.4` y `3.5`, que verificaron a mano sabiendo que `3.7` recogería la deuda, **aquí no hay punto que la recoja** — `4.7` es la máquina de estados, no este servicio. Es el hueco más grande que deja y queda anotado como tal. Ver [fase_4_6.md](docs/fase_4_6.md).

**Sobre 4.7 — la saga por fin tiene tests, y hacían falta dos suites y no una (decidido el 2026-09-03).** El título pide los cuatro escenarios contra la máquina de estados; se entregan, y además la persistencia de `4.5`, que `CLAUDE.md` llamaba *"el primer trabajo de 4.7"*. Son dos preguntas distintas y se responden por separado: **`OrderStateMachineTests`** (9 tests) usa `InMemoryRepository()`, no toca SQL Server y es la **primera suite de servicio `Category=Fast` del repositorio** —10 s los nueve, frente a los 60-100 s de las otras cuatro—; **`OrderStatePersistenceTests`** (4) usa el repositorio EF contra Testcontainers. Las transiciones no se repiten en la segunda: son idénticas con los dos repositorios, porque `4.5` no cambió ni una línea de la saga. El repositorio pasa de **71 a 84** tests, la suite de arquitectura se queda en 16 y **no se toca ni un archivo de `src/`, ni un `.csproj`, ni entra un paquete**.

Se incumple a propósito lo que cuatro documentos habían dejado escrito. `4.1`, `4.2`, `4.3` y `4.5` repitieron que *"`OrdersApiFactory` tendrá que dejar de desmontar la saga"*; **no se hace**. Devolvérsela obliga a reescribir dentro del test el `AddMassTransit` entero de Orders.API —outbox, repositorio EF, callback de endpoints y los dos consumers— para probar una clase que no depende de nada de eso. Se cumple el fin por otro medio, con el criterio de la decisión 3 de [fase_3_7.md](docs/fase_3_7.md); el precio, ya aceptado entonces, es que nadie comprueba que `Program.cs` registre lo que dice, y eso es de `8.2`.

**Ningún test se dio por bueno sin verlo antes en rojo, y de los seis intentos de rotura el que más enseña es el que NO reprodujo.** Quitar las guardas de idempotencia falla por el `Assert.Empty(Published<Fault<…>>())` y **no** por el recuento de `OrderConfirmed`, que sigue saliendo 1 — la trampa 3 de `3.7` confirmada sobre la saga. Renombrar `queue:release-stock` tumba dos tests, que es el único acuerdo del proyecto que no vigila el compilador. Volver a `InMemoryRepository()` tumba los cuatro de persistencia. Pero **invertir `TransitionTo` y `.Send`, la carrera de la que avisa el `///` de esa transición, pasa en verde cinco veces seguidas**: lo que ordena los mensajes es el `ConcurrentMessageLimit = 1`, o sea que **la misma línea que hace deterministas todas las suites desde `3.7` es la que esconde esa carrera**. Queda anotado sin dueño en vez de bajar el límite y volver intermitentes los otros ocho tests. Ver [fase_4_7.md](docs/fase_4_7.md).

**Sobre 4.8 y 4.9 — añadidos el 2026-08-27, después de releer la decisión 2 de [fase_3_3.md](docs/fase_3_3.md).** `3.3` dejó que el cuerpo del `POST` traiga el precio y dio por hecho que la comprobación "se mudaba a Inventory". Solo se mudó la de **existencia**: Inventory guarda cantidades, no importes, así que un pedido de un producto que existe a `0.01` atraviesa toda la saga y **se cobra un céntimo**. El importe se había quedado sin dueño.

Lo que hay que validar **no es la igualdad, es la autenticidad de la foto**: comparar contra el precio actual rechazaría un pedido legítimo cuyo precio cambió a mitad del checkout, y congelar el precio que el cliente vio es el comportamiento correcto, no una concesión. Por eso la validación vive en el único servicio que puede firmar ese dato —Catalog— y lo hace **asíncronamente**: con Catalog caído el `POST` sigue devolviendo `201` y el pedido espera en `PricingPending`. Un retraso, no un `502`; lo que la Fase 3 ganó no se devuelve.

Van numerados al final porque los números son la clave entre commit, roadmap y `docs/` y **no se renumeran**, pero en orden de ejecución `4.8`/`4.9` caen junto a `4.2`/`4.3`: **`4.9` obliga a releer la lista de estados de 4.2**, que no contempla el nuevo. Añaden además dos contratos a los nueve que fijó `0.3` — con el precedente de `3.2`: un contrato se revisa cuando aparece el consumidor que lo necesita.

---

## Fase 5 — API Gateway con YARP (3-4 días)

- [ ] **5.1** Configurar rutas: `/api/catalog/*`, `/api/orders/*`, etc. hacia cada servicio
- [ ] **5.2** Agregar rate limiting básico
- [ ] **5.3** Centralizar CORS aquí (para que el Frontend solo hable con el Gateway)
- [ ] **5.4** Smoke de enrutado: cada ruta de 5.1 alcanza su servicio y el rate limiting de 5.2 devuelve `429` al superar el umbral

---

## Fase 6 — Frontend con Bootstrap 5 (1-1.5 semanas)

Proyecto `Shop133.Web` (ASP.NET Core MVC), consumiendo **únicamente el Gateway**, nunca los servicios directo.

- [ ] **6.1** Layout base con Bootstrap 5 (navbar, footer, `_Layout.cshtml`)
- [ ] **6.2** Vista de catálogo: grid de productos con `card` de Bootstrap
- [ ] **6.3** Carrito de compras **en sesión de servidor**, no en cookie — ver la nota de abajo
- [ ] **6.4** Formulario de checkout (Bootstrap forms + validación client-side)
- [ ] **6.5** Página de estado del pedido — aquí es interesante mostrar el estado en tiempo real:
  - Opción simple: polling cada 2-3s a `GET /orders/{id}/status`
  - Opción avanzada: SignalR para push en tiempo real cuando la Saga cambia de estado
- [ ] **6.6** Uso de `IHttpClientFactory` con Polly para resiliencia al llamar al Gateway (retry, circuit breaker)
- [ ] **6.7** Toasts de Bootstrap para feedback de éxito/error

**Sobre 6.3 — sesión, no cookie, y el motivo es de arquitectura.** El punto decía "sesión o cookie mientras no hay auth". Desde `3.3` el cuerpo de `POST /orders` lleva el precio, así que **quien guarda el carrito es quien acuña la foto del pedido**: en sesión de servidor la acuña `Shop133.Web` leyendo Catalog por el Gateway —lo que la regla 3 permite—, y en cookie la acuña el navegador. Con cookie, el cliente vuelve a dictar el importe y `4.8` se queda como única defensa. Es la tercera capa de la decisión 2b de [fase_3_3.md](docs/fase_3_3.md): `6.3` y `8.1` deciden **quién** puede mandar la foto, `4.8` decide si la foto es cierta. Hacen falta las dos.

**Sugerencia de UX que refuerza el aprendizaje de arquitectura:** muestra visualmente el estado del pedido avanzando por las etapas (Reservando stock → Procesando pago → Confirmado), para que el frontend refleje la naturaleza asíncrona del backend en vez de ocultarla.

---

## Fase 7 — Observabilidad (4-5 días)

- [ ] **7.1** Integrar OpenTelemetry en todos los servicios + Frontend
- [ ] **7.2** Exportar trazas a Jaeger (ya en docker-compose)
- [ ] **7.3** Serilog con logging estructurado, correlación por `TraceId`
- [ ] **7.4** Verificar que puedes seguir **una sola request del Frontend** a través de Gateway → Orders → RabbitMQ → Inventory → Payments → de vuelta, en Jaeger

Este es el "aha" del proyecto: ver visualmente la complejidad que la arquitectura introduce.

---

## Fase 8 — Pulido y extras opcionales (según tiempo)

- [ ] **8.1** Autenticación con JWT en el Gateway (Identity o Auth0/Keycloak)
- [ ] **8.2** Tests de integración con infraestructura real (Testcontainers): persistencia de la Saga en SQL Server con concurrencia optimista, topología de exchanges que MassTransit crea por convención, migraciones EF contra SQL Server real
- [ ] **8.3** CI/CD con GitHub Actions (build, test, push de imágenes Docker)
- [ ] **8.4** Health checks (`/health`) en cada servicio + panel simple en el Frontend
- [ ] **8.5** Migrar Kafka en lugar de RabbitMQ como ejercicio comparativo (opcional, ambicioso)
- [ ] **8.6** Dos o tres smoke tests E2E sobre `docker compose` (camino feliz y compensación)

**Sobre 8.2:** Testcontainers ya entra en 1.7 para los tests de componente; lo que queda aquí es lo que ni esos tests ni el harness en memoria pueden cubrir — el comportamiento de la infraestructura real. `dotnet test` en 8.3 debe correr las dos categorías (`Fast` y `Docker`).

---

## Estimación total

| Fase | Duración estimada |
|---|---|
| 0. Setup | 3-4 días |
| 1. Catalog.API | 1 semana |
| 2. Orders síncrono | 1 semana |
| 3. Mensajería | 1-1.5 semanas |
| 4. Saga + compensación | 1-1.5 semanas |
| 5. Gateway | 3-4 días |
| 6. Frontend Bootstrap | 1-1.5 semanas |
| 7. Observabilidad | 4-5 días |
| **Total núcleo (Fases 0-7)** | **~7-9 semanas** a ritmo de side project (5-10h/semana) |

Los items de test (0.6, 1.7, 2.4, 3.7, 4.7, 5.4) añaden en torno a un 15-20% al tiempo de su fase; los rangos de arriba ya lo absorben. Ese coste se recupera en la Fase 4: depurar una saga sin tests automatizados es republicar mensajes a mano y leer la UI de RabbitMQ.

La Fase 8 es indefinida — extiéndela según lo que quieras profundizar.

---

## Checklist de "señales de que estás aprendiendo de verdad"

- Puedes explicar por qué NO compartes base de datos entre servicios
- Puedes reproducir y explicar un escenario de inconsistencia temporal (ej. el pedido aparece "Pending" en el Frontend mientras el pago aún se procesa)
- Puedes forzar un fallo de pago y ver la compensación liberar el stock sin intervención manual
- Puedes rastrear una request completa en Jaeger sin perderte
- Puedes matar un contenedor de un servicio a mitad de flujo y razonar qué pasa con los mensajes en RabbitMQ
