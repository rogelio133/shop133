# Fase 4.7 — Los cuatro escenarios obligatorios contra `OrderStateMachine`

**Fecha:** 2026-09-03 · **Estado:** completado · **Roadmap:** [punto 4.7](../plan-desarrollo-shop133.md)

---

## Objetivo

Automatizar los **cuatro escenarios obligatorios** que el roadmap enumera y llama, con todas
las letras, *"la especificación del punto 4.7"*: compra exitosa, sin stock, stock reservado con
pago rechazado (la compensación) y evento duplicado.

El punto existe porque la saga —el núcleo pedagógico del proyecto— **no tenía ni un test**.
`4.1`, `4.2`, `4.3`, `4.4` y `4.5` se verificaron **a mano** contra el compose real: cinco
puntos seguidos cuya comprobación no sobrevive a un refactor, y cuatro de ellos dejaron la
misma línea escrita en su sección *Pendiente*. `4.5` fue más lejos y midió el daño: sus nueve
verificaciones eran manuales y **los 71 tests pasaban exactamente igual con el código de
`4.4`**, porque `OrdersApiFactory` borra todo MassTransit y con él la saga, el repositorio EF y
el outbox.

Se entregan **13 tests nuevos** repartidos en dos clases: los escenarios contra la máquina de
estados (`Category=Fast`, la primera suite de servicio del repositorio que no necesita Docker)
y la persistencia de `4.5` (`Category=Docker`). El repositorio pasa de **71 a 84**.

**Fuera de alcance a propósito:**

- **El outbox transaccional.** Registrarlo convertiría cada `Publish` en una fila que un
  servicio de sondeo vacía más tarde, así que `InactivityTask` dejaría de medir el final del
  trabajo y la suite pasaría a depender de un intervalo de polling. Es de `8.2`.
- **El choque real de concurrencia optimista.** La sección *Pendiente* de
  [fase_4_5.md](fase_4_5.md) lo dejó apuntado a este punto **o** a `8.2`; necesita dos entregas
  simultáneas del mismo pedido, que es exactamente lo contrario del `ConcurrentMessageLimit = 1`
  que hace deterministas todas las suites del repositorio desde `3.7`. Va a `8.2`, con la
  infraestructura real.
- **Que `Program.cs` registre de verdad la saga y los consumers.** Ningún test de este punto
  levanta Orders.API. Hueco heredado de `3.7` y de `8.2`.
- **Los tests de Notifications.** `4.6` los dejó sin dueño y este punto no los recoge: `4.7` es
  la máquina de estados, no aquel servicio.
- **`4.8`/`4.9`.** La validación de precios de Catalog no existe todavía; cuando llegue,
  `PricingPending` trae sus propios casos.

---

## Decisiones

### 1. Dos suites y dos repositorios de saga, no una

El roadmap pide el harness contra la máquina de estados; `CLAUDE.md` pide además cubrir `4.5`,
del que dice que *"es el primer trabajo de 4.7"*. Son dos preguntas distintas y se responden
por separado:

- **`OrderStateMachineTests`** (`Fast`, 9 tests) usa `InMemoryRepository()` y **no toca SQL
  Server**. Lo que prueba es un *proceso*: qué transición dispara cada evento y qué mensaje
  sale. Eso no necesita tabla.
- **`OrderStatePersistenceTests`** (`Docker`, 4 tests) usa el `EntityFrameworkRepository` que
  registra el `Program.cs` desde `4.5`, contra Testcontainers.

*Descartado* usar solo el repositorio EF, que habría sido más coherente con el resto del
repositorio (ninguna suite de servicio era `Fast`). Obligaría a levantar SQL Server para
comprobar transiciones que no lo tocan, y renunciaría a lo único que el harness prometía desde
`3.7` y nunca se había cobrado: **una suite de milisegundos**.

*Descartado* usar solo `InMemoryRepository()`, que es lo que el título del punto pide al pie de
la letra. Dejaría `4.5` sin un solo test hasta la Fase 8, que es justo lo que este punto venía a
arreglar.

