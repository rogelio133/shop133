# Fase 4.9 — La saga gana `PricingPending` y su primera rama paralela

**Fecha:** 2026-09-07 · **Estado:** completado · [Roadmap](../plan-desarrollo-shop133.md)

---

## Objetivo

Cerrar el circuito que `4.8` dejó abierto.

`4.8` le dio a Catalog.API un consumer de `OrderCreated` que valida la autenticidad de la foto de precios y publica `OrderPricingValidated` / `OrderPricingRejected`. **Nadie los consumía**: sus dos exchanges tenían cero colas ligadas, igual que les pasó a `StockRejected` y `PaymentFailed` entre `3.4` y `4.3`. Mientras eso durara, la validación no tenía ninguna consecuencia — un pedido con un precio inventado se rechazaba *en Catalog* y se cobraba igual en Payments.

Este punto los consume: la saga gana `PricingPending` **antes** de `StockPending` y un rechazo de precio termina el pedido.

**Y desmiente el título de su propio punto en el roadmap**, que es la parte que importa. Ese título dice "`OrderPricingRejected → Cancelled` **sin nada que compensar**". El `///` de [`OrderPricingRejected`](../src/Shared/Shop133.Contracts/Events/OrderPricingRejected.cs) ya avisó en `4.8` de que podía ser falso —deliberadamente no prometió lo que sí promete `StockRejected`— y encargó releerlo con la máquina de estados delante. Releído: **es falso**. Inventory sigue consumiendo `OrderCreated` del mismo exchange fanout, así que la validación de precio y la reserva de stock corren **en paralelo**, y un rechazo de precio puede llegar con el stock ya reservado. Es el mismo movimiento con el que `4.4` corrigió la nota de `4.3` sobre `CompensatingStock`.

Resultado: **`OrderPricingRejected` no lleva nunca a `Cancelled` directamente.** O manda la compensación, o espera a saber si hay algo que compensar.

**Fuera de alcance, deliberadamente:** el reembolso. Este punto destapa un caso en el que el pedido se cobra y luego se cancela por precio falso; la saga suelta el stock, pero no existe ningún contrato de devolución en el proyecto. Se anota, no se inventa.

---

## Decisiones

### 1. La unión (join) se modela **solo con estados**, sin campos nuevos en `OrderState`

Hasta `4.8` esta máquina era una cadena: un evento, una respuesta, un estado. La regla que `4.2` dejó escrita —*"en una saga que observa, hay un estado por cada respuesta que se espera, no por cada hecho que ocurre"*— describe exactamente eso.

Desde `4.9` hay **dos respuestas pendientes a la vez** desde el `Initially`, la de Catalog y la de Inventory, y ninguna causa a la otra. La regla no cambia, se generaliza: **un estado por cada conjunto de respuestas que siguen pendientes**, más lo que ya se sabe del desenlace. De ahí salen cuatro estados nuevos:

|                                  | Catalog | rama Inventory → Payments |
|---|---|---|
| `PricingPending`                 | falta   | sin contestar |
| `PricingPendingStockReserved`    | falta   | reservó |
| `PricingPendingPaymentCompleted` | falta   | reservó **y cobró** |
| `StockPending`                   | validó  | sin contestar |
| `CancellingStockPending`         | **rechazó** | sin contestar |

*Descartado* modelar el join con un `bool StockIsReserved` en `OrderState` y ramificar con `.IfElse(...)`: son menos estados, pero cuesta una migración de `OrdersDb` (columna `bit`) y estrena los primeros condicionales de esta máquina. Con estados no hay nada que decidir en tiempo de ejecución y, sobre todo, **la fila de `OrderStates` dice por sí sola dónde está un pedido en paralelo** — que es exactamente por lo que `CurrentState` es `string` y no `int` (2.1) y por lo que los estados terminales no son `Finalize()` (4.5). Un `bit` en otra columna habría que cruzarlo a mano para leer lo mismo. Tiene test: `ParallelJoin_IsReadableInTheRow`.

**El coste real es cero de esquema**: `CurrentStateMaxLength` es 64 y el nombre más largo mide 30, así que **no hay migración**.

