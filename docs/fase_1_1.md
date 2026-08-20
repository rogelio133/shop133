# Fase 1.1 — Modelo `Product`

**Fecha:** 2026-08-18 · **Estado:** completado · **Roadmap:** [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md)

> **Revisado el 2026-08-18, después del primer commit del punto.** El `Id` pasó de `Guid` a `int` y se añadió la columna `Sku`. La decisión 2 y la 9 cuentan el porqué; el resto del documento es el original. El cambio también toca `Shop133.Contracts`, así que hay una nota en [fase_0_3.md](fase_0_3.md).

---

## Objetivo

Escribir **la primera pieza de código de negocio del proyecto**. Hasta aquí todo era andamiaje: solución, compose, contratos, logins de SQL Server, reglas ejecutables. `Catalog.Infrastructure` no tenía un solo `.cs` propio.

`Product` es la entidad sobre la que se monta el resto de la Fase 1 — 1.2 la mapea a `CatalogDb`, 1.3 la expone por HTTP, 1.4 la siembra y 1.7 la prueba contra SQL Server real. Se entrega **la entidad y nada más**: un tipo, sus invariantes y su constructor.

Está en primer lugar de la fase porque las decisiones que fija (tipo del id, quién lo genera, qué es válido) las hereda todo lo demás. Cambiar el tipo del id en 1.3, con migraciones ya aplicadas y endpoints escritos, es rehacer la fase.

Esa frase se puso a prueba de inmediato: el `Id` **sí** cambió de `Guid` a `int` (decisión 2), y salió barato precisamente por estar todavía en 1.1, sin EF Core ni endpoints ni un solo consumidor del contrato. Es el mismo argumento por el que 0.3 está en la Fase 0 y no en la 3.

**Fuera de alcance deliberadamente:**

- **EF Core.** El `.csproj` sigue sin un solo `PackageReference`. `CatalogDbContext`, la configuración de columnas y las migraciones son 1.2.
- **Métodos de mutación** (`Update`, `SetStock`, …). La entidad se construye y no cambia. El `PUT` de 1.3 es quien sabrá qué se puede modificar y qué no; añadir un `Update` ahora sería inventarle la firma antes de tener el caso de uso.
- **DTOs y validación de entrada.** 1.3.
- **Seed.** 1.4.

---

## Decisiones

### 1. La entidad vive en `Catalog.Infrastructure`, no en un `Catalog.Domain` nuevo

**Descartado:** crear `Catalog.Domain` y poner ahí la entidad, por simetría con Orders — que sí tiene `Orders.Domain`.

**Elegido:** `Catalog.Infrastructure/Entities/Product.cs`. El layout objetivo de [CLAUDE.md](../CLAUDE.md) le da a Catalog exactamente dos proyectos, `.API` y `.Infrastructure`, y añadir uno fuera de esa lista requiere permiso explícito.

La asimetría con Orders no es un descuido del layout: `Orders.Domain` existe porque ahí vive la `OrderStateMachine` de la Fase 4, que es lógica de verdad y que la regla 5 mantiene aislada de la persistencia. Catalog es un CRUD. Un proyecto de dominio con una clase de datos dentro serían tres capas para mover un `nvarchar` de la base al JSON.

### 2. `int` para el `Id` — revirtiendo lo que dejó escrito 0.3, y por qué

**Este punto se escribió primero con `Guid`.** La versión original decía que el tipo "venía dado" por la decisión 4 de [fase_0_3.md](fase_0_3.md), que fijó `Guid` para `OrderId` **y** `ProductId`. Al revisarlo salió que ese documento justifica los dos ids con **un solo argumento**, y que el argumento solo vale para uno.

El argumento de 0.3 es: *el productor genera el identificador sin consultar a nadie*, cosa que un `IDENTITY` no permite porque el id no existe hasta el `INSERT`.

