# Fase 0.5 — Repositorio Git con convención de branches

**Fecha:** 2026-08-17 · **Estado:** completado · **Roadmap:** [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md)

---

## Objetivo

El repositorio ya existía: `origin` apuntando a `github.com/rogelio133/shop133`, `.gitignore` de `dotnet new gitignore` con `.env` excluido desde [0.2](fase_0_2.md), y cuatro commits de trabajo real. Así que este punto no es "crear el repo" — es **fijar el modelo de ramas y dejarlo escrito antes de que empiece la Fase 1**.

La posición en el roadmap es la misma lógica que justifica que [0.6](../plan-desarrollo-shop133.md) vaya antes de escribir código de servicio: una convención adoptada a mitad de camino no es una convención, es arqueología. Ahora mismo el repo tiene 4 commits y una rama; en la Fase 4 tendrá decenas y el coste de reordenar es otro.

El estado de partida tenía dos cosas mal colocadas:

- La rama de trabajo se llamaba **`feature_fase_0`** — con guion bajo. El propio roadmap enuncia `feature/*`, con barra. La convención se estaba incumpliendo en su primera aplicación.
- **`main` estaba 3 commits por detrás.** Todo el trabajo de 0.2, 0.3 y 0.4 vivía únicamente en la rama de feature, y no existía ninguna rama de integración donde acumularlo.

**Fuera de alcance deliberadamente:** la protección de ramas en GitHub y cualquier automatización de PR. Ver *Decisiones §6* y *Pendiente*.

---

## Decisiones

### 1. `main` + `develop` + `feature/*`, no trunk-based

**Descartado — GitHub Flow (`main` + `feature/*`, sin `develop`).** Para un proyecto de una persona es objetivamente mejor: menos ramas, menos merges, `main` siempre refleja lo último. La ceremonia de `develop` existe para coordinar equipos y releases, y aquí no hay ni equipo ni releases.

**Elegido igualmente el modelo de tres ramas**, por dos razones:

1. Es lo que enuncia el roadmap, y cambiarlo obligaría a reescribir el punto 0.5 del plan.
2. Es la razón de ser del proyecto. shop133 no existe para entregar una tienda — existe para practicar las cosas que un equipo hace de verdad. `develop` como rama de integración es una de ellas, igual que la saga con compensaciones lo es del lado del código. Aprender la fricción es parte del ejercicio.

El coste asumido es real: un merge extra por fase y una rama más que mantener sincronizada.

**Reparto:**

| Rama | Rol |
|---|---|
| `main` | Estable. Avanza **solo al cerrar una fase**, por merge `--no-ff` desde `develop`, y recibe un tag. Nunca se commitea directamente. |
| `develop` | Integración. Aquí aterriza cada subfase. Es la rama de la que se sale y a la que se vuelve. |
| `feature/*` | Una por fase: `feature/fase-1-catalog`. Sale de `develop`, vuelve a `develop` con `--no-ff`, se borra. |

### 2. Una rama por fase, no una por subfase

**Descartado — `feature/fase-0-2-compose`, `feature/fase-0-3-contracts`, …** Es más granular y cada subfase tendría su PR. Pero la Fase 0 sola habrían sido 6 ramas para 6 commits, cada una con su merge: más ruido de integración que trabajo integrado.

**Elegido:** una rama por fase; las subfases son commits dentro de ella. La rama se cierra cuando la fase se cierra. El número de subfase ya vive en el mensaje del commit (§4), así que la granularidad no se pierde — solo deja de tener una rama por encima.

### 3. `--no-ff` en los dos merges

**Descartado — fast-forward.** Con una sola persona trabajando, casi todos los merges podrían ser fast-forward. El problema es que un fast-forward **borra el hecho de que un conjunto de commits pertenecía a una fase**: la historia queda plana y `fase-1` deja de ser identificable como unidad.

**Descartado — squash.** Colapsa la fase en un commit, que es lo contrario de lo que interesa: los commits por subfase son el enlace entre el roadmap, el commit y su documento en `docs/`. Aplastarlos rompe ese hilo.

**Elegido:** `--no-ff` en `feature/* → develop` y en `develop → main`. El commit de merge es exactamente el marcador que hace legible dónde empieza y acaba cada fase.

### 4. Mensaje de commit `<punto> <qué cambió>`, no Conventional Commits

**Descartado — Conventional Commits** (`feat:`, `fix:`, `chore:`). Es el estándar de facto y habilita changelog automático y semver. Aquí no hay ninguna de las dos cosas: shop133 no se publica ni se versiona, así que el prefijo sería ceremonia sin consumidor.