Las transiciones **no se repiten** en la suite `Docker`: son idénticas con los dos
repositorios, porque `4.5` no cambió ni una línea de la máquina de estados. Duplicarlas costaría
SQL Server para no afirmar nada nuevo.

### 2. `OrdersApiFactory` no se toca, y eso contradice la letra de cuatro documentos

`4.1`, `4.2`, `4.3` y `4.5` repitieron la misma frase en su *Pendiente*: *"`OrdersApiFactory`
tendrá que dejar de desmontar la saga junto con el resto de MassTransit"*. **No se hace**, y
conviene decir por qué en vez de dejarlo notado como si se hubiera olvidado.

Devolverle la saga a esa fábrica significa reescribir dentro del test el `AddMassTransit`
entero de Orders.API —outbox, repositorio EF, `AddConfigureEndpointsCallback` con su retry, la
saga y los dos consumers— para probar una clase que no depende de ninguna de esas cosas. El
resultado sería un test más frágil y menos legible que un host dedicado.

Es el mismo criterio con el que la decisión 3 de [fase_3_7.md](fase_3_7.md) dejó a
`Inventory.Tests` y `Payments.Tests` sin `WebApplicationFactory`. **El fin se cumple —la saga
queda cubierta— por otro medio.** El precio es el que ya estaba aceptado por escrito: nada
comprueba que `Program.cs` registre lo que dice registrar, y ese hueco es de `8.2`.

### 3. Un consumer espía para cerrar el escenario 3

El escenario 3 tiene que afirmar que sale **exactamente un** `ReleaseStock` *y* que el estado
final es `Cancelled`. Lo segundo necesita que alguien conteste `StockReleased`, porque desde
`4.4` la saga no cancela hasta que Inventory responde.

`ReleaseStockSpyConsumer` hace de Inventory: recibe el comando y publica la respuesta. Va
registrado con **nombre de endpoint explícito** (`.Endpoint(e => e.Name = "release-stock")`), y
esa línea es carga estructural: el formatter kebab derivaría `release-stock-spy` del nombre del
tipo, y la saga manda su comando a la URI literal `queue:release-stock`. Con el nombre por
convención, el `Send` llegaría a una cola que nadie lee — **sin error y sin aviso**, que es
exactamente el modo de fallo del que avisa el `///` de `InventoryReleaseStockEndpoint`.
Escrita así, la línea ata ese destino desde el lado de **Orders**, igual que
`ReleaseStockConsumerTests` lo ata desde el de Inventory.

*Descartado* publicar el `StockReleased` desde el propio test tras comprobar que la saga llegó
a `CompensatingStock`: serían dos etapas de bus, o sea el fallo que midió la decisión 8 de
[fase_4_4.md](fase_4_4.md) — y con el `InactivityTask` gastado solo se puede afirmar *"al menos
uno"*, nunca *"exactamente uno"*.

*Descartado* referenciar el `ReleaseStockConsumer` de verdad: haría que `Orders.Tests`
referenciara otro servicio y necesitara `InventoryDb` para probar una máquina de estados que no
sabe nada de stock.

### 4. Una sola etapa de bus por test, y el test se autocomprueba

Una saga es multi-etapa por naturaleza (`OrderCreated → StockReserved → PaymentCompleted`), y
`harness.InactivityTask` es **una sola tarea** que se completa la primera vez que el bus queda
ocioso. El remedio de `4.4` —sembrar el estado previo por base de datos— no se puede trasladar:
aquí la secuencia *es* lo que se prueba.

Lo que se hace: **publicar todos los eventos seguidos y esperar una sola vez al final**. Los
seis eventos de la saga entran por el mismo endpoint (`order-state`) con
`ConcurrentMessageLimit = 1`, así que la cola es FIFO y el orden de publicación es el de
consumo.

Lo que hace aceptable depender de ese orden es que **un desorden no puede pasar inadvertido**:
un `StockReserved` sin instancia dispara el `OnMissingInstance`, y un `PaymentCompleted` en
`StockPending` no está aceptado. Los dos producen un fault, y **todos** los tests llevan
`AssertNoFaults()`.

*Descartado* `SagaHarness.Exists(orderId, m => m.PaymentPending)` entre etapas: ordena bien,
pero no des-gasta el `InactivityTask`.