- Para **`OrderId`** es correcto y decisivo. Es la clave de correlación de la saga (decisión 5 de 0.3): Orders.API la necesita antes de tocar la base para poder publicar `OrderCreated` y arrancar la máquina de estados. Con `IDENTITY` habría que hacer `INSERT` → leer el id → publicar, metiendo la base de datos en el camino crítico de un flujo que existe para ser asíncrono. **`OrderId` sigue siendo `Guid`.**
- Para **`ProductId`** no aplica. Catalog es un CRUD, el `POST /products` de 1.3 es síncrono, y `CatalogDb` es el único escritor. Nadie necesita el id antes de guardar. El argumento se heredó por arrastre, no porque se hubiera comprobado que valía aquí.

Y hay un segundo motivo, este medido en el propio punto: **el `Guid` estaba pagando su coste sin cobrar la ventaja.** La defensa habitual de un `Guid` como PK en SQL Server es "usa v7 y el índice clustered deja de fragmentarse". Eso es falso — SQL Server compara `uniqueidentifier` empezando por los **últimos** 6 bytes, donde v7 pone la parte aleatoria (ver *Detalles que cuestan tiempo*, que se conserva íntegro porque es el hallazgo que provocó esta reversión). O sea: 16 bytes por fila y fragmentación de inserción, a cambio de una ordenación que solo existe del lado de .NET.

**Descartado — mantener `Guid.CreateVersion7()`.** Es lo que ya estaba escrito y commiteado, y cambiar de criterio cuesta un documento como este. Se descarta porque el coste de cambiarlo **ahora es cero**: `Shop133.Contracts` no tiene un solo consumidor, y el propio 0.3 se colocó en la Fase 0 con ese razonamiento — *"discutir la forma de `OrderCreated` ahora, con los archivos vacíos, cuesta una tarde"*. En 1.2, con migraciones aplicadas, ya no lo sería.

**Descartado — `NEWSEQUENTIALID()` como default de columna.** Era la alternativa cuando el tipo era `Guid`: genera valores que SQL Server sí ordena de forma creciente. Con `int` sobra, pero se deja anotada porque explica el terreno: existe precisamente porque el orden de v7 no le sirve a SQL Server.

**Descartado — `Guid.NewGuid()`.** Aleatorio: ni ordena en .NET ni en SQL Server. Era estrictamente peor que v7 y no costaba menos.

**Descartado — `string Sku` como identificador en los mensajes.** Con el `Id` en `int` aparece la duda razonable: el `int` depende de la secuencia `IDENTITY` de `CatalogDb`, así que un restore o un re-seed reasigna números y las referencias guardadas en `InventoryDb` y `OrdersDb` apuntarían en silencio a otro producto. El `Sku` no tiene ese problema. Se descarta igualmente porque **una línea de pedido debe apuntar a algo inmutable**, y un código de producto se corrige y se renumera en la vida real; un `Sku` corregido dejaría pedidos históricos apuntando a un código muerto. Se mitiga por el otro lado: el seed de 1.4 fija ids explícitos, así que la reconstrucción es determinista. Descartado también llevar **los dos** en `OrderLine`: son dos fuentes de verdad para la misma referencia, que es lo que la decisión 5 de 0.3 rechazó con `CorrelationId`.

**Elegido — `int` con `IDENTITY`**, asignado por SQL Server en el `INSERT` (1.2). La entidad no lo toca: `Id` vale `0` hasta que se guarda.

**Lo que queda es una asimetría deliberada, y es mejor que la uniformidad:** en este sistema `OrderId` es `Guid` y `ProductId` es `int`. Eso enseña la regla de verdad — **el tipo del id lo decide quién lo acuña y cuándo** — en lugar de "usa siempre `Guid`", que es la conclusión que uno se lleva cuando todos los ids son iguales y nadie recuerda por qué.

### 3. `sealed class` con setters privados, no un `record`

**Descartado:** `public sealed record Product` con propiedades `init`, por coherencia con los 10 tipos de `Shop133.Contracts`.