### 2. `CancellingStockPending` existe, y es el estado que el título del roadmap da por innecesario

Cuando el rechazo de precio llega y Inventory no ha contestado, no es que *no haya* nada que compensar: es que **no se sabe todavía**. Las dos alternativas se descartan por motivos concretos:

*Descartado* cancelar ahí mismo e ignorar el `StockReserved` que llegue después: la reserva de Inventory no se soltaría nunca. Es la **regla 7 de CLAUDE.md rota en silencio**, con el pedido perfectamente `Cancelled` y sus unidades apartadas para siempre — el agujero exacto que `4.4` cerró por el otro camino. **Y no lo detectaría ningún assert de estado final**: se comprobó implementando el título al pie de la letra (rotura deliberada 2), y el pedido sigue llegando a `Cancelled` publicando un solo `OrderCancelled`. Lo único que cambia es el recuento de `ReleaseStock`.

*Descartado* mandar `ReleaseStock` a ciegas sin esperar: el `ReleaseStockConsumer` de Inventory **lanza si no encuentra fila de reserva**, y es una decisión deliberada de `4.4` (soltar lo que nunca se apartó crea unidades de la nada). El comando acabaría en `release-stock_error` y la saga esperaría en `CompensatingStock` un `StockReleased` que no va a llegar — cambiar una espera por otra peor.

### 3. `PricingPendingPaymentCompleted` no estaba planificado, y lo impuso una medición

El plan de este punto daba por raro que la rama de Inventory terminara **entera** antes que Catalog: exige que una lectura de Catalog tarde más que dos escrituras encadenadas (Inventory reserva, Payments cobra). Se dejó anotado como caso que se dejaría faultear, confiando en el `UseMessageRetry` de `Program.cs` (5 × 100 ms) para reordenarlo.

**Contra el compose real fue la mitad de los pedidos.** De siete pedidos legítimos, **cuatro** los ganó Inventory y tres Catalog. En una segunda tanda de seis, con el contenedor de Catalog ya caliente, los ganó Catalog los seis — o sea que el resultado depende de lo caliente que esté un contenedor, que es la definición de algo con lo que no se puede contar.

Así que el estado se añade. **La prueba de que el reintento no bastaba es la verificación 5**: con `catalog-api` parado, un pedido se quedó en `PricingPendingPaymentCompleted` durante 12 segundos y terminó bien al arrancar Catalog. Ninguna ventana de reintento de 500 ms cubre eso.

**La regla que sale de aquí, y es lo más transferible del punto:** una inversión **estructural** —dos ramas paralelas que pueden terminar en cualquier orden— se modela con un estado; un reordenamiento de **entrega** dentro de una misma cadena causal se deja al reintento. Ver la decisión 5.

### 4. Los cruces que **no** necesitan estado

No todo cruce del join se gana un estado, y ver por qué es la otra mitad de la regla anterior. Dos casos terminan sin esperar a Catalog, porque el desenlace ya no depende de él:

- **Inventory rechaza** (esté el precio como esté): el pedido está perdido y no hay nada apartado, así que se cancela ya. Es el único camino en el que el "sin nada que compensar" del título acierta — y ni siquiera es el evento que el título nombra.
- **El cobro se rechaza con el precio sin saber**: condena el pedido diga lo que diga Catalog, y hay stock apartado, así que se va directo a `CompensatingStock`.

En los dos casos la respuesta de Catalog llega tarde y se ignora donde caiga. **Eso es seguro por una propiedad concreta de `4.8`**: validar precios **no escribe nada de negocio** —es la razón por la que la idempotencia de `OrderCreatedPricingConsumer` salió distinta de la de los otros cuatro servicios—, así que descartar su respuesta no deja rastro que limpiar.

### 5. El reordenamiento de entrega se deja faultear, y se mide en vez de taparlo

`PaymentCompleted` llegó a la saga estando en `PricingPending`, o sea **antes de que la saga procesara el `StockReserved`**. Y `OrderPricingValidated`, `OrderPricingRejected` y `StockReserved` llegaron alguna vez antes de que la saga procesara el `OrderCreated`, disparando el `OnMissingInstance(Fault())`.

