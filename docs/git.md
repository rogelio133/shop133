# Guía de Git — shop133

**Tipo:** guía operativa (no es un documento de subfase) · **Roadmap:** [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md)

Este documento es el **cómo**: la secuencia exacta de comandos para trabajar una subfase, cerrar una fase y publicar en GitHub.

El **por qué** de estas reglas vive en [fase_0_5.md](fase_0_5.md) — qué alternativas se descartaron y con qué motivo. Si algo aquí te parece ceremonia innecesaria, la respuesta está ahí. Las reglas en forma resumida están en la sección *Git workflow* de [CLAUDE.md](../CLAUDE.md).

---

## Mapa mental: tres ramas

| Rama | Rol | Se commitea directamente |
|---|---|---|
| `main` | Estable. Avanza **solo al cerrar una fase** y recibe un tag anotado. | ❌ Nunca |
| `develop` | Integración. Aquí aterriza cada subfase. Es de la que se sale y a la que se vuelve. | ❌ Solo por merge de PR |
| `feature/*` | Una por fase. Sale de `develop`, vuelve a `develop`, se borra. | ✅ Aquí trabajas |

En una frase: **todo el trabajo ocurre en `feature/*`, se integra en `develop`, y `main` solo se mueve cuando una fase entera está terminada.**

---

## 1. Nombres de rama

**Patrón:** `feature/fase-<número>-<slug-en-inglés>`

| Fase | Nombre de rama |
|---|---|
| 0 | `feature/fase-0` |
| 1 | `feature/fase-1-catalog` |
| 2 | `feature/fase-2-orders` |
| 3 | `feature/fase-3-messaging` |
| 4 | `feature/fase-4-saga` |
| 5 | `feature/fase-5-gateway` |
| 6 | `feature/fase-6-frontend` |
| 7 | `feature/fase-7-observability` |

### Las tres reglas del nombre

**Barras, nunca guiones bajos.** `feature/fase-1-catalog` ✅ · `feature_fase_1` ❌

No es estética: la barra convierte `feature/` en un **namespace** que Git y las herramientas pueden filtrar (`git branch --list 'feature/*'`). Con guion bajo es solo un nombre largo. Este error se cometió de verdad en la Fase 0 y hubo que renombrar la rama — está contado en [fase_0_5.md §7](fase_0_5.md).

**Una rama por fase, no por subfase.** Las subfases (`1.1`, `1.2`, `1.3`…) son **commits dentro** de la rama, no ramas propias. La Fase 1 tiene 7 subfases y **una** rama; se cierra cuando la fase se cierra.

**Nunca crees una rama llamada `feature` a secas.** Git guarda las refs como archivos en disco: `refs/heads/feature` no puede ser a la vez archivo y carpeta. Si existiera esa rama, `git branch feature/fase-1-catalog` fallaría con un error *D/F conflict*. El prefijo es siempre namespace, jamás rama.

---

## 2. Mensajes de commit

**Formato:** `<subfase> <qué cambió, en inglés, en pasado>`

```
0.5 git branching convention defined
```

- **Una sola línea.** Sin cuerpo, sin punto final.
- **El número de subfase va primero.** Ese número es el hilo que une tres sitios: el item del roadmap ↔ el commit ↔ su documento en `docs/`. Ponerlo delante convierte `git log --oneline` en un índice del roadmap.
- **En inglés**, como todo identificador del proyecto. El español vive en `docs/`.
- **Sin prefijos Conventional Commits** (`feat:`, `fix:`, `chore:`). Aquí no hay changelog automático ni semver que los consuma — sería ceremonia sin consumidor.

### Ejemplos contrastados

| ❌ Mal | Por qué | ✅ Bien |
|---|---|---|
| `avances` | No dice qué cambió ni a qué punto pertenece | `1.1 product entity created` |
| `feat: add product model` | Prefijo Conventional, y falta el número | `1.1 product entity created` |
| `1.2 agregando migraciones` | Español, y en gerundio | `1.2 ef core migrations added` |
| `1.3 endpoints.` | Punto final; demasiado vago | `1.3 products crud endpoints added` |
| `WIP` | No es un commit publicable | (termina el trabajo o usa `git stash`) |

### Qué entra en el commit de una subfase

Una subfase **no está cerrada** hasta que existen las tres cosas que exige [CLAUDE.md](../CLAUDE.md) (sección *Sub-phase documentation*). Las tres van **en el mismo commit** que el código:

1. El código o configuración del punto.
2. `docs/fase_<fase>_<punto>.md` — el documento de la subfase.
3. El checkbox del roadmap marcado **con link**: `- [x] **1.1** … — [doc](docs/fase_1_1.md)`.
4. La fila nueva en la tabla de índice de [docs/README.md](README.md).

