# Fase 0.6 — Tests de arquitectura con NetArchTest

**Fecha:** 2026-08-18 · **Estado:** completado · **Roadmap:** [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md)

---

## Objetivo

Las reglas de arquitectura de [CLAUDE.md](../CLAUDE.md) están escritas en prosa: una base de datos por servicio, `Shop133.Contracts` sin dependencias, `.API → .Infrastructure → .Domain`, el frontend hablando solo con el Gateway. Una regla que solo está escrita **se rompe en silencio** — que es exactamente el modo de fallo que este proyecto existe para enseñar a evitar.

Este punto convierte cuatro de esas reglas en tests que fallan solos.

**Por qué va aquí y no después:** los once proyectos ya existen y `Shop133.Contracts` ya tiene sus nueve mensajes, pero **no hay una sola línea de código de servicio**. Fijar las reglas ahora las convierte en una barrera que la Fase 1 encuentra puesta. Añadirlas en la Fase 4, con la saga escrita, sería arqueología: cada fallo obligaría a decidir si se arregla el código o se relaja la regla, y con el código ya funcionando gana el código.

**Reglas cubiertas** (numeradas como en *Architecture rules* de `CLAUDE.md`):

| Regla | Enunciado | Test |
|---|---|---|
| 1 | Una base de datos por servicio | `ServiceProjects_DoNotReference_OtherServices`, `DbContextFiles_LiveOnlyIn_OwningServiceInfrastructure` |
| 3 | El frontend solo habla con el Gateway | `Frontend_DoesNotReference_ServicesOrGateway` |
| 4 | `Shop133.Contracts` delgado e inmutable | Las cinco de `ContractsRulesTests` |
| 5 | Dirección de dependencias dentro de un servicio | Las tres de `LayeringRulesTests` |

**Fuera de alcance deliberadamente:**

- **Reglas 2, 6 y 7** (comunicación por eventos, consumers idempotentes, compensación explícita). No son estructurales: hablan de comportamiento en tiempo de ejecución. No hay forma de expresarlas sobre un grafo de referencias, y su verificación real es el harness de MassTransit en 3.7 y 4.7.
- **La infraestructura de tests de componente** — fixtures de Testcontainers, reset de datos entre tests. Entra en 1.7, sobre el servicio más simple.
- **Que esto corra en CI.** Necesita 8.3.

---

## Decisiones

### 1. Las reglas de referencias se leen del `.csproj`, no de los ensamblados

**Descartado — hacerlo todo con reflexión / NetArchTest sobre los ensamblados compilados.** Es lo obvio y es lo que hace cualquier ejemplo de NetArchTest.

**El problema:** Roslyn **poda del manifiesto las referencias que el código no usa**. Los diez proyectos de servicio están hoy vacíos, así que `Catalog.API.dll` no declara ninguna referencia a nada — y un test del tipo "Catalog no referencia a Orders" pasaría **en vacío**. Peor todavía: seguiría pasando si alguien añadiera la referencia al `.csproj`, hasta el día en que alguien escribiera el primer `using`. El test estaría verde justo durante toda la ventana en la que la infracción es fácil de deshacer.

**Elegido:** [ProjectGraph.cs](../tests/Shop133.ArchitectureTests/ProjectGraph.cs) localiza la raíz del repo (subiendo hasta encontrar `shop133.slnx`), hace glob de `src/**/*.csproj` y parsea los `<ProjectReference>` con `System.Xml.Linq`. El `.csproj` es donde la referencia **se declara**, así que la infracción salta en el commit que la añade.

El cierre transitivo importa y no es adorno: en la verificación negativa de más abajo, una sola referencia `Catalog.API → Orders.API` produjo tres infracciones, porque arrastra `Orders.Infrastructure` y `Orders.Domain` detrás.

Efecto colateral bueno: el proyecto de test **solo referencia `Shop133.Contracts`**. No tiene que referenciar la solución entera para inspeccionarla.

### 2. `Orders.Domain` sí puede referenciar `Shop133.Contracts` — y CLAUDE.md decía lo contrario

Al escribir el test apareció un conflicto real. La regla 5 de `CLAUDE.md` decía *"The domain layer references no other project"*, pero [Orders.Domain.csproj](../src/Services/Orders/Orders.Domain/Orders.Domain.csproj) referencia `Shop133.Contracts` desde [0.3](fase_0_3.md) — y **debe** hacerlo: la `OrderStateMachine` de la Fase 4 vive ahí y consume esos mensajes.