**Elegido:** una clase mutable con los setters privados. La distinción es la que separa este archivo de aquellos, y conviene tenerla escrita:

- Un **mensaje** es una foto de algo que ya pasó. Viaja, y una vez enviado no cambia. Inmutable y con igualdad por valor.
- Una **entidad** tiene identidad y vida: es *el mismo* producto aunque le suban el precio. La igualdad es por `Id`, no por contenido, y EF Core la rastrea y detecta sus cambios comparando estado.

Un `record` con `init` obligaría a reemplazar el objeto entero para cambiar el stock, que es exactamente lo que EF Core no espera.

Los setters son **privados**, no públicos: se puede leer desde fuera, pero solo el propio tipo decide cómo cambia. Verificado por reflexión que no queda ninguno público.

### 4. Constructor con guardas, no DataAnnotations

**Descartado:** `[Required]`, `[MaxLength(200)]`, `[Range]` sobre las propiedades. Es lo más corto y lo que ASP.NET Core valida solo.

**Elegido:** guardas en el constructor (`ArgumentException.ThrowIfNullOrWhiteSpace`, `ArgumentOutOfRangeException.ThrowIfNegativeOrZero`, …). Dos motivos:

1. **Un atributo no impide construir el objeto.** `new Product { Name = "" }` con `[Required]` compila, se ejecuta y crea un producto inválido; solo falla si alguien acuerda pasarlo por un validador. Con la guarda en el constructor, un `Product` inválido **no llega a existir**.
2. Las DataAnnotations son de la capa de presentación. Puestas en la entidad, atan el modelo de persistencia al binder de MVC. Su sitio son los DTO de entrada de 1.3, que sí las llevarán.

Las reglas son: nombre y descripción no vacíos y dentro de su longitud, precio **mayor que cero**, stock **cero o positivo** (un producto agotado es un producto válido; uno con stock negativo no).

`Name`, `Description` e `ImageUrl` se guardan con `Trim()` aplicado. Un nombre con espacios al final es el mismo nombre, y normalizar al entrar evita que dos productos "iguales" se vean distintos más tarde.

### 5. Constructor privado sin parámetros para EF Core

EF Core necesita poder instanciar la entidad al leer filas. Puede hacerlo de dos maneras.

**Descartado:** dejar que EF enlace el **constructor público** por nombre de parámetro — lo hace automáticamente si los nombres coinciden con las propiedades, y aquí coinciden los cinco. Sale gratis y ahorra el constructor privado. El problema es que entonces **las guardas se ejecutarían al leer de la base de datos**: una fila ya persistida que por lo que sea infringe una regla haría reventar un `SELECT`, y el error aparecería en una consulta que no tiene nada que ver con quien metió el dato. Además el `Id` no es parámetro, así que el enlace sería a medias.

**Elegido:** un constructor privado sin parámetros. Las guardas protegen la **escritura**, que es donde un dato inválido se puede rechazar a tiempo; la lectura solo materializa lo que ya hay.

Lleva dos `null!` para `Name` y `Description`: EF asigna las propiedades por reflexión justo después de llamarlo, pero el compilador no lo sabe y avisaría de que quedan sin inicializar. Es la excepción documentada, no un descuido.

### 6. `Stock` a secas, con el límite escrito en el tipo

El roadmap lo llama "Stock inicial", y el matiz importa: **desde la Fase 3.4 el stock reservable vive en `InventoryDb`** y lo gestiona Inventory.API con `ReserveStock`/`ReleaseStock`. El número que hay aquí es el que el catálogo enseña.

**Descartado:** llamarlo `InitialStock`, con la advertencia metida en el nombre. Es más difícil de malinterpretar, pero arrastra un nombre defensivo hasta la vista de catálogo de 6.2, donde lo que se muestra es simplemente disponibilidad.

**Elegido:** `Stock`, con un `///` que dice explícitamente que nadie descuenta de aquí al crear un pedido. El riesgo real que se está evitando es que en la Fase 2 o 3 alguien reste unidades a esta columna: eso pondría la reserva en el servicio equivocado y dejaría dos fuentes de verdad para la misma cantidad, que es la regla 1 rota por la puerta de atrás.