Un commit que trae el código pero no el documento deja la subfase a medias y el siguiente commit tiene que arreglarlo. Haz `git status` antes de commitear y comprueba que los cuatro aparecen.

---

## 3. Ciclo diario dentro de una fase

Estás en tu rama de fase. Terminas un punto del roadmap:

```powershell
# 1. Mira qué has tocado — y comprueba que están el doc, el roadmap y el README
git status

# 2. Prepara los cambios
git add .

# 3. Revisa exactamente qué vas a commitear (opcional pero recomendable)
git diff --staged --stat

# 4. Commit con el formato de la convención
git commit -m "1.1 product entity created"

# 5. Publica
git push
```

**El paso 5 solo funciona sin argumentos si la rama ya tiene upstream.** La primera vez que empujas una rama nueva:

```powershell
git push -u origin feature/fase-1-catalog
```

El `-u` (`--set-upstream`) enlaza tu rama local con la del remoto. A partir de ahí `git push` y `git pull` a secas ya saben con quién hablar. Si lo olvidas, Git te lo recuerda con el comando exacto en el mensaje de error.

> **PowerShell 5.1 no tiene `&&`.** Para encadenar en una línea usa `;` — `git add . ; git commit -m "..."`. El operador `&&` da error de sintaxis, no un fallo silencioso.

---

## 4. Abrir una fase nueva

```powershell
# 1. Ponte en develop y tráete lo último
git switch develop
git pull

# 2. Crea la rama de la fase a partir de ahí
git switch -c feature/fase-1-catalog

# 3. Publícala y fija el upstream
git push -u origin feature/fase-1-catalog
```

**El punto crítico es el paso 1: se sale siempre de `develop`, nunca de `main`.**

`main` va por detrás a propósito — solo avanza al cerrar fases. Si ramificas desde `main`, tu rama nace sin el trabajo de las fases anteriores y el primer merge será un campo de conflictos. Antes de crear la rama, verifica dónde estás:

```powershell
git branch --show-current   # debe decir: develop
```

---

## 5. Cerrar una fase: dos Pull Requests

Cuando el último punto de la fase está commiteado y pusheado, la fase se cierra con **dos PRs encadenados** en github.com.

### PR 1 — `feature/fase-X` → `develop`

**1. Asegúrate de que todo está publicado:**

```powershell
git push
git status        # debe estar limpio
```

**2. Abre el PR.** Puedes ir directo con esta URL (sustituyendo la rama):

```
https://github.com/rogelio133/shop133/compare/develop...feature/fase-0
```

> ⚠️ **El error número uno.** Si abres el PR desde el botón que GitHub ofrece tras un push, la rama **base** que propone es `main` — la rama por defecto del repo. **Hay que cambiar el desplegable `base:` a `develop` a mano.**
>
> Un PR con base `main` se salta `develop` entera y rompe el modelo de tres ramas. Antes de crear el PR, lee la cabecera: debe decir `base: develop  ←  compare: feature/fase-0`.

**3. Título y cuerpo.**

- Título: el nombre de la fase — `Fase 0 — Setup base`
- Cuerpo: la lista de puntos que entran, enlazando sus documentos. Ejemplo:

```markdown
Cierra la Fase 0.

- 0.2 docker-compose (SQL Server, RabbitMQ, Jaeger) — docs/fase_0_2.md
- 0.3 Shop133.Contracts con los 9 mensajes — docs/fase_0_3.md
- 0.4 Una base de datos y un login por servicio — docs/fase_0_4.md
- 0.5 Convención de branches — docs/fase_0_5.md
- 0.6 Shop133.ArchitectureTests — docs/fase_0_6.md
```

**4. Mergea con el botón correcto.**

> ⚠️ El desplegable del botón verde tiene tres opciones. **Solo una es válida aquí: "Create a merge commit".**

| Opción | ¿Usar? | Motivo |
|---|---|---|
| **Create a merge commit** | ✅ **Sí** | Es exactamente el `--no-ff` de la convención. Deja un commit de merge que marca dónde empieza y acaba la fase. |
| Squash and merge | ❌ No | Colapsa todos los commits de subfase en uno solo y **rompe el hilo** roadmap ↔ commit ↔ `docs/`. |
| Rebase and merge | ❌ No | Aplana la historia y borra el límite de la fase — el dato que más interesa conservar aquí. |

Que el merge lo ejecute GitHub en vez de tu terminal no cambia el resultado: "Create a merge commit" produce el mismo commit de merge que `git merge --no-ff`, así que la regla de [fase_0_5.md §3](fase_0_5.md) se cumple igual.

**5. Borra la rama** con el botón *Delete branch* que aparece tras el merge. La local se borra después (paso de resincronización).

### PR 2 — `develop` → `main`