**Descartado — quitar la referencia** para que la regla se cumpliera literalmente. Obligaría a duplicar los tipos de mensaje dentro del dominio, que es justo lo que `Shop133.Contracts` existe para evitar.

**Elegido:** la regla ejecutable es *"`Orders.Domain` no referencia ningún proyecto salvo `Shop133.Contracts`"*, y se ha **corregido la redacción de la regla 5 en `CLAUDE.md`**. Esto es lo que la sección *Testing §4* de `CLAUDE.md` predecía que pasaría: escribir una regla en forma ejecutable obliga a ser exacto, y la prosa aproximada se cae sola.

### 3. NetArchTest se queda, pero cubre un test de once

**El estado del paquete:** `NetArchTest.Rules` 1.3.2 es de 2021, apunta a `netstandard2.0` y usa Mono.Cecil. Se asumió el riesgo de que no leyera ensamblados `net10.0`. **No se materializó** — restaura y funciona sin tocar nada.

Aun así, dos de sus límites decidieron el reparto:

- No sabe distinguir un `record` de una `class`, ni un setter `init` de uno normal. Son metadatos de compilador (§*Detalles*), no conceptos de NetArchTest.
- Sobre proyectos vacíos sufre el mismo problema de la decisión 1.

**Elegido:** NetArchTest se usa donde su API es más legible que el equivalente a mano — `Contracts_Types_HaveNoDependencyOnForbiddenNamespaces`, que declara la prohibición de `MassTransit`, `Microsoft.EntityFrameworkCore` y `System.ComponentModel.DataAnnotations` en una línea. El resto va en reflexión plana y lectura de `.csproj`.

**Descartado — `TngTech.ArchUnitNET` y `NetArchTest.eNhancedEdition`** (ambos mantenidos, el segundo un fork casi compatible). Cambiar de librería habría obligado a reescribir el roadmap y `CLAUDE.md`, que nombran `NetArchTest.Rules`, a cambio de un beneficio nulo para un uso de un solo test. Si algún día ese test crece, la conversación se reabre.

### 4. Microsoft.Testing.Platform, porque el SDK 10 no deja otra

Esto **no fue una elección**: `xunit.v3` 4.0.0 corre sobre Microsoft.Testing.Platform, y el SDK 10 **eliminó el puente que permitía ejecutar MTP a través de VSTest**. El plan inicial era VSTest precisamente para que el comando `dotnet test --filter Category=Fast` documentado en `CLAUDE.md` siguiera siendo literal. No es posible.

**Consecuencias asumidas, todas ya aplicadas:**

- El opt-in va en [global.json](../global.json) (`"test": { "runner": "Microsoft.Testing.Platform" }`) — es decir, afecta al repositorio entero, no solo a este proyecto.
- El proyecto de test es un **ejecutable** (`<OutputType>Exe</OutputType>`): se lanza a sí mismo en vez de que un runner cargue su dll.
- **Dos paquetes previstos sobraron**: `Microsoft.NET.Test.Sdk` y `xunit.runner.visualstudio` son infraestructura de VSTest. `xunit.v3` trae todo lo necesario. El proyecto queda con dos paquetes en vez de cuatro.
- La sintaxis de filtro cambia; los comandos nuevos están en *Verificación* y ya actualizados en `CLAUDE.md`.

### 5. El test de `DbContext` se escribe ahora aunque hoy no verifique nada

`DbContextFiles_LiveOnlyIn_OwningServiceInfrastructure` recorre `src/**/*DbContext.cs` y comprueba que cada uno esté en el `.Infrastructure` de su propio servicio. Hoy **no hay ningún `DbContext`**: el primero llega en 1.2. El test pasa sobre una lista vacía.

**Descartado — no escribirlo hasta que exista un `DbContext`.** Un test verde que no comprueba nada es una mentira, y ese es el argumento para no escribirlo.

**Elegido escribirlo igualmente**, con un comentario en el propio test que dice que hoy pasa en vacío. El motivo es la asimetría de coste: escrito ahora, cuesta cero y está puesto el día que aparezca `CatalogDbContext`; escrito en 1.2, hay que acordarse — y acordarse es exactamente el mecanismo que este punto del roadmap existe para no depender de él.

---

## Cambios

### Creados

