# Fase 6.1 — Layout base con Bootstrap 5

**Fecha:** 2026-09-10 · **Estado:** completado · [Roadmap](../plan-desarrollo-shop133.md)

---

## Objetivo

Convertir `Shop133.Web` —el andamiaje `dotnet new mvc` intacto desde el commit `1511e9c`, el **único proyecto de `src/` que no se había tocado nunca**— en el esqueleto de la tienda: un `_Layout.cshtml` con navbar y footer propios sobre el que `6.2` (grid de catálogo), `6.3` (carrito en sesión), `6.4` (checkout), `6.5` (estado del pedido) y `6.7` (toasts) solo tengan que colgar su contenido.

El punto es **estructura y nada más**. Bootstrap **5.3.3** ya estaba vendorizado en `wwwroot/lib/bootstrap/dist/` desde `0.1` (MIT, dist completo con el bundle que incluye Popper), junto a jQuery 3.7.1 y jquery-validation — así que la parte de "Bootstrap 5" del título no era instalar nada, era **usarlo de verdad**: la plantilla de Microsoft renderiza el navbar con nomenclatura de la 4.x.

**No entra ningún paquete.** `Shop133.Web.csproj` sigue con **cero `PackageReference` y cero `ProjectReference`**, que es la regla 3 de [CLAUDE.md](../CLAUDE.md) en su forma más literal.

**Fuera de alcance, deliberadamente:**

- Ninguna llamada HTTP, ninguna URL base del Gateway, ningún `IHttpClientFactory` — son `6.2` y `6.6`.
- Ningún `ViewComponent`, ningún contenedor de toasts, ningún carrito en sesión.
- Ningún `Dockerfile` ni servicio en `docker-compose` para el frontend.
- **Ni un test.** Ver la última decisión y la sección Pendiente: no es un olvido, es un hueco que el roadmap no le asigna a nadie.

La suite de arquitectura se queda en **17** y el repositorio en **131**, dicho por escrito con el precedente de `3.3`, `3.5`, `4.5` y `5.2`: no entra paquete ni forma nueva que vigilar, y una regla que nunca engancha es lo que `3.2` rechazó.

---

## Decisiones

### 1. Los enlaces que todavía no existen se declaran DESHABILITADOS, no se omiten ni se apuntan a un stub

El navbar lleva cuatro items: `Inicio`, que resuelve, y `Catálogo`, `Carrito` y `Estado del pedido` con `class="nav-link disabled"`, `aria-disabled="true"` y **sin `href`**. Cada uno lo activa su punto: `6.2`, `6.3` y `6.5`.

*Descartado* declararlos con `href` hacia controllers que no existen: sería el mismo **filtro que nunca engancha** que `5.1` rechazó al no declarar rutas del Gateway hacia servicios sin `Controllers/`, solo que devolviendo 404 en la cara del usuario.

*Descartado* crear `CatalogController`/`CartController` como stubs vacíos para que el navbar navegue hoy: es inventar la forma antes de que exista el caso de uso —lo que `1.1` refusó con `Product.Update()` y `2.1` con `Order.Confirm()`— y `6.2` tendría que reescribir lo que este punto adivinó.

*Descartado* también dejar el navbar con un solo enlace y que cada punto añada el suyo: es lo más coherente con los dos precedentes anteriores, pero deja el entregable de `6.1` sin nada que mirar. El `disabled` es el punto medio que se eligió a conciencia: **enseña el mapa completo de la aplicación sin prometer un destino que no existe**, y la promesa a medias es visible (gris, no clicable) en vez de ser un 404.

### 2. El navbar se reescribe en Bootstrap 5.3, porque la plantilla habla 4.x

La plantilla trae `navbar-light` (deprecado en 5.3 en favor de `data-bs-theme`), `navbar-toggleable-sm` (una clase que **no existe** en Bootstrap 5, resto de la 4) y una clase propia `box-shadow` definida en el CSS aislado. Se sustituye todo por `navbar navbar-expand-md bg-body-tertiary border-bottom`.