### 7. Las longitudes máximas, como constantes públicas

`NameMaxLength`, `DescriptionMaxLength` e `ImageUrlMaxLength` viven en la entidad.

**Descartado:** no ponerlas y decidir las longitudes en la configuración de EF de 1.2.

**Elegido:** constantes en el tipo, comprobadas por el constructor. Son una sola fuente para tres sitios que si no las repetirían como números mágicos: la guarda de aquí, el `nvarchar(n)` de 1.2 y la validación del DTO de 1.3. Y sin ellas EF generaría `nvarchar(max)` para las tres columnas, que además de desperdiciar espacio impide indexar `Name`.

### 8. `ImageUrl` opcional y sin validar su forma

Es `string?`: un producto sin foto es válido y no debe impedir darlo de alta.

**No se comprueba que sea una URI absoluta**, que es lo primero que uno añadiría. El motivo es concreto: el seed de 1.4 y el frontend de la Fase 6 pueden servir imágenes con rutas relativas (`/img/laptop.png`), y un `Uri.TryCreate(..., UriKind.Absolute)` las rechazaría. Sí se exige que, si viene, no esté en blanco y quepa en su longitud.

### 9. `Sku` obligatorio, normalizado en mayúsculas y sin formato exigido

Añadido en la revisión, junto con el cambio de la decisión 2. Es el **código de negocio** del producto: el que se imprime en una etiqueta y el que usa una persona para referirse a él. Convive con el `Id` sin solaparse, y la distinción merece estar escrita:

| | `Id` | `Sku` |
|---|---|---|
| Quién lo asigna | `CatalogDb`, con `IDENTITY` | Quien da de alta el producto |
| Para qué sirve | Referencia entre servicios y clave de las relaciones | Que un humano identifique el producto |
| Puede cambiar | Nunca | Sí — se corrige y se renumera |

**Obligatorio, no `string?`.** *Descartado* hacerlo opcional como `ImageUrl`, con el argumento de que un producto puede existir antes de que le asignen código. Se descarta porque entonces nada garantiza que un producto tenga código y el índice único de 1.2 tendría que ser filtrado (`WHERE Sku IS NOT NULL`), que es complejidad para sostener un estado que el catálogo no debería permitir. Una foto opcional es razonable; un producto sin código no.

**Normalizado con `Trim()` + `ToUpperInvariant()`.** *Descartado* guardarlo tal cual se escribe. El motivo no es estético: sin normalizar, `lap-14` y `LAP-14` son dos filas distintas para la entidad y dos productos distintos para quien consulte. Que hoy no colisionen dependería de la collation por defecto de SQL Server, que es *case-insensitive* — o sea, funcionaría **por accidente y no por diseño**, y se rompería el día que alguien cambie la collation o migre la base. Normalizando al entrar, la unicidad de 1.2 se sostiene sola.

**Sin regex de formato.** *Descartado* exigir un patrón tipo `[A-Z0-9-]{3,50}`. Rechazaría códigos malformados al entrar, que suena bien, pero fija un formato **antes** de saber qué códigos usa el seed de 1.4 o un catálogo importado. Es el mismo criterio que la decisión 8 aplica a `ImageUrl`: se valida lo que se sabe (no vacío, longitud), no lo que se supone. Si más adelante hace falta un formato, se añade con casos reales delante.

La unicidad **no la impone la entidad** — un constructor no puede saber qué hay en la tabla. Es un índice único en la configuración de EF Core de **1.2**, y está en *Pendiente*.

---

## Cambios

Dos archivos de código — el segundo entró con la revisión.