**Elegido:** `0.5 git branching convention defined` — número de subfase primero, descripción en inglés. Ese número es el identificador que ya enlaza tres sitios (item del roadmap ↔ commit ↔ `docs/fase_0_5.md`); ponerlo delante convierte `git log --oneline` en un índice del roadmap. Es además lo que los cuatro commits existentes ya hacían de forma informal; esto solo lo declara.

Inglés como todo identificador del proyecto. El español vive en `docs/`.

### 5. `develop` creada desde el trabajo actual, no desde `main`

**Descartado — `git branch develop main`.** Es el arranque de libro de Git Flow, pero aquí dejaría `develop` en el commit inicial y los tres commits de 0.2–0.4 colgando solo de la rama de feature. El primer acto tras crear la rama sería un merge para arreglar eso.

**Elegido:** `develop` creada en `ba1d317`, la punta actual. Las subfases 0.2–0.4 quedan donde el modelo dice que deben estar (integradas) sin tocar un solo commit. `main` se queda en `1511e9c` hasta que la Fase 0 cierre entera — que es precisamente el comportamiento que se acaba de definir para `main`.

Efecto colateral esperado: `develop` y `feature/fase-0` apuntan hoy al **mismo commit**. No es un error; es que la rama de feature todavía no ha avanzado por encima de la integración.

### 6. Sin protección de ramas en GitHub, todavía

**Descartado por ahora — proteger `main` (sin push directo, PR obligatorio).** Es la forma de que "nunca se commitea a `main`" deje de ser disciplina y pase a estar aplicada, igual que los logins de [0.4](fase_0_4.md) hicieron con la regla de una base por servicio.

**Motivo del aplazamiento:** una protección que exige PR sin ningún check que ejecutar es solo un clic extra — no verifica nada. Donde la regla empieza a tener contenido es en **8.3**, cuando CI ejecute `dotnet build` y `dotnet test`, porque entonces la protección puede exigir que esos checks pasen. Además `gh` no está instalado en esta máquina (verificado), así que hoy sería configuración hecha a mano en la web y no reproducible desde el repo.

Queda anotado en *Pendiente*.

### 7. Renombrar `feature_fase_0` en vez de dejarla como excepción

**Descartado — dejarla y aplicar la convención desde la Fase 1.** Cero riesgo sobre el remoto, y se documenta como rareza histórica.

**Descartado** porque la excepción sería la **primera** aplicación de la regla, no una vieja. Un repo cuya única rama de feature incumple el patrón `feature/*` enseña la convención al revés.

**Elegido:** renombrar a `feature/fase-0`. Es seguro porque **no reescribe historia**: una rama es un puntero, y los cuatro commits no cambian de hash. La rama no estaba mergeada en ningún sitio ni tenía PR abierto, así que el borrado del ref antiguo en `origin` no dejó nada huérfano — `origin/feature/fase-0` y `origin/develop` ya apuntaban a `ba1d317` antes de borrarlo.

Esto es distinto de un `--force`, que sí destruiría commits. Ver *Detalles*.

---

## Cambios

| Archivo | Rol |
|---|---|
| [CLAUDE.md](../CLAUDE.md) | Sección nueva **Git workflow** con las tres reglas (ramas, commits, no reescribir historia). Estado de fase actualizado. |
| [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md) | 0.5 marcado con link a este documento. |
| [docs/README.md](README.md) | Fila de índice para 0.5. |
| `docs/fase_0_5.md` | Este documento. |

**No hay cambios de código.** El entregable real de este punto es el estado del repositorio:

| Ref | Antes | Después |
|---|---|---|
| `main` | `1511e9c` | `1511e9c` (sin tocar) |
| `develop` | — | `ba1d317`, publicada en `origin` |
| `feature/fase-0` | — | `ba1d317`, publicada en `origin` |
| `feature_fase_0` | local + `origin` | eliminada en ambos |

Ningún commit cambió de hash.

---

## Detalles que cuestan tiempo

**`git branch -m` no renombra nada en el remoto.** Renombra el puntero local y punto. El remoto sigue teniendo el nombre viejo, y el upstream de la rama recién renombrada **sigue apuntando al ref antiguo** hasta que se hace `push -u`. El renombrado completo son tres operaciones, y el orden importa:

```powershell
git branch -m feature/fase-0            # 1. local
git push -u origin feature/fase-0       # 2. publicar el nuevo ref
git push origin --delete feature_fase_0 # 3. borrar el viejo
```

Invertir 2 y 3 deja la punta sin ninguna rama que la alcance entre ambos comandos — recuperable, pero no hay motivo para pasar por ahí.