El `data-bs-target` pasa de `.navbar-collapse` —un **selector de clase**, que funciona solo mientras no haya un segundo `collapse` en la página— a `#mainNav`, un `id` real, con `aria-controls` apuntando al mismo sitio. La plantilla tenía además un `aria-controls="navbarSupportedContent"` que **no correspondía a ningún elemento del documento**.

### 3. El footer se pega con flexbox, no con la posición absoluta de la plantilla

`<body class="d-flex flex-column min-vh-100">`, `<main class="flex-grow-1">`, `<footer class="mt-auto">`.

*Descartado* conservar el mecanismo de la plantilla —`.footer { position: absolute; bottom: 0 }` en el CSS aislado más `body { margin-bottom: 60px }` y `html { position: relative; min-height: 100% }` en `site.css`— porque **codifica la altura del footer en tres archivos distintos**: asume que mide exactamente 60px, y el `white-space: nowrap` que lo acompañaba existía justamente para impedir que creciera. El footer de este punto tiene dos bloques y envuelve en pantalla estrecha, así que el mecanismo viejo se habría roto en el primer cambio. Con flexbox la altura del footer no la sabe nadie y no hace falta que la sepa.

### 4. El acento de marca vive en `site.css`, no en el CSS aislado — y el motivo es un override que llevaba muerto desde el día uno

`_Layout.cshtml.css` es CSS **aislado**: Razor añade un atributo `b-<hash>` a los elementos de ese archivo y reescribe cada selector como `footer[b-<hash>]`. La plantilla metía ahí `.btn-primary`, `.nav-pills .nav-link.active` y un `a { color }`, y **ninguno de los tres alcanzaba jamás a una vista hija** — un botón renderizado por `Index.cshtml` no lleva el hash del layout. Eran reglas muertas desde `0.1`.

Así que los colores salen de ahí y entran en `wwwroot/css/site.css`, que no está aislado, y además **como variables de Bootstrap (`--bs-primary`, `--bs-btn-bg`) en lugar de como reglas**: así el acento viaja solo a los botones, los enlaces y todo lo que la 5.3 deriva de esas variables, en vez de tener que perseguir cada selector.

### 5. Se limpia el andamiaje de Microsoft: Privacy desaparece entera

Se borran `Views/Home/Privacy.cshtml`, la acción `HomeController.Privacy()` y el enlace del footer que apuntaba a ella. Con ella se va el `button.accept-policy` del CSS aislado, que era el resto del banner de cookies que la plantilla nunca llegó a dibujar. La portada "Welcome / Learn about building Web apps with ASP.NET Core" se sustituye por la de la tienda.

*Descartado* dejarlo para el punto que lo sustituya: `Privacy` no lo sustituye ningún punto del roadmap, así que se quedaría para siempre — y el footer, que es entregable de `6.1`, enlazaba a ella.

### 6. La UI va en español; los identificadores siguen en inglés

`<html lang="es">`, y todo el texto visible en español. Es lo coherente con el resto del sistema que ve un usuario: el seed del catálogo son *Tazas, Llaveros, Playeras, Pines, Libretas* (`1.4`), los `Reason` de `StockRejected` son español (`3.4`) y los cuerpos de email de Notifications también (`4.6`). La convención de [CLAUDE.md](../CLAUDE.md) no se toca: clases, propiedades, acciones y nombres de archivo siguen en inglés.

### 7. `UseHttpsRedirection()` SE QUEDA, y eso no contradice a `5.1`

`5.1` se la quitó a `Catalog.API` y a `Orders.API` y lo escribió como reversión de `1.6`. El motivo era que esos dos están **detrás** del Gateway: su `307` devolvía `Location: https://localhost:7024/products`, o sea **la dirección real del servicio entregada al cliente a través del Gateway**, que es justo lo que la regla 3 existe para impedir.

`Shop133.Web` no está detrás de nada. Es la aplicación que abre el navegador, no la proxea nadie, y su perfil de depuración activo (`.csproj.user` → `https`) sirve en 7227. El argumento de `5.1` no se le aplica, y quitársela por simetría sería copiar la conclusión sin la premisa. Queda anotado en el propio `Program.cs`.