| Archivo | Rol |
|---|---|
| [src/Services/Catalog/Catalog.Infrastructure/Entities/Product.cs](../src/Services/Catalog/Catalog.Infrastructure/Entities/Product.cs) | La entidad: `Id` (`int`), `Sku`, `Name`, `Description`, `Price`, `Stock`, `ImageUrl`, más las cuatro constantes de longitud y las guardas del constructor. |
| [src/Shared/Shop133.Contracts/OrderLine.cs](../src/Shared/Shop133.Contracts/OrderLine.cs) | `ProductId` pasa de `Guid` a `int`. Es el único punto de Contracts que menciona un producto: los 9 mensajes lo reciben a través de `OrderLine` y ninguno se tocó. |

⚠️ **El cambio de `OrderLine` cruza una frontera de servicio.** `OrderLine` viaja dentro de `OrderCreated`, `ReserveStock` y `ReleaseStock`, así que por la regla 4 de [CLAUDE.md](../CLAUDE.md) es un breaking change del contrato. Hoy cuesta cero porque ningún servicio lo consume todavía; en la Fase 3 costaría reescribir tres consumers a la vez.

**Ningún `.csproj` se tocó.** `Catalog.Infrastructure` sigue sin paquetes y sin referencias a otros proyectos (EF Core entra en 1.2), y `Shop133.Contracts` sigue sin `PackageReference` ni `ProjectReference`, que es lo que hace verificable la regla 4.

Otros archivos: [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md) (checkbox 1.1), [docs/README.md](README.md) (fila del índice) y [CLAUDE.md](../CLAUDE.md) (tabla de estado y párrafo de situación).

No se añadió ningún test de arquitectura: este punto no introduce una regla nueva en CLAUDE.md, y el item de test de la fase es 1.7.

---

## Detalles que cuestan tiempo

### SQL Server ordena los `uniqueidentifier` al revés que .NET — y eso tumba el argumento de la versión 7

Este es el hallazgo del punto, y salió de verificar una afirmación que se iba a escribir como obvia. **Se conserva entero aunque el `Id` ya no sea un `Guid`**: es la medición que dejó al `Guid` sin su mejor defensa y, por tanto, media decisión 2. Sin ella la reversión a `int` parecería un capricho. Lo medido sigue valiendo para `OrderId`, que sí es un `Guid` y sí se va a persistir en `OrdersDb` a partir de 4.5.

La idea aceptada es: "usa UUID v7 en vez de `NewGuid()` y el índice clustered deja de fragmentarse, porque los ids salen ordenados". La primera mitad es cierta **en .NET**. La segunda es falsa en SQL Server.

Los 5 ids que generó la entidad, en orden de creación, ordenados por SQL Server con `ORDER BY id`:

```
creationOrder id
------------- ------------------------------------
            1 01A016FA-1977-702D-9FBA-7A62BA4C6972
            5 01A016FA-19AA-7738-81EA-838E36D1B7E2
            4 01A016FA-199A-7B17-B94F-B0A6A4C8787D
            2 01A016FA-197A-7D31-B981-D58BEFEB587A
            3 01A016FA-198A-7ADE-A081-D5EC76DF8300
```

`1, 5, 4, 2, 3`. En .NET esos mismos cinco `Guid` se ordenan `1, 2, 3, 4, 5` (comprobado en la misma tanda). No es que se pierda algo de orden: es que no hay ninguno.

El motivo es el criterio de comparación de `uniqueidentifier`, medido con cinco GUIDs que tienen los bits a uno en un solo grupo cada uno:

```
grupo                          id
------------------------------ ------------------------------------
g1 bytes 0-3                   FFFFFFFF-0000-0000-0000-000000000000
g2 bytes 4-5                   00000000-FFFF-0000-0000-000000000000
g3 bytes 6-7                   00000000-0000-FFFF-0000-000000000000
g4 bytes 8-9                   00000000-0000-0000-FFFF-000000000000
g5 bytes 10-15                 00000000-0000-0000-0000-FFFFFFFFFFFF
```

Ascendente, el más pequeño es el que tiene los bits en los **primeros** bytes. O sea que SQL Server compara **empezando por los últimos 6 bytes** y termina por los 4 primeros — exactamente al revés que .NET.