En la suite `Docker` esto no vale, porque sus tests necesitan leer la fila **entre** tandas —
que es justo lo que vienen a comprobar. Allí se espera con `WaitForStateAsync`, que **sondea la
tabla**: la fuente de verdad de esa suite es la base, así que se le pregunta a la base. No gasta
el `InactivityTask` y falla diciendo en qué estado se quedó. Ver *Detalles*: se descubrió
estrellándose.

### 5. La idempotencia de la saga se prueba con `MessageId` **distintos**

Es lo contrario de lo que hacen `Inventory.Tests` y `Payments.Tests`, y no es un despiste. La
guarda de la saga son los `Ignore(...)`, que reconocen **el mismo pedido**, no la misma entrega
— la mitad de *negocio* de la guarda de `3.6`, no la de transporte. Un `MessageId` repetido
sería una reentrega, que en el harness no la para nadie (no hay inbox) y además colapsaría las
dos entradas de `harness.Consumed` en una (trampa 2 de `3.7`), dejando al test sin poder
demostrar que la saga llegó a ver los dos mensajes.

Por eso el test afirma primero `Consumed<OrderCreated>().Count == 2` y solo después
`Assert.Single(Published<OrderConfirmed>())`: sin lo primero, podría estar aprobando porque el
duplicado se perdió por el camino.

### 6. Cobertura más allá de los cuatro escenarios: solo `OnMissingInstance`

Se añade un test para `OnMissingInstance(m => m.Fault())` porque **el comportamiento por
defecto de MassTransit 8 es descartar el mensaje en silencio** —sin excepción, sin cola de
error y sin una línea de log—, y eso se midió en la verificación 7 de [fase_4_2.md](fase_4_2.md)
creyendo lo contrario. Esas dos líneas por evento existen solo para evitarlo, y borrarlas no
rompe ninguna compilación.

*Descartados por ahora* dos tests que se consideraron: la entrega fuera de orden
(`PaymentCompleted` en `StockPending` faultea en vez de ignorarse) y las guardas de los estados
terminales por separado. El segundo queda cubierto de refilón —el test de duplicados mete un
`PaymentCompleted` repetido estando ya en `Confirmed`, y se comprobó que cae al borrar los seis
`Ignore`—; el primero sigue sin test.

### 7. `SetTestTimeouts`, y es lo que hace que la suite `Fast` sea rápida de verdad

No estaba previsto y salió midiendo: con el valor por defecto los 9 tests tardaban **23,1 s**.
El coste no era el trabajo —el transporte en memoria resuelve la saga entera en menos de un
milisegundo— sino la ventana de silencio que `InactivityTask` espera antes de darse por
satisfecha.

Se fija en **500 ms** y no en los 200 ms con los que se midió (3,9 s). El margen sobra tres
órdenes de magnitud sobre el trabajo real, y el modo de fallo de quedarse corto es el peor que
hay: el `await` vuelve con mensajes en vuelo y un `Assert.Empty` pasa **por no haber llegado a
ocurrir nada**. Lo que hace que un descuido ahí no pase inadvertido es que todos los tests
llevan además una afirmación positiva —el estado alcanzado, el evento que salió—, y ésas no
pueden pasar sin que el trabajo termine.

### 8. Ni un paquete, ni un `.csproj`, ni una línea de `src/`

`AddMassTransitTestHarness` e `ITestHarness` llegan por la vía transitiva
`Orders.API → MassTransit.RabbitMQ → MassTransit` (decisión 2 de `3.7`), y
`OrderStateMachine`/`OrderState`/`OrdersDbContext` por `Orders.API → Orders.Infrastructure →
Orders.Domain`. **La suite de arquitectura se queda en 16 y no se añade regla**, dicho por
escrito — precedente de `3.3` y `3.5`: inventar una que no matchea nunca es peor que no tenerla.

---

## Cambios

### `tests/Services/Orders/Orders.Tests/` — cinco archivos nuevos