### 8. Se quita `app.UseAuthorization()`

No hay ningún esquema de autenticación registrado detrás, así que el middleware no puede autorizar nada: es ruido de plantilla. Vuelve en `8.1`, cuando el JWT del Gateway le dé algo que mirar.

### 9. No se añade `public partial class Program { }`, y el motivo es que la Fase 6 no tiene punto de test

`1.7`, `2.3` y `5.4` añadieron esa línea porque su suite usa `WebApplicationFactory<Program>`. Aquí no hay suite y **no la hay porque el roadmap no la pide**: los puntos de test son `0.6`, `1.7`, `2.4`, `3.7`, `4.7`, `5.4` y `8.2`/`8.6`, y la Fase 6 entera no tiene ninguno.

Se dice en voz alta en vez de dejarlo implícito, porque es exactamente el hueco que `4.6` dejó abierto y que nadie recogió: `4.7` era la máquina de estados, no aquel servicio. Añadir la línea "por si acaso" sería declarar una superficie de test que no existe. *Descartado* también proponer aquí un `tests/Frontend/Shop133.Web.Tests`: un proyecto nuevo necesita aprobación y esta decisión es de la fase, no de este punto.

---

## Cambios

| Archivo | Rol |
|---|---|
| `src/Frontend/Shop133.Web/Views/Shared/_Layout.cshtml` | Reescrito. `lang="es"`, navbar 5.3 con los cuatro items, footer con flexbox, sección `Styles` nueva, marcado del item activo desde `ViewContext.RouteData`. |
| `src/Frontend/Shop133.Web/Views/Shared/_Layout.cshtml.css` | Podado a una sola regla. Se documenta en el propio archivo qué alcanza el CSS aislado y qué no. |
| `src/Frontend/Shop133.Web/wwwroot/css/site.css` | Fuera el andamiaje del footer absoluto y el `font-size: 14px`. Entra el acento de marca como variables de Bootstrap y el `.navbar-brand` que el CSS aislado no puede alcanzar. |
| `src/Frontend/Shop133.Web/Views/Home/Index.cshtml` | Reescrito. Portada de la tienda con CTA deshabilitado y un bloque que nombra los cinco servicios y explica por qué el pedido tarda en confirmarse. |
| `src/Frontend/Shop133.Web/Views/Shared/Error.cshtml` | Traducido, conservando `RequestId` — que es el `traceparent` de `Activity.Current`, o sea lo que `7.3` va a querer para correlacionar. |
| `src/Frontend/Shop133.Web/Controllers/HomeController.cs` | Se borra la acción `Privacy()`. |
| `src/Frontend/Shop133.Web/Views/Home/Privacy.cshtml` | **Borrado.** |
| `src/Frontend/Shop133.Web/Program.cs` | Se quita `UseAuthorization()`. Se anota por qué `UseHttpsRedirection()` se queda. |

`Shop133.Web.csproj`, `launchSettings.json` y `appsettings.json` **no se tocan**. Lo segundo importa: `http://localhost:5025` y `https://localhost:7227` son exactamente los dos orígenes de `Cors:AllowedOrigins` del Gateway (`5.3`), así que mover un puerto rompería aquel punto y dos tests de `Shop133.Gateway.Tests`.

---

## Detalles que cuestan tiempo