Y ahí está el choque: **UUID v7 pone la marca de tiempo en los bytes 0-5**, que para SQL Server son los *menos* significativos. Los bytes que decide primero son los 10-15, que en v7 son aleatorios. Por eso `NEWSEQUENTIALID()` existe: genera valores crecientes **según ese criterio**, no según el de .NET.

Consecuencias, que es lo que hay que recordar:

- **Un `Guid` v7 como PK clustered en SQL Server se fragmenta igual que uno aleatorio.** La salida sería PK **non-clustered** más un índice clustered por otra columna, o `NEWSEQUENTIALID()`, que es sequential *según el criterio de SQL Server*.
- Para `Product` esto se resolvió por la vía de arriba: el `Id` es `int` y la pregunta desaparece — un `IDENTITY` creciente es exactamente lo que un clustered quiere. Ver decisión 2.
- **Sigue abierto para `OrderId`**, que es `Guid` por necesidad (correlación de la saga) y se persistirá en `OrdersDb` en 4.5. Ahí sí habrá que decidir cómo se indexa, y esta medición es el material para hacerlo.
- Con un catálogo de decenas de productos esto es irrelevante en la práctica. Se documenta porque el proyecto existe para entender los tradeoffs, y porque una justificación equivocada escrita en un comentario sobrevive años.

El comentario de `Product.cs` pasó por las dos correcciones: primero decía la versión aceptada del argumento antes de medirlo, y después de la revisión ya no habla de `Guid` en absoluto.

### `ToUpperInvariant()` puede alargar la cadena, así que el orden de las operaciones importa

Al normalizar el `Sku` la tentación es validar primero y normalizar después:

```csharp
Sku = Validated(sku, SkuMaxLength, nameof(sku)).ToUpperInvariant();   // mal
```

Con ASCII da igual, y por eso pasa desapercibido. Pero `ToUpperInvariant()` **no garantiza conservar la longitud**: `ß` se convierte en `SS`, y hay más casos en griego y armenio. Un `Sku` de 50 caracteres con una `ß` se validaría como válido y se persistiría con 51 — un `nvarchar(50)` lo rechaza o lo trunca, según la configuración, y el error saldría en el `INSERT` y no en el constructor, que es justo lo que la decisión 4 quiere evitar.

El orden correcto es normalizar y validar el resultado:

```csharp
ArgumentException.ThrowIfNullOrWhiteSpace(sku, nameof(sku));
Sku = Validated(sku.ToUpperInvariant(), SkuMaxLength, nameof(sku));
```

La guarda de `null` va suelta y duplica la que `Validated` hace por dentro. No es un descuido: `sku.ToUpperInvariant()` se evalúa **antes** de entrar en `Validated`, así que sin ella un `null` saldría como `NullReferenceException` en vez de como `ArgumentNullException` con su `paramName` — y 1.3 necesita ese `paramName` para devolver un `400` que diga qué campo falla.

### `ArgumentException.ThrowIfNullOrWhiteSpace` lanza dos excepciones distintas

Con `null` lanza `ArgumentNullException`; con `""` o `"   "` lanza `ArgumentException`. Son la misma llamada y dan tipos distintos. Importa para 1.3: un `catch (ArgumentException)` sí cubre los dos casos, porque `ArgumentNullException` hereda de `ArgumentException` — pero al revés no. Comprobado que ambos salen con el `paramName` correcto, que es lo que permitirá mapearlos a un `400` con el nombre del campo.

### El `paramName` sale gratis, pero solo si el parámetro se pasa directo

`ArgumentOutOfRangeException.ThrowIfNegativeOrZero(price)` produce `Parameter 'price'` sin escribirlo, por `CallerArgumentExpression`. En las tres validaciones de texto **sí** hay que pasar el nombre a mano (`nameof(name)`), porque el valor viaja a un helper y la expresión que capturaría el compilador sería `value`, el nombre del parámetro del helper — inútil para quien recibe el error.