| Archivo | Rol |
|---|---|
| [tests/Shop133.ArchitectureTests/Shop133.ArchitectureTests.csproj](../tests/Shop133.ArchitectureTests/Shop133.ArchitectureTests.csproj) | `net10.0`, `OutputType=Exe`. Paquetes: `xunit.v3` 4.0.0 y `NetArchTest.Rules` 1.3.2. Una sola `ProjectReference`: `Shop133.Contracts`. |
| [tests/Shop133.ArchitectureTests/ProjectGraph.cs](../tests/Shop133.ArchitectureTests/ProjectGraph.cs) | El grafo de referencias leído de los `.csproj`. Localiza la raíz del repo, parsea `ProjectReference`/`PackageReference`, resuelve el cierre transitivo y sabe a qué servicio pertenece cada proyecto. |
| [tests/Shop133.ArchitectureTests/ContractsRulesTests.cs](../tests/Shop133.ArchitectureTests/ContractsRulesTests.cs) | Regla 4: 5 tests. |
| [tests/Shop133.ArchitectureTests/LayeringRulesTests.cs](../tests/Shop133.ArchitectureTests/LayeringRulesTests.cs) | Regla 5: 3 tests. |
| [tests/Shop133.ArchitectureTests/ServiceBoundaryRulesTests.cs](../tests/Shop133.ArchitectureTests/ServiceBoundaryRulesTests.cs) | Reglas 1 y 3: 3 tests. |
| `docs/fase_0_6.md` | Este documento. |

Los once tests llevan `[Trait("Category", "Fast")]`: ninguno necesita Docker.

### Modificados

| Archivo | Cambio |
|---|---|
| [global.json](../global.json) | Bloque `"test": { "runner": "Microsoft.Testing.Platform" }`. |
| [shop133.slnx](../shop133.slnx) | Carpeta `/tests/` con el proyecto nuevo. |
| [CLAUDE.md](../CLAUDE.md) | Tabla de estado (Fase 0 cerrada), párrafo *Current status*, redacción de la regla 5 (decisión 2), y los comandos de test con la sintaxis de MTP. |
| [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md) | `0.6` marcado con enlace a este documento. |
| [docs/README.md](README.md) | Fila del índice. |

---

## Detalles que cuestan tiempo

**1. El SDK 10 rompe `dotnet test` con xunit.v3, y el mensaje de error apunta mal.** El primer intento (VSTest, tal y como estaba planeado) falló con:

```
error : Testing with VSTest target is no longer supported by Microsoft.Testing.Platform
on .NET 10 SDK and later. If you use dotnet test, you should opt-in to the new dotnet
test experience.
```

Costó **tres intentos** dar con la forma del opt-in:

- `<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>` en el `.csproj` **no es el opt-in** — es lo contrario: lo que hace es importar los targets de VSTest. El error no cambió.
- El opt-in real va en **`global.json`**, no en el proyecto. Se ve leyendo `dotnet test --help`: la descripción del comando cambia a *"opted-in via 'global.json' file"* en cuanto está puesto.
- Con eso, el siguiente error: `xUnit.net v3 test projects must be executable`. Falta `<OutputType>Exe</OutputType>`.

**2. `--filter Category=Fast` ya no existe.** En MTP las opciones de filtro las aporta el propio adaptador de xunit, así que son **opciones de extensión** y van **después de `--`**:

```powershell
dotnet test -- --filter-trait "Category=Fast"
```

Dos trampas alrededor de esto:

- `dotnet test -- --trait "Category=Fast"` (el nombre que usa el ejecutable de xunit por sí solo) devuelve *"Zero tests ran"* con código de salida 5, sin decir que la opción no existe. En MTP el nombre es `--filter-trait`.
- El *query filter* `-- --filter "/*/*/*/*[Category=Fast]"` devuelve 0 tests aunque `/*/*/*/*` a secas devuelva los 11. No se investigó más: el camino soportado es `--filter-trait`, y `--filter` acepta además la sintaxis VSTest de siempre (`-- --filter "Category=Fast"`, verificado).
- `dotnet test -- --help` **revienta el CLI** con un `System.InvalidOperationException` en `TestApplication.OnCommandLineOptionMessages`. La forma que funciona para ver las opciones del adaptador es `dotnet test --project <ruta> --help`.

**3. Git Bash mangleaba los argumentos que empiezan por `/`.** Los primeros intentos con el query filter se hicieron desde Bash, que convierte `/*/*/*/*` en una ruta de Windows antes de que llegue al proceso. Cualquier prueba de filtros de este tipo hay que hacerla desde PowerShell.