- **Un elemento que pasa por un TAG HELPER sale renderizado SIN el atributo `b-<hash>` del CSS aislado.** Medido aquí, y es lo que obligó a mover una regla a mitad del punto: en el HTML servido, los tres `<a>` deshabilitados llevan `b-aivb9iabbi` y el `navbar-brand` y el enlace de `Inicio` —los dos que usan `asp-controller`/`asp-action`— **no lo llevan**. O sea que un `.navbar-brand { … }` escrito en `_Layout.cshtml.css` se compila a `.navbar-brand[b-aivb9iabbi]` y no engancha nunca. Es la misma clase de regla muerta que el `.btn-primary` de la plantilla, y no da ni un aviso: el CSS es válido, el archivo se sirve, el selector simplemente no encuentra a nadie.
- **Edge en modo headless recorta el ancho de ventana por debajo de ~500px y el resultado parece un desbordamiento horizontal.** Una captura con `--window-size=420,900` sale con el texto cortado por la derecha y el toggler a medias, exactamente como si algo forzara un ancho mínimo en el CSS. No lo hay: a 500 y a 700 la misma página sale perfecta. El navegador clampa la ventana a su mínimo, maqueta a ese ancho y **recorta la captura a los 420 pedidos**. Antes de perseguir un `overflow-x`, repite la captura a 500.
- **La plantilla codifica la altura del footer en tres archivos.** `.footer { position:absolute; bottom:0; line-height:60px }` en el CSS aislado, `body { margin-bottom: 60px }` y `html { position:relative; min-height:100% }` en `site.css`. Cambiar solo uno deja el footer flotando sobre el contenido o un hueco blanco al final, y nada lo señala.
- **El `aria-controls="navbarSupportedContent"` de la plantilla no apunta a ningún elemento existente.** Convive con un `data-bs-target=".navbar-collapse"` que sí funciona, así que el collapse abre y el fallo de accesibilidad no se nota nunca.
- **Razor omite un atributo cuyo valor es `null`**, que es lo que hace legible el marcado del item activo: `aria-current="@(currentController == "Home" ? "page" : null)"` renderiza el atributo o no renderiza nada, sin necesidad de un `if` alrededor del `<a>`.

---

## Verificación

### 1. Build limpio

Es lo que prueba que el glob implícito recogió los `.cshtml` y que compilan — los errores de Razor no aparecen hasta aquí.

```
> dotnet build src\Frontend\Shop133.Web\Shop133.Web.csproj
  Shop133.Web -> C:\personalprojects\shop133\src\Frontend\Shop133.Web\bin\Debug\net10.0\Shop133.Web.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### 2. Códigos de estado

```
> foreach ($p in @("/", "/Home/Error", "/Home/Privacy", "/lib/bootstrap/dist/css/bootstrap.min.css")) { ... }
/                                             200
/Home/Error                                   200
/Home/Privacy                                 404
/lib/bootstrap/dist/css/bootstrap.min.css     200
```

El `404` de `/Home/Privacy` es la prueba de que la acción y la vista se fueron de verdad, no de que la vista esté huérfana. El `200` del CSS confirma que `MapStaticAssets()` sigue sirviendo el dist vendorizado.

### 3. Marcadores en el HTML servido

```
<html lang="es">                         PRESENTE
<title>Inicio - Shop133</title>          PRESENTE
aria-disabled="true"                     PRESENTE
id="mainNav"                             PRESENTE
data-bs-target="#mainNav"                PRESENTE
d-flex flex-column min-vh-100            PRESENTE
flex-grow-1                              PRESENTE
mt-auto border-top                       PRESENTE
nav-link active                          PRESENTE
aria-current="page"                      PRESENTE
navbar-light                             ausente
Privacy                                  ausente
```

Las dos últimas líneas son las que cierran el punto: `navbar-light` era la nomenclatura 4.x y `Privacy` el andamiaje.

### 4. El navbar renderizado, que es donde salió el hallazgo del CSS aislado

```html
<a class="navbar-brand fw-semibold" href="/">Shop133</a>
<a class="nav-link active" aria-current="page" href="/">Inicio</a>
<a b-aivb9iabbi class="nav-link disabled" aria-disabled="true">Catálogo</a>
<a b-aivb9iabbi class="nav-link disabled" aria-disabled="true">Carrito</a>
<a b-aivb9iabbi class="nav-link disabled" aria-disabled="true">Estado del pedido</a>
<main b-aivb9iabbi role="main" class="container flex-grow-1 pb-3">
<footer b-aivb9iabbi class="mt-auto border-top py-3 text-body-secondary">
```

Los dos primeros pasan por el `AnchorTagHelper` y salen **sin** el `b-aivb9iabbi`. Ver el primer detalle de la sección anterior.

### 5. Comprobación visual, con capturas reales

Capturas con Edge headless (`--headless=new --disable-gpu --screenshot`) a tres anchos:

| Ancho | Resultado |
|---|---|
| 1280 | Menú desplegado con `Inicio` activo y los tres siguientes en gris; las dos columnas de la portada lado a lado; **footer abajo del todo en una página que no llena la pantalla** — que es justo lo que el mecanismo absoluto de la plantilla hacía mal. |
| 700 | Toggler visible y bien colocado, columnas apiladas (`col-md-6` por debajo de 768), sin desbordamiento. |
| 500 | Igual, sin recortes. |
| 420 | **Texto cortado por la derecha y toggler a medias** — artefacto del headless, no del CSS. Ver el segundo detalle de la sección anterior. |

`/Home/Error` a 420 muestra además el footer envolviendo en dos líneas, que es lo que el `white-space: nowrap` de la plantilla impedía.

**Lo que NO se verificó y se dice en voz alta:** que el toggler *abra* el collapse. Una captura estática no puede enseñarlo y no se montó automatización de clic. Lo que sí se comprobó es el único modo de fallo real —que el `id` del `div` y el `data-bs-target` del botón coincidan— y que `bootstrap.bundle.min.js` se sirve con `200`.

### 6. La suite de arquitectura no se mueve

```
> dotnet tests\Shop133.ArchitectureTests\bin\Debug\net10.0\Shop133.ArchitectureTests.dll
=== TEST EXECUTION SUMMARY ===
   Shop133.ArchitectureTests  Total: 17, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.961s