---

## Verificación

Ejecutado el 2026-08-18. Salidas reales. La tabla es la de **después** de la revisión; las dos filas del orden de los `Guid` son de la primera pasada y se conservan porque son la prueba de lo que cuenta *Detalles*.

| Check | Resultado |
|---|---|
| `dotnet build shop133.slnx` | **Build succeeded. 0 Warning(s), 0 Error(s)** |
| `dotnet test -- --filter-trait "Category=Fast"` | **Passed! total: 11, failed: 0** |
| Sonda de la entidad y del contrato (32 comprobaciones) | **total: 32  ok: 32  fail: 0** |
| `Product.Id` | `Int32`, vale `0` sin base de datos |
| `OrderLine.ProductId` | `Int32` |
| Normalización del `Sku` | `'  lap-14  '` → `'LAP-14'` |
| Setters públicos en `Product` | ninguno |
| Constructor privado sin parámetros | presente, y materializa sin ejecutar guardas |
| Orden de los `Guid` v7 en .NET *(primera pasada)* | creciente (1,2,3,4,5) |
| Orden de los mismos en SQL Server *(primera pasada)* | **1,5,4,2,3** — ver *Detalles* |

La comprobación con `sqlcmd` **no se repitió tras la revisión**: existía para medir el orden de los `uniqueidentifier` y ya no queda ninguno en `Product`.

**Que `dotnet build` pase no demuestra nada aquí**, y es el mismo problema que tuvo 0.3: ningún proyecto usa todavía `Product` ni `OrderLine`, así que compilarían igual estando mal. Se repitió la técnica de aquel punto — un proyecto de consola desechable **en el scratchpad, fuera del repo**, con `ProjectReference` a `Catalog.Infrastructure` y a `Shop133.Contracts`. La revisión le añadió nueve comprobaciones a las 23 originales: el tipo del `Id`, el de `OrderLine.ProductId` y las seis del `Sku`.

Salida de la sonda:

```
== Tipo del Id: int, sin asignar hasta el INSERT ==
  PASS  Id es int
  PASS  Id vale 0 sin base de datos  (0)

== OrderLine: el contrato también viaja con int ==
  PASS  OrderLine.ProductId es int  (Int32)
  PASS  OrderLine se construye

== Sku: normalización ==
  PASS  Trim + mayúsculas  ('lap-14' -> 'LAP-14')
  PASS  '  lap-14  ' == 'LAP-14'
  PASS  Sku de 50 caracteres aceptado

== Sku: guardas ==
  PASS  sku null  (ArgumentNullException: Value cannot be null. (Parameter 'sku') [paramName=sku])
  PASS  sku vacío  (ArgumentException: The value cannot be an empty string or composed entirely of whitespace. (Parameter 'sku') [paramName=sku])
  PASS  sku en blanco  (ArgumentException: The value cannot be an empty string or composed entirely of whitespace. (Parameter 'sku') [paramName=sku])
  PASS  sku de 51 caracteres  (ArgumentOutOfRangeException: El valor supera el máximo de 50 caracteres. (Parameter 'sku') [paramName=sku])

== Guardas heredadas de 1.1 ==
  PASS  Name recortado  (Laptop Pro 14)
  PASS  Description recortada  (Portátil de 14 pulgadas.)
  PASS  Price asignado  (1299.99)
  PASS  Stock asignado  (7)
  PASS  ImageUrl asignada  (/img/laptop.png)
  PASS  imageUrl null aceptado
  PASS  Stock 0 aceptado
  PASS  name vacío  (ArgumentException: ... (Parameter 'name') [paramName=name])
  PASS  name null  (ArgumentNullException: ... (Parameter 'name') [paramName=name])
  PASS  name de 201 caracteres  (ArgumentOutOfRangeException: El valor supera el máximo de 200 caracteres. (Parameter 'name') [paramName=name])
  PASS  description vacía  (ArgumentException: ... (Parameter 'description') [paramName=description])
  PASS  description de 2001 caracteres  (ArgumentOutOfRangeException: El valor supera el máximo de 2000 caracteres. (Parameter 'description') [paramName=description])
  PASS  imageUrl en blanco  (ArgumentException: ... (Parameter 'imageUrl') [paramName=imageUrl])
  PASS  imageUrl de 501 caracteres  (ArgumentOutOfRangeException: El valor supera el máximo de 500 caracteres. (Parameter 'imageUrl') [paramName=imageUrl])
  PASS  price = 0  (ArgumentOutOfRangeException: price ('0') must be a non-negative and non-zero value. [paramName=price])
  PASS  price negativo  (ArgumentOutOfRangeException: price ('-1') must be a non-negative and non-zero value. [paramName=price])
  PASS  stock negativo  (ArgumentOutOfRangeException: stock ('-1') must be a non-negative value. [paramName=stock])
  PASS  longitudes exactas 200 / 2000 / 500 aceptadas

== Superficie del tipo (lo que verá EF Core en 1.2) ==
  sealed=True
  Id           Int32    setter público=False
  Sku          String   setter público=False
  Name         String   setter público=False
  Description  String   setter público=False
  Price        Decimal  setter público=False
  Stock        Int32    setter público=False
  ImageUrl     String   setter público=False
  PASS  existe ctor privado sin parámetros (EF)
  PASS  ningún setter público
  PASS  EF puede materializar sin ejecutar guardas

total: 32  ok: 32  fail: 0
```