Mismo procedimiento, con base `main`:

```
https://github.com/rogelio133/shop133/compare/main...develop
```

- Título: `Fase 0 — Setup base → main`
- Mismo botón: **Create a merge commit**.
- **No borres `develop`.** Es una rama permanente; GitHub ofrecerá el botón igual, ignóralo.

> Este segundo PR **solo ocurre al cerrar una fase completa**, nunca por subfase. Es lo único que mueve `main`.

### 6. Resincroniza tu clon

GitHub mergeó en el servidor; tu máquina no se ha enterado de nada:

```powershell
git switch develop
git pull

git switch main
git pull

git fetch --prune                  # borra la referencia a origin/feature/fase-0
git branch -d feature/fase-0       # borra la rama local
```

`--prune` es lo que hace desaparecer `origin/feature/fase-0` de tu lista local después de borrarla en GitHub. Sin él, sigue apareciendo en `git branch -a` para siempre.

`git branch -d` (minúscula) se niega a borrar una rama con trabajo sin mergear — es una red de seguridad. Si se queja, es que algo no llegó a `develop`: investiga antes de forzar con `-D`.

### 7. El tag — este paso sigue siendo local

**GitHub no crea tags anotados al mergear un PR.** El botón de merge no tiene equivalente, así que el tag lo pones tú:

```powershell
git switch main
git pull                                    # asegúrate de tener el merge del PR 2
git tag -a fase-0 -m "Fase 0 — Setup base"
git push origin fase-0
```

- `-a` crea un tag **anotado**: lleva autor, fecha y mensaje. Un tag sin `-a` es solo un puntero sin metadatos.
- **Los tags no viajan con `git push`.** Hay que empujarlos explícitamente, por nombre (como arriba) o todos con `git push --tags`.
- Nomenclatura: `fase-0`, `fase-1`, … una por fase cerrada.

Comprueba:

```powershell
git tag -l                       # local
git ls-remote --tags origin      # remoto
```

---

## 6. Ejemplo completo: cerrar la Fase 0 y abrir la Fase 1

Esta es la secuencia real que toca ejecutar a continuación en este repo. Estado de partida verificado:

| Ref | Commit |
|---|---|
| `feature/fase-0` | `08f68c5` (rama activa, 1 commit por delante de `develop`) |
| `develop` | `ba1d317` |
| `main` | `1511e9c` |
| tags | ninguno |

**Paso 1 — Termina el punto 0.6 en la rama de fase.**

```powershell
git branch --show-current        # feature/fase-0
# ... creas tests/Shop133.ArchitectureTests, escribes docs/fase_0_6.md,
#     marcas el checkbox 0.6 del roadmap y añades la fila en docs/README.md
git status
git add .
git commit -m "0.6 architecture tests project added"
git push
```

**Paso 2 — PR 1: `feature/fase-0` → `develop`.**

Abre `https://github.com/rogelio133/shop133/compare/develop...feature/fase-0`, verifica que la base dice **`develop`**, crea el PR con título `Fase 0 — Setup base`, y mergea con **Create a merge commit**. Borra la rama remota con el botón.

**Paso 3 — PR 2: `develop` → `main`.**

Abre `https://github.com/rogelio133/shop133/compare/main...develop`, base **`main`**, mergea con **Create a merge commit**. No borres `develop`.

**Paso 4 — Resincroniza y limpia.**

```powershell
git switch develop
git pull
git switch main
git pull
git fetch --prune
git branch -d feature/fase-0
```

**Paso 5 — Tag de la fase cerrada.**

```powershell
git tag -a fase-0 -m "Fase 0 — Setup base"
git push origin fase-0
```

**Paso 6 — Abre la Fase 1.**

```powershell
git switch develop
git pull
git switch -c feature/fase-1-catalog
git push -u origin feature/fase-1-catalog
```

**Paso 7 — Comprueba que todo quedó donde debe.**

```powershell
git branch -vv
git ls-remote --heads origin
git ls-remote --tags origin
```

`main` y `develop` deben apuntar al mismo commit (la fase acaba de cerrarse), y `feature/fase-1-catalog` debe existir en local y en `origin`.

---

## 7. Cuando algo sale mal

> **La línea roja:** antes de `push` puedes arreglar casi todo. Después de `push`, la historia es pública y **no se reescribe**. `--force` sobre `develop` o `main` está fuera de la mesa, sin excepciones. Un error publicado se corrige con un commit nuevo o un `git revert`, no borrando el pasado.

### Me equivoqué en el mensaje del commit y **no** he hecho push

```powershell
git commit --amend -m "0.6 architecture tests project added"
```

### Me equivoqué en el mensaje y **ya** hice push