**No pueden coexistir una rama `feature` y una `feature/loquesea`.** Git guarda las refs como archivos: `refs/heads/feature` no puede ser a la vez archivo y directorio. Si alguna vez existe una rama llamada `feature` a secas, el `git branch feature/fase-1-catalog` falla con un *D/F conflict*. Es un argumento a favor de que el prefijo sea siempre un namespace y nunca una rama.

**Borrar una rama no borra commits mientras otra los alcance.** Por eso el paso 3 es seguro aquí: `ba1d317` es la punta de `develop` y de `feature/fase-0`. Esto es lo que separa un rename de un `push --force`, que sí puede dejar commits inalcanzables.

**Otros clones no ven desaparecer la rama vieja solos.** `origin/feature_fase_0` sigue apareciendo en cualquier otro clon hasta un `git fetch --prune` (o `git remote prune origin`). Si el repo se clona en otra máquina, ese es el comando.

**El commit `ba1d317` tiene una errata en el mensaje** — dice `0.4 tabase per service configured` en lugar de `database`. **No se corrige.** Está publicado en `origin` desde antes de este punto, y la regla de no reescribir historia publicada que se acaba de escribir en CLAUDE.md empieza a aplicar ya, no a partir del siguiente commit. Una errata en un mensaje de log cuesta menos que la excepción.

**`git rev-list --left-right --count A...B` usa tres puntos**, no dos. Con dos puntos (`A..B`) el resultado es un solo número y `--left-right` no aporta nada. Es la forma rápida de responder "¿cuánto se han separado estas dos ramas?" sin leer el grafo:

```powershell
git rev-list --left-right --count main...develop   # -> 0   3
```

Cero commits que `main` tenga y `develop` no; tres al revés.

**PowerShell 5.1 no tiene `&&`.** Encadenar operaciones de git condicionalmente es `git branch -m x; if ($?) { git branch develop }`. Ya está en la sección *Environment gotchas* de CLAUDE.md, pero es donde reaparece a la primera.

---

## Verificación

Ejecutado el 2026-08-17. Salidas reales:

```
=== git branch -vv ===
  develop        ba1d317 [origin/develop] 0.4 tabase per service configured
* feature/fase-0 ba1d317 [origin/feature/fase-0] 0.4 tabase per service configured
  main           1511e9c [origin/main] project structure created

=== git ls-remote --heads origin ===
ba1d317659f70e139635f1cd23bef165673d74fc	refs/heads/develop
ba1d317659f70e139635f1cd23bef165673d74fc	refs/heads/feature/fase-0
1511e9ce2bcfa2c0efea23f19c0f54dd6f776318	refs/heads/main

=== git rev-list --left-right --count main...develop ===
0	3
```

| Check | Resultado |
|---|---|
| `git push -u origin develop` | `* [new branch] develop -> develop`, tracking configurado |
| `git push -u origin feature/fase-0` | `* [new branch] feature/fase-0 -> feature/fase-0` |
| `git push origin --delete feature_fase_0` | `- [deleted] feature_fase_0` |
| `git ls-remote --heads origin` | 3 refs: `develop`, `feature/fase-0`, `main`. La antigua ya no aparece |
| Las 3 ramas locales | todas con upstream, ninguna huérfana |
| `git ls-remote --tags origin` | vacío — aún no cerró ninguna fase, ver *Pendiente* |
| `git rev-list --left-right --count main...develop` | `0 3` — `main` no adelanta a `develop` por nada |
| Hashes antes/después | `ba1d317` y `1511e9c` intactos: el rename no reescribió historia |
| `Get-Command gh` | no instalado — de ahí la decisión §6 |

`git status` quedó limpio salvo los archivos de documentación de este mismo punto.

---

## Pendiente

De la Fase 0 queda **0.6** — `tests/Shop133.ArchitectureTests` con NetArchTest.

**Derivado de este punto:**

- **Al cerrar la Fase 0** (después de 0.6): `feature/fase-0 → develop` con `--no-ff`, luego `develop → main` con `--no-ff`, tag anotado `fase-0`, y borrar `feature/fase-0`. Será la primera vez que el modelo se ejecute entero de punta a punta; hasta entonces solo está declarado.
- **Fase 1:** la rama se llamará `feature/fase-1-catalog` y saldrá de `develop`, no de `main`.
- **8.3 (CI/CD):** es donde la protección de `main` deja de ser un clic vacío — con `dotnet build` y `dotnet test` como checks obligatorios. Requiere instalar `gh` si se quiere configurar desde el repo en lugar de por la web. Ahí también se decide si hace falta una plantilla de PR.