La comparación de ordenación contra SQL Server se hizo con `sqlcmd` **dentro del contenedor del compose**, conectando con `catalog_user` sobre `CatalogDb` — no con `sa`, que la regla 1 prohíbe:

```
docker compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U catalog_user -P *** -d CatalogDb -C -Q "..."
```

Dos cosas de esa llamada que cuesta descubrir: hace falta `-C` porque el certificado es autofirmado (mismo motivo que `TrustServerCertificate=True` en las cadenas de conexión), y **desde Git Bash hay que anteponer `MSYS_NO_PATHCONV=1`** o la ruta `/opt/mssql-tools18/...` se traduce a una ruta de Windows antes de llegar al contenedor:

```
OCI runtime exec failed: "C:/Program Files/Git/opt/mssql-tools18/bin/sqlcmd": no such file or directory
```

El proyecto de consola **no se añadió al repo**.

---

## Pendiente

De la Fase 1 quedan 1.2 a 1.7. Lo que este punto le deja abierto a los siguientes:

- **1.2** — `CatalogDbContext` y la configuración: `decimal(18,2)` para `Price` (por defecto EF usa `decimal(18,2)` en SQL Server, pero conviene declararlo), las **cuatro** longitudes leídas de las constantes de la entidad, y sobre todo el **índice único sobre `Sku`** — la entidad no puede imponer esa regla, ver decisión 9. La pregunta de si la PK va clustered **ya no existe** para `Product`: un `int IDENTITY` es creciente y es lo que un clustered quiere. Sigue abierta para `OrderId` en 4.5.
- **1.3** — el `PUT` necesitará una vía de mutación en la entidad. Se añade cuando exista el caso de uso, no antes. También el mapeo de `ArgumentException`/`ArgumentOutOfRangeException` a `400` con el nombre del campo — y decidir si el `Sku` se puede modificar después del alta, que es la pregunta que abre la decisión 9 al llamarlo "código que se corrige".
- **1.4** — el seed con `HasData` necesita ids **fijos**. Con `int` es trivial (`1, 2, 3…`) y hay que pasarlos explícitamente, porque `HasData` no puede depender de un `IDENTITY`. Los `Sku` del seed son los primeros códigos reales del proyecto: si no encajan en lo que hoy se valida, la decisión 9 se revisa con casos delante en vez de por suposición.
- **1.7** — los tests de componente son los que sustituyen a la sonda desechable de este punto.