| Archivo | Rol |
|---|---|
| `Infrastructure/OrderSagaHost.cs` | `ServiceCollection` + harness en memoria con la saga y `InMemoryRepository()`. Sin base de datos y sin `WebApplicationFactory`. Expone `Harness`, `Spy`, `Instance(orderId)` y `State(orderId)`. |
| `Infrastructure/ReleaseStockSpyConsumer.cs` | El doble de Inventory: consume `ReleaseStock` y publica `StockReleased`. Incluye `ReleaseStockSpySwitch`, el interruptor que lo hace callar. |
| `OrderStateMachineTests.cs` | Los cuatro escenarios obligatorios + `OnMissingInstance`. **9 tests, `Category=Fast`, sin `[Collection]`** — no debe tocar el `SqlServerContainerFixture`. |
| `Infrastructure/OrderSagaDbHost.cs` | El mismo montaje con el `EntityFrameworkRepository` de `4.5` contra Testcontainers. Sabe reconstruir su bus sobre la misma base (`RestartBusAsync`) y esperar sondeando la tabla (`WaitForStateAsync`). |
| `OrderStatePersistenceTests.cs` | La persistencia de `4.5`. **4 tests, `Category=Docker`**, en la collection `orders-api`. |

**Ningún archivo de `src/` modificado, ningún `.csproj` tocado, ningún paquete añadido.** La
máquina de estados se rompió a propósito seis veces durante la verificación y se restauró byte
a byte (`git diff` vacío).

---

## Detalles que cuestan tiempo

**`LogContext` de MassTransit es estático, y por eso reiniciar el bus revienta.**
`RestartBusAsync` falló con `ObjectDisposedException: 'LoggerFactory'` al arrancar el segundo
bus. La causa no está en el test: el primer bus deja su `ILoggerFactory` en el `LogContext`
estático de MassTransit al arrancar, y al destruir el proveedor esa fábrica muere **sin que
nadie limpie el estático**. El segundo bus la reutiliza al construirse —
`BaseHostConfiguration.set_LogContext` → `BusLogContext.CreateLogContext` → excepción. El
arreglo es registrar `NullLoggerFactory.Instance`, un singleton compartido cuyo `Dispose()` no
hace nada, así que ningún proveedor puede dejarlo inservible para el siguiente. El precio es
que los `LogInformation` de la saga no salen en esa suite; `OrderStateMachineTests` conserva el
logging de verdad.

**La trampa 1 de `3.7` volvió a morder, y esta vez en la suite `Docker`.**
`EachTransition_AdvancesTheRowVersion` falló con `Expected: "PaymentPending" / Actual:
"StockPending"`: el segundo `await SettleAsync()` volvió al instante con el `StockReserved`
todavía en vuelo. Es la tercera vez que este proyecto la pisa (3.7, 4.4 y aquí) y la primera en
la que el remedio no es "una sola etapa": lo que la desactiva aquí es esperar sondeando **la
tabla**, no el bus.

**El formatter kebab habría desviado el comando de compensación en silencio.** El espía se llama
`ReleaseStockSpyConsumer`, así que por convención su cola sería `release-stock-spy` y el `Send`
de la saga a `queue:release-stock` acabaría en una cola que nadie lee — sin error, sin fault y
sin log. Se comprobó al revés, renombrando el destino de la saga: **dos tests caen**, los dos
con `Assert.Single() Failure: The collection was empty`.

**PowerShell 5.1 destroza los acentos de estos archivos.** Un
`Get-Content -Raw | ... | Set-Content -Encoding utf8` sobre `OrderStateMachine.cs` —para
romperlo a propósito— convirtió todos los `ú`, `ó` y `—` en mojibake, porque `Get-Content` lo
leyó como ANSI. El archivo se recuperó del backup hecho con `Copy-Item`. **Para editar código
del repositorio, nunca la tubería de PowerShell**; y antes de romper algo a propósito, un
`Copy-Item` y un `git diff --stat` al terminar.

**Smart App Control no se disparó ni una vez** en todo el punto, pese a ~15 recompilaciones de
`Orders.Domain` y `Orders.Tests`, que es la combinación que lo activó en `1.7`, `3.5`, `3.7` y
`4.4`. La escalada documentada sigue en pie; simplemente no hizo falta.

---

## Verificación