No se toca. Hay precedente real en este repo: el commit `ba1d317` dice `0.4 tabase per service configured` en lugar de `database`, y **se dejó tal cual** — está explicado en [fase_0_5.md](fase_0_5.md). Una errata en un mensaje de log cuesta menos que la excepción a la regla.

Si el error es de contenido y no de redacción, el arreglo es hacia delante:

```powershell
git revert <hash>        # crea un commit que deshace aquel
```

### Olvidé algo en el último commit y **no** he hecho push

```powershell
git add el-archivo-que-falto
git commit --amend --no-edit     # se une al commit anterior, mismo mensaje
```

Muy útil cuando commiteas el código y te das cuenta de que faltaba `docs/fase_X_Y.md`.

### Commiteé en la rama equivocada (por ejemplo, en `develop`)

Mientras **no** hayas hecho push:

```powershell
# 1. Lleva los commits a una rama correcta
git switch -c feature/fase-1-catalog

# 2. Vuelve a la rama equivocada y devuélvela a donde estaba el remoto
git switch develop
git reset --hard origin/develop
```

`reset --hard` descarta cambios sin guardar de esa rama — asegúrate de que tus commits ya están en la rama nueva (`git log --oneline -3` en ella) antes de ejecutarlo.

### Creé la rama desde `main` en vez de `develop`

Mientras **no** esté pusheada:

```powershell
git rebase --onto develop main feature/fase-1-catalog
```

Si ya la publicaste, lo más barato es crear la rama bien y volver a aplicar el trabajo con `git cherry-pick`.

### `git pull` me dejó un conflicto

```powershell
git status                       # lista los archivos en conflicto
# ... editas cada archivo y borras los marcadores <<<<<<< ======= >>>>>>>
git add el-archivo-resuelto
git commit                       # acepta el mensaje de merge que propone
```

Para salir sin resolver nada y volver al estado anterior:

```powershell
git merge --abort
```

### Tengo trabajo a medias y necesito cambiar de rama

```powershell
git stash                        # guarda los cambios y limpia el directorio
git switch otra-rama
# ... lo que tengas que hacer
git switch la-de-antes
git stash pop                    # recupera los cambios
```

### Olvidé el `-u` en el primer push

```powershell
git push -u origin feature/fase-1-catalog
```

### Quiero deshacer un `git add`

```powershell
git restore --staged el-archivo      # lo saca del staging, conserva los cambios
```

### Borré algo y quiero recuperarlo

```powershell
git restore el-archivo               # descarta cambios no commiteados de ese archivo
git reflog                           # historial de dónde ha estado HEAD: casi todo se recupera desde aquí
```

`git reflog` es la red de seguridad de último recurso: registra cada movimiento de `HEAD` durante ~90 días, incluidos los commits que ya no alcanza ninguna rama.

---

## 8. Chuleta

| Comando | Para qué |
|---|---|
| `git status` | Qué has tocado y en qué rama estás |
| `git branch --show-current` | Solo el nombre de la rama actual |
| `git branch -vv` | Ramas locales, su commit y su upstream |
| `git switch <rama>` | Cambiar de rama |
| `git switch -c <rama>` | Crear rama y cambiarse a ella |
| `git pull` | Traer lo del remoto a la rama actual |
| `git add .` | Preparar todos los cambios |
| `git diff --staged --stat` | Qué se va a commitear, en resumen |
| `git commit -m "1.1 ..."` | Commit con la convención |
| `git push` | Publicar (requiere upstream) |
| `git push -u origin <rama>` | Primer push de una rama: publica y fija upstream |
| `git log --oneline --graph --all` | La historia y su forma, incluidos los merges |
| `git fetch --prune` | Actualizar refs remotas y limpiar las borradas |
| `git branch -d <rama>` | Borrar rama local ya mergeada |
| `git tag -a fase-N -m "..."` | Tag anotado al cerrar fase |
| `git push origin fase-N` | Publicar el tag (no viaja con `git push`) |
| `git stash` / `git stash pop` | Aparcar y recuperar trabajo a medias |
| `git reflog` | Red de seguridad: dónde ha estado `HEAD` |

**Comandos prohibidos en este repo:** `git push --force` (y `--force-with-lease`) sobre `develop` o `main`, `git rebase` de commits ya publicados, y el botón *Squash and merge* de GitHub.

---

## Referencias

- [fase_0_5.md](fase_0_5.md) — el porqué de este modelo, con las alternativas descartadas
- [CLAUDE.md](../CLAUDE.md) — sección *Git workflow* (reglas) y *Sub-phase documentation* (qué exige cerrar una subfase)
- [plan-desarrollo-shop133.md](../plan-desarrollo-shop133.md) — el roadmap del que salen los números de subfase
- [README.md](README.md) — índice de documentos por subfase