La causa no es que Payments se adelante a Inventory —no puede, consume el `StockReserved` que Inventory publica— sino que **la cola `order-state` se consume con concurrencia > 1**: dos mensajes que llegaron en orden se procesan a la vez y terminan al revés.

**Eso no lo estrena `4.9`.** Existe desde `4.2` con `PaymentCompleted` en `StockPending`, cuyo comentario ya dice que faultear es lo correcto porque *"los agujeros se miden, no se tapan"*. Lo que ha hecho este punto es **destaparlo**, al añadir un salto más al recorrido.

Se deja así, y funciona: las cuatro inversiones medidas se absorbieron por reintento y **`order-state_error` no creció ni un mensaje** en toda la verificación (sus dos mensajes son de una sesión anterior, de un pedido que no es de este punto).

*Descartado* bajar `ConcurrentMessageLimit` a 1 en el endpoint `order-state` de `Program.cs`: lo quitaría del todo, pero serializa la saga entera del servicio para tapar una carrera que el reintento ya cubre, y es literalmente la línea que `4.7` dejó anotada por **esconder** carreras en los tests en vez de mostrarlas.

**Cambia además, por tercera vez, lo que significa `OnMissingInstance(Fault())`.** `4.2` lo puso porque el descarte silencioso hacía desaparecer pedidos; `4.5` reescribió su `///` diciendo que con la saga persistida ya solo señala "un evento de un pedido que nunca existió". Desde `4.9` vuelve a dispararse por una causa rutinaria — la respuesta que adelanta al `OrderCreated` de su propio pedido — y el reintento la resuelve.

### 6. La asimetría del `Reason`: ahora la escriben tres caminos, y uno publica los dos motivos

`OrderState.CancellationReason` lo escribía **un solo camino** (`PaymentFailed`) y su `///` decía exactamente eso. Ahora son tres, con los dos `OrderPricingRejected`. La regla que separa quién lo escribe de quién no **no es qué evento llega**, es si la publicación ocurre en esa misma transición o en una posterior: los `StockRejected` que cancelan directamente leen el motivo del mensaje que entra.

En `CancellingStockPending --StockRejected--> Cancelled` los **dos** motivos son ciertos a la vez (precio falso y sin stock) y se publica el de precio, el que está guardado. *Descartado* concatenarlos: el `///` de `OrderCancelled` promete un texto para que el cliente entienda qué pasó, no un inventario de todo lo que falló.

### 7. `CompensatingStock` pierde una detección, y se dice en voz alta

Desde `4.9` se llega a ese estado por **cinco** caminos con historias distintas, así que sus guardas de idempotencia son la unión de los cinco recorridos — y eso incluye `PaymentCompleted`.

Hasta `4.8` ese evento **no** se ignoraba a propósito: solo se llegaba con un `PaymentFailed`, y un cobro aceptado ahí era Payments contradiciéndose. Ahora hay un camino legítimo que sí pasó por el cobro (`PricingPendingPaymentCompleted --OrderPricingRejected-->`), así que esa contradicción deja de detectarse. *Descartado* partir el estado en dos por procedencia: no ganaría ninguna transición distinta. Es el precio de que un estado tenga varias historias.

### 8. No se añade regla de arquitectura, y se dice por escrito

La suite se queda en **16**. Todas las formas que introduce este punto ya estaban cubiertas —`StateMachineFiles_LiveOnlyIn_OrdersDomain` desde `4.1`, y no entra ningún `.csproj`, paquete ni contrato— y el precedente de `3.3` y `3.5` es decirlo en vez de inventar un filtro que no vigila nada. `3.2` ya avisó de que una regla que nunca coincide pasa en verde para siempre.

---

## Cambios

### `src/` — dos archivos, y uno solo de código