```

`Frontend_DoesNotReference_ServicesOrGateway` sigue en verde con `Shop133.Web` en sus cero `ProjectReference`.

### 7. Los archivos tocados son los ocho previstos

```
> git status --short
 M src/Frontend/Shop133.Web/Controllers/HomeController.cs
 M src/Frontend/Shop133.Web/Program.cs
 M src/Frontend/Shop133.Web/Views/Home/Index.cshtml
 D src/Frontend/Shop133.Web/Views/Home/Privacy.cshtml
 M src/Frontend/Shop133.Web/Views/Shared/Error.cshtml
 M src/Frontend/Shop133.Web/Views/Shared/_Layout.cshtml
 M src/Frontend/Shop133.Web/Views/Shared/_Layout.cshtml.css
 M src/Frontend/Shop133.Web/wwwroot/css/site.css
```

Ningún `.csproj`, ningún `appsettings.json`, ningún `launchSettings.json`.

---

## Pendiente

- **La Fase 6 no tiene ningún punto de test en el roadmap**, así que nada de `6.1` está cubierto y nada lo va a cubrir por sí solo. Es el mismo hueco sin dueño que dejó `4.6`. El día que se recoja, la forma sería `Shop133.Gateway.Tests`: `WebApplicationFactory` sin Docker, `Category=Fast`, y entonces sí haría falta el `public partial class Program { }` de la decisión 9. Un proyecto nuevo necesita aprobación.
- **Los tres enlaces deshabilitados los activa quien los sirva**: `Catálogo` en `6.2`, `Carrito` en `6.3`, `Estado del pedido` en `6.5`. El CTA de la portada va con `6.2`.
- **Contenedor de toasts y `site.js`**: `6.7`. `site.js` sigue vacío a propósito.
- **La URL base del Gateway** (`http://localhost:5104`) no está en `appsettings.json` todavía: entra con la primera llamada, en `6.2`, y su política de resiliencia en `6.6`.
- **Sin `Dockerfile` ni servicio en compose** para el frontend, y **sin conmutador de modo oscuro** — Bootstrap 5.3 trae `data-bs-theme` y el layout ya no lo bloquea (se quitó `navbar-light`), pero el conmutador no lo pide ningún punto.
- **La cabecera `Location` del `201` de `POST /api/orders` sigue apuntando al backend sin prefijo**, deuda medida en `5.1` y releída en `5.3`, todavía sin dueño. Duele en `6.5`.