### 1. Compilación

```
> dotnet build tests\Services\Orders\Orders.Tests\Orders.Tests.csproj
Build succeeded.
    2 Warning(s)
    0 Error(s)
```

Los dos warnings son los `xUnit1051` de `CreateOrderTests.cs` que arrastra el repositorio desde
`3.7`; ningún archivo nuevo añade ninguno.

### 2. La suite `Fast`, sin Docker

```
> dotnet tests\Services\Orders\Orders.Tests\bin\Debug\net10.0\Orders.Tests.dll -trait "Category=Fast"
   Orders.Tests  Total: 9, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 10.194s
```

Con el `testInactivityTimeout` por defecto ese mismo comando daba **23,121 s**; con 200 ms,
**3,888 s**. Se elige 500 ms — ver la decisión 7.

Estabilidad, cuatro ejecuciones seguidas antes de fijar el valor: 4,161 s / 3,681 s / 3,690 s /
4,176 s, 9 de 9 en verde las cuatro veces.

### 3. La suite `Docker` de persistencia

```
> dotnet tests\...\Orders.Tests.dll -class "Orders.Tests.OrderStatePersistenceTests"
   Orders.Tests  Total: 4, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 26.611s
```

### 4. Romper la máquina de estados a propósito — seis intentos

Ningún test se dio por bueno sin verlo en rojo primero. `src/` se restauró después de cada uno
y el `git diff --stat` final sale vacío.

**4a. Quitar `Ignore(OrderCreated)` de `During(StockPending, …)`**

```
    Orders.Tests.OrderStateMachineTests.DuplicateEvents_ProduceASingleOrderConfirmedAndNoFaults [FAIL]
      Assert.Empty() Failure: Collection was not empty
   Orders.Tests  Total: 9, Errors: 0, Failed: 1
```

**Y falla en el `Assert.Empty` de los faults, no en el recuento de `OrderConfirmed`** — que
sigue saliendo 1. Es la trampa 3 de `3.7` confirmada sobre la saga: contar eventos de negocio no
distingue *se descartó* de *reventó*.

**4b. Quitar los seis `Ignore` de `During(Confirmed, …)`** — el bloque que parece código muerto:

```
    Orders.Tests.OrderStateMachineTests.DuplicateEvents_ProduceASingleOrderConfirmedAndNoFaults [FAIL]
      Assert.Empty() Failure: Collection was not empty
```

**4c. Quitar `OnMissingInstance(missing => missing.Fault())` de `StockReserved`**

```
    Orders.Tests.OrderStateMachineTests.EventForAnOrderThatNeverExisted_Faults [FAIL]
      Assert.Single() Failure: The collection was empty
```

El mensaje desaparece sin dejar rastro, que es exactamente lo que midió `4.2`.

**4d. Renombrar el destino a `queue:release-stock-renombrada`**

```
    ...PaymentFailed_OrderCancelledCarriesTheReasonSavedInTheInstance [FAIL]
      Assert.Single() Failure: The collection was empty
    ...PaymentFailed_SendsExactlyOneReleaseStockAndEndsCancelled [FAIL]
      Assert.Single() Failure: The collection was empty
   Orders.Tests  Total: 9, Errors: 0, Failed: 2
```

**4e. Invertir `TransitionTo(CompensatingStock)` y `.Send(...)` — NO REPRODUCE.**

Cinco ejecuciones seguidas, 9 de 9 en verde las cinco veces (10,4 s / 9,2 s / 9,9 s / 9,4 s /
9,8 s). El `///` de esa transición avisa de que con el `Send` primero la respuesta de Inventory
podría llegar con la instancia todavía en `PaymentPending` *"una de cada tantas veces"*, y esta
suite **no lo detecta**.

El motivo es que lo que ordena los mensajes es el `ConcurrentMessageLimit = 1` del endpoint
`order-state`: mientras la transición del `PaymentFailed` se está ejecutando, el
`StockReleased` que provoca no puede consumirse. Es decir, **la misma línea que hace
deterministas estas suites es la que esconde esta carrera**. Queda anotado en *Pendiente* en vez
de bajarse el límite, que volvería intermitentes los otros ocho tests para cubrir un caso.