| Archivo | Rol |
|---|---|
| [`Orders.Domain/Sagas/OrderStateMachine.cs`](../src/Services/Orders/Orders.Domain/Sagas/OrderStateMachine.cs) | **Todo el punto.** 4 estados nuevos, 2 `Event<T>` con su `CorrelateById` + `OnMissingInstance(Fault())`, el `Initially` apuntando a `PricingPending`, 4 bloques `During` nuevos y las guardas de idempotencia recalculadas en los 9 estados. |
| [`Orders.Domain/Sagas/OrderState.cs`](../src/Services/Orders/Orders.Domain/Sagas/OrderState.cs) | **Solo comentarios**: el `///` de `CancellationReason` afirmaba que lo escribe un único camino y ahora son tres. |

**Nada más.** Ni contrato (los dos eventos existen desde `4.8`), ni `.csproj`, ni paquete, ni migración, ni `Program.cs`, ni `OrderStateConfiguration`, ni ningún otro servicio. Es la misma forma que tuvo `4.2`.

De 5 estados a **9**: `PricingPending`, `PricingPendingStockReserved`, `PricingPendingPaymentCompleted`, `StockPending`, `CancellingStockPending`, `PaymentPending`, `CompensatingStock`, `Confirmed`, `Cancelled`.

### `tests/`

| Archivo | Rol |
|---|---|
| [`Orders.Tests/OrderStateMachineTests.cs`](../tests/Services/Orders/Orders.Tests/OrderStateMachineTests.cs) | 9 → **18**. Los 9 anteriores adaptados (se pusieron rojos de golpe: `StockReserved` ya no lleva a `PaymentPending`) y 9 nuevos para el join. `AssertNoFaults()` pasa de 6 a 8 `Fault<T>`. |
| [`Orders.Tests/OrderStatePersistenceTests.cs`](../tests/Services/Orders/Orders.Tests/OrderStatePersistenceTests.cs) | 4 → **5**. Los 4 esperaban `StockPending` tras el `Initially`; ahora es `PricingPending`. El nuevo, `ParallelJoin_IsReadableInTheRow`, es la decisión 1 hecha assert. |

`OrderSagaHost` y `OrderSagaDbHost` **no cambian**: registran la saga genéricamente.

`Orders.Tests` pasa de 25 a **35** (18 `Fast` + 17 `Docker`); el repositorio, de 94 a **104**.

---

## Detalles que cuestan tiempo

**Los nueve tests anteriores se ponen rojos de golpe, y eso es la señal buena.** Publicaban `OrderCreated → StockReserved → …` sin pasar por Catalog. Que rompan es lo que demuestra que cubrían de verdad las transiciones; la adaptación es insertarles el `OrderPricingValidated` donde toca.

**Un servicio corriendo bloquea el `.dll` y el `build` falla, pero el test runner sigue corriendo el binario viejo.** `dotnet build tests/...` devolvió `MSB3027 … The file is locked by: "Orders.API (5244)"` y, en la misma línea de comandos, la suite dio **15/15 en verde** — contra el binario anterior. Es la misma clase de trampa que `4.8` anotó con el `bin/` obsoleto de `Catalog.Tests`, pero peor, porque aquí sí hay un error visible y está a diez líneas del resultado verde. Hay que parar los servicios antes de compilar, y leer el resultado del `build` antes que el de los tests.

**`sqlcmd -P $env:MSSQL_SA_PASSWORD` con la variable vacía se come el `-C` siguiente**, y el error acusa a un certificado autofirmado en vez de a la variable. Ya estaba anotado en CLAUDE.md desde `3.6` y volvió a morder. Lo que funciona es leerla del `.env`:

```powershell
$sa = (Select-String -Path .env -Pattern '^MSSQL_SA_PASSWORD=(.*)$').Matches[0].Groups[1].Value
```

**El resultado de la carrera depende de si el contenedor de Catalog está caliente.** Siete pedidos en frío: 4 los ganó Inventory. Seis en caliente: los ganó Catalog los seis. Cualquier conclusión sacada de una sola tanda es falsa, y la primera tanda es la que dice la verdad sobre el peor caso.

**`order-state_error` puede tener mensajes viejos de otra sesión.** Estaba en 2 antes de empezar y en 2 al terminar. Lo que hace falta comprobar no es que esté vacía, es que **no crezca** — y para saber de qué son, la API de gestión los da con un `POST .../get` y `ackmode: ack_requeue_true`, que los deja donde estaban:

```powershell
curl.exe -s -u guest:guest -X POST "http://localhost:15672/api/queues/%2F/order-state_error/get" `
  -H "Content-Type: application/json" --data-binary "@body.json"
```

---

## Verificación

### 1. Los 18 tests `Fast` de la máquina de estados, sin Docker

```
=== TEST EXECUTION SUMMARY ===
   Orders.Tests  Total: 18, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 19.634s
```

### 2. La suite entera de Orders y la de arquitectura

```
   Orders.Tests               Total: 35, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 82.170s
   Shop133.ArchitectureTests  Total: 16, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.620s
```

### 3. Las cuatro roturas deliberadas — ningún test se dio por bueno sin verlo rojo

| Rotura | Test que cae | Lección |
|---|---|---|
| Quitar `Ignore(OrderPricingValidated)` de `StockPending` | `DuplicatePricingValidated_…` | Falla por `Assert.Empty(Published<Fault<…>>())` y **no** por el recuento, que sigue saliendo 1. Trampa 3 de `3.7` confirmada por **quinta** vez. |
| Implementar el título del roadmap al pie de la letra (`PricingPendingStockReserved --OrderPricingRejected--> Cancelled`) | `PricingRejected_WithStockAlreadyReserved_…` | Falla por `Assert.Single(Sent<ReleaseStock>())`. **El pedido sigue llegando a `Cancelled` con un solo `OrderCancelled`**: un test de estado final habría aprobado la fuga de stock. |
| Quitar `Ignore(OrderPricingValidated)` de `Cancelled` | `StockRejected_WithPricingStillPending_…` | Sin esa guarda, **todo** pedido sin stock deja un mensaje en `order-state_error`. Es el caso normal, no el raro. |
| Sustituir `PricingPendingPaymentCompleted` por un `Ignore(PaymentCompleted)` | `HappyPath_WithTheWholeStockAndPaymentBranchBeforePricing_…` | El pedido **nunca se confirma**, en silencio. Sólo lo caza el camino feliz: el de rechazo sigue pasando en verde. |

### 4. Los dos eventos de `4.8` dejan de publicarse al vacío

```
Shop133.Contracts.Events:OrderPricingRejected        -> order-state
Shop133.Contracts.Events:OrderPricingValidated       -> order-state
```

Los doce mensajes del proyecto tienen ya cola ligada. Y la trampa de nombres de `4.6`/`4.8` no se ha reabierto — `order-created` sigue con **un solo** consumidor:

```
order-created                    consumers=1
order-created-pricing            consumers=1
order-state                      consumers=1
```

### 5. El pedido con precio inventado — **el punto entero**

3 unidades del producto 1 a `0.01` (total `0.03`), que en `4.8` llegaban a Payments y se cobraban:

```
stock inicial (OnHand/Reserved): 42/18
POST /orders (precio 0.01) -> id=f9c504b5-… status=Pending total=0.03
GET  /orders -> status=Cancelled
stock final   (OnHand/Reserved): 42/18
```

Y el recorrido, que es donde se ve que el título del roadmap era falso:

```
Saga arrancada …; pasa a PricingPending. Catalog e Inventory están procesando este mismo evento en paralelo.
… Catalog rechazó la foto de precios (el producto 1 (TAZA-001) se pidió a 0.01 y su precio es 249.00);
  pasa a CancellingStockPending. El pedido NO se cancela hasta saber si Inventory llegó a reservar algo.
… Inventory había reservado stock para un pedido cuyo precio Catalog ya rechazó;
  pasa a CompensatingStock y se envía ReleaseStock a queue:release-stock.
… stock liberado por Inventory; pasa a Cancelled y se publica OrderCancelled (…). La compensación está completa.
… cancelado en OrdersDb (…); su estado pasa de Pending a Cancelled.
```

Inventory confirma las dos mitades y Notifications manda el aviso con el motivo dentro:

```
Stock reservado para el pedido f9c504b5-…: 1 línea(s) por un importe de 0.03.
Stock liberado para el pedido f9c504b5-…: 1 línea(s) devuelta(s) al inventario.
Email enviado a cliente@shop133.test | Asunto: Tu pedido … no se ha podido completar
  | Motivo: el producto 1 (TAZA-001) se pidió a 0.01 y su precio es 249.00