**4. `record` e `init` no son conceptos que la reflexión exponga.** No hay `Type.IsRecord` ni `PropertyInfo.IsInitOnly`. Lo que hay:

- Un `record` se reconoce porque el compilador le sintetiza un método llamado **`<Clone>$`**. Es la única marca fiable; `record` no deja ningún flag en los metadatos del tipo.
- Un setter `init` es un setter normal cuyo **parámetro de retorno** lleva el modificador requerido `System.Runtime.CompilerServices.IsExternalInit`. Se lee con `setter.ReturnParameter.GetRequiredCustomModifiers()`.

**5. Los paquetes que no hicieron falta.** `Microsoft.NET.Test.Sdk` y `xunit.runner.visualstudio` estaban en el plan y en la lista de `CLAUDE.md`. Con MTP son infraestructura de VSTest y sobran: `xunit.v3` trae su propio runner.

---

## Verificación

Todo lo de abajo se ejecutó tal cual, con esta salida.

**Build de la solución** (12 proyectos, con el de tests dentro):

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:25.54
```

**Suite completa, desde la raíz del repo** (`dotnet test`):

```
Running tests from ...\Shop133.ArchitectureTests\bin\Debug\net10.0\Shop133.ArchitectureTests.dll (net10.0|x64)
...passed (1s 131ms)

Test run summary: Passed!
  total: 11
  failed: 0
  succeeded: 11
  skipped: 0
  duration: 1s 921ms
```

**Filtrando por categoría** (`dotnet test -- --filter-trait "Category=Fast"`): mismos 11 tests, `Passed!`. Hoy coinciden porque todavía no existe ninguna categoría `Docker`; la primera llega en 1.7.

### Verificación negativa

Un test de arquitectura que nunca ha fallado no está demostrado que sea una barrera. Se rompieron dos reglas a propósito y se revirtieron después.

**a) Referencia entre servicios.** Se añadió `<ProjectReference Include="..\..\Orders\Orders.API\Orders.API.csproj" />` a `Catalog.API.csproj`:

```
failed Shop133.ArchitectureTests.ServiceBoundaryRulesTests.ServiceProjects_DoNotReference_OtherServices (4ms)
  Un servicio no referencia a otro; lo único compartido es Shop133.Contracts. Si necesita
  sus datos, van por evento o por API. Referencias prohibidas: Catalog.API → Orders.API,
  Catalog.API → Orders.Infrastructure, Catalog.API → Orders.Domain

  total: 11 / failed: 1 / succeeded: 10
```

Las **tres** infracciones a partir de **una** referencia son el cierre transitivo funcionando: referenciar `Orders.API` arrastra todo lo que `Orders.API` referencia.

**b) Mensaje mutable.** Se cambió `OrderLine.Quantity` de `{ get; init; }` a `{ get; set; }`:

```
failed Shop133.ArchitectureTests.ContractsRulesTests.Contracts_PublicMembers_AreImmutable (5ms)
  total: 11 / failed: 1 / succeeded: 10
```

Esto confirma además que la detección de `IsExternalInit` (§*Detalles* 4) discrimina de verdad y no está dando verde por defecto.

Ambos cambios revertidos; `git diff --stat` posterior no los incluye y la suite vuelve a 11/11.

---

## Pendiente

| Qué | Cuándo |
|---|---|
| `DbContextFiles_LiveOnlyIn_OwningServiceInfrastructure` deja de pasar en vacío | **1.2**, con el primer `CatalogDbContext` |
| Categoría `Docker` (hoy los 11 tests son `Fast`) | **1.7**, con Testcontainers |
| Reglas de comportamiento: consumers idempotentes (6), compensación (7) | **3.7** y **4.7**, con el harness de MassTransit |
| Regla estructural "los consumers viven en `Consumers/`, no en `Controllers/`" | **3.1**, cuando exista el primer consumer |
| Regla "los controllers son delgados" — si es que se puede expresar sin falsos positivos | **1.3** en adelante |
| Ejecutar esta suite en CI | **8.3** |
| Borrar `Microsoft.NET.Test.Sdk` y `xunit.runner.visualstudio` de la lista de paquetes previstos de `CLAUDE.md` | Hecho ya, en el mismo commit |

**Sobre el cierre de la Fase 0:** con este punto los seis items están completos. El cierre formal —PR `feature/fase-0 → develop`, PR `develop → main` y tag anotado `fase-0`— sigue el procedimiento de [git.md](git.md) y no forma parte de este documento.