**4f. Volver a `InMemoryRepository()` en el host `Docker`** — o sea, el código de `4.4`:

```
    Orders.Tests.OrderStatePersistenceTests.SagaStarted_WritesTheInstanceInOrderStates [FAIL]
    Orders.Tests.OrderStatePersistenceTests.AfterRestartingTheBus_TheSagaResumesFromTheStoredRow [FAIL]
    Orders.Tests.OrderStatePersistenceTests.EachTransition_AdvancesTheRowVersion [FAIL]
    Orders.Tests.OrderStatePersistenceTests.TerminalSaga_KeepsItsRow [FAIL]
   Orders.Tests  Total: 4, Errors: 0, Failed: 4
```

Los cuatro. Es la respuesta al *"nada de `4.5` está cubierto por un test"*.

### 5. `Orders.Tests` completa

```
> dotnet tests\Services\Orders\Orders.Tests\bin\Debug\net10.0\Orders.Tests.dll
   Orders.Tests  Total: 25, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 73.285s
```

De 12 a 25: 9 `Fast` y 4 `Docker` nuevos.

### 6. Regresión — las otras cuatro suites

```
   Shop133.ArchitectureTests  Total: 16, Errors: 0, Failed: 0, ... Time: 0.695s
   Catalog.Tests              Total: 19, Errors: 0, Failed: 0, ... Time: 79.527s
   Inventory.Tests            Total: 15, Errors: 0, Failed: 0, ... Time: 101.214s
   Payments.Tests             Total:  9, Errors: 0, Failed: 0, ... Time: 60.822s
```

**Total del repositorio: 84 tests** — 25 `Fast` (16 de arquitectura + 9 de la saga) y 59
`Docker`. La suite de arquitectura se queda en 16, como anunciaba la decisión 8.

### 7. `src/` sin tocar

```
> git status --porcelain
?? tests/Services/Orders/Orders.Tests/Infrastructure/OrderSagaDbHost.cs
?? tests/Services/Orders/Orders.Tests/Infrastructure/OrderSagaHost.cs
?? tests/Services/Orders/Orders.Tests/Infrastructure/ReleaseStockSpyConsumer.cs
?? tests/Services/Orders/Orders.Tests/OrderStateMachineTests.cs
?? tests/Services/Orders/Orders.Tests/OrderStatePersistenceTests.cs
```

Cinco archivos nuevos y nada modificado, que era la intención: el punto prueba lo que hay, no
lo cambia.

---

## Pendiente

- **La carrera del orden `TransitionTo`/`Send` no tiene test** — verificación 4e. El
  `ConcurrentMessageLimit = 1` que hace deterministas las suites es lo que la esconde.
  Cubrirla necesita decidir antes qué comportamiento se quiere con entrega concurrente, que es
  la misma pregunta sin dueño que `3.6` dejó abierta sobre `StockItem`. Candidato natural:
  `8.2`.
- **El outbox transaccional y el choque de concurrencia optimista siguen sin test** — `8.2`,
  por los motivos de la sección *Objetivo*. El `UseMessageRetry` de `4.5` sigue siendo una
  defensa razonada y no medida.
- **Nada comprueba que el `Program.cs` de Orders.API registre la saga ni los consumers.** Hueco
  heredado de `3.7`; es de `8.2`.
- **Notifications.API sigue sin un solo test y sin punto que lo recoja.** `4.6` lo dejó
  anotado como el hueco más grande que abría, y este punto no lo cierra: es la máquina de
  estados, no aquel servicio. El patrón a copiar está descrito en la sección *Pendiente* de
  [fase_4_6.md](fase_4_6.md).
- **La entrega fuera de orden no tiene test** (un `PaymentCompleted` en `StockPending` debe
  faultear, no ignorarse). Decisión 6: se consideró y se dejó fuera.
- **`dotnet test` sigue roto** desde que el SDK pasó a 10.0.400 — todas las ejecuciones de
  arriba son del `.dll` con `dotnet`. Es de `8.3`.
- **Los dos `xUnit1051` de `CreateOrderTests.cs`** siguen ahí desde `3.7`.