```

**Había algo que compensar.** El pedido pasó por `CancellingStockPending` y por `CompensatingStock`, y mandó un `ReleaseStock`.

### 6. La carrera, medida en las dos direcciones

Primera tanda (contenedor de Catalog frío), 7 pedidos:

```
  Catalog ganó (-> StockPending)                     : 3
  Inventory ganó (-> PricingPendingStockReserved)    : 4
```

Segunda tanda (caliente), 6 pedidos: los 6 los ganó Catalog. Los seis acabaron `Confirmed`.

Faults absorbidos por el `UseMessageRetry`, con `order-state_error` **sin crecer**:

```
   1 … OrderPricingRejected:  An existing saga instance was not found
   1 … OrderPricingValidated: An existing saga instance was not found
   1 … StockReserved:         An existing saga instance was not found
   1 … PaymentCompleted:      Not accepted in state PricingPending
```

Los dos mensajes que hay en `order-state_error` son de una sesión anterior (pedido `2fb2aa74`), no de este punto.

### 7. Con Catalog caído: un retraso, no un `502` — y el estado que lo demuestra

```
catalog-api parado.
HTTP=201 t=0.015197s
GET /orders -> status=Pending  (esperando a Catalog)
OrderStates.CurrentState = PricingPendingPaymentCompleted
```

El `POST` contesta `201` en **15 ms**, y el pedido se queda esperando con el stock reservado y **el cobro ya aceptado** — o sea en el estado que la decisión 3 añadió. Ninguna ventana de reintento de 500 ms cubre un servicio parado: sin ese estado, este pedido acaba en `order-state_error`.

Al arrancar Catalog, termina solo:

```
catalog-api arrancado; esperando...
GET /orders/c44431fc -> status=Confirmed
OrderStates.CurrentState = Confirmed
order-state_error: 2   (sin crecer)
```

---

## Pendiente

**El reembolso no existe, y este punto es quien lo destapa.** Si Catalog rechaza el precio después de que Payments haya cobrado, la saga suelta el stock y cancela, pero **el cobro no se devuelve**: no hay ningún contrato de devolución en `Shop133.Contracts`, y el `TransactionId` que `PaymentCompleted` lleva desde `0.3` *"para poder emitir el reembolso"* sigue sin nadie que lo use. El caso sale por el log con un `LogError` y todas las letras en vez de desaparecer. Inventar un `RefundPayment` sería el decimotercer contrato, un consumer nuevo en Payments y una decisión de negocio que este punto no tiene por qué tomar. **Sin dueño en el roadmap.**

**Ni `PricingPending` ni `CancellingStockPending` tienen plazo.** Si Catalog no contesta nunca, el pedido se queda ahí para siempre. Son el tercer y cuarto hueco de esta clase, junto al de `CompensatingStock` (`4.4`) y al de `OnMissingInstance`. No hay `Schedule` ni `Request` con timeout en el proyecto. **Sin dueño en el roadmap.**

**La validación de precios no llega a ser una puerta, y conviene no confundirlo.** Payments consume `StockReserved` del fanout por su cuenta (`3.5`) y cobra sin esperar a esta saga ni a Catalog. Lo que `4.9` garantiza no es que no se cobre —eso exigiría cambiar el consumer de Payments, que es justo lo que la decisión 2 de `4.1` descartó— sino que **el pedido acaba cancelado y el stock devuelto**. Un veto con compensación, no una autorización previa.

**El reordenamiento por concurrencia en `order-state` sigue abierto y sin dueño**, cubierto de hecho por el `UseMessageRetry`. Es el hueco que `4.2` anotó y que este punto ha medido por primera vez. `8.2`, que pide *"persistencia de la Saga en SQL Server con concurrencia optimista"*, es su sitio natural: forzar el choque necesita justo dos entregas simultáneas.

**Fase 4 queda completa en código.** Falta la ceremonia de git: PR a `develop`, PR a `main` y la etiqueta anotada `fase-4`.
