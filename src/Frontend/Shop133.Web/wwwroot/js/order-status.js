// shop133 — sondeo del estado del pedido (6.5)
//
// El PRIMER JavaScript propio del proyecto. site.js sigue siendo la plantilla vacia de 0.1, y
// hasta aqui todo lo que Shop133.Web pintaba lo pintaba el servidor.
//
// ── Este fetch sale del NAVEGADOR y va DIRECTO al Gateway ──
//
// No pasa por Shop133.Web, y esa es la decision del punto. Descartado sondear contra una accion de
// este proyecto que reenviara al Gateway (mismo origen, ni CORS ni contenido mixto): el frontend
// renderiza en SERVIDOR, asi que para el rate limiter de 5.2 todos los visitantes serian UNA sola
// IP — 6.2 lo midio, el primer 429 llega en el render 30. Con el sondeo aqui, cada visitante gasta
// su propio cupo.
//
// Es ademas el primer consumidor de CORS. 5.3 dejo escrito que quien lo necesitaba de verdad era
// "el JavaScript de 6.5 (el polling del estado del pedido)", y hasta hoy no existia: las llamadas
// del checkout y del catalogo son servidor-a-servidor y CORS no las toca. Esta peticion es un GET
// sin cabeceras propias, o sea una peticion SIMPLE: no hay preflight, solo la comprobacion del
// Origin en la respuesta.
//
// ── El mapa de estados esta DUPLICADO ──
//
// Lo mismo que hay en Models/OrderProgress.cs. Es duplicacion consciente y anotada en los dos
// sitios: el servidor pinta el primer render y este archivo pinta los siguientes, asi que los dos
// tienen que saber traducir los nueve estados de la saga. Descartado que el servidor devolviera el
// HTML ya pintado en cada vuelta — gastaria el mismo cupo para mandar marcado en vez de seis
// campos, y obligaria a que el sondeo pasara por Shop133.Web, que es justo lo que arruina la
// particion por IP de arriba.
//
// Si los dos mapas divergen, el sintoma es que la pagina cambia de texto sola en la primera vuelta
// del sondeo. No lo vigila nada.

(function () {
    "use strict";

    var root = document.getElementById("order-status");

    if (!root) {
        return;
    }

    // Cada 2 s, que es lo que plantea el roadmap. Contra el cupo orders-read de 60/60 s que 6.5
    // anadio al Gateway son 30 por minuto: la mitad. Antes de partir la ruta esto habria chocado
    // con los 10/60 s de escritura a los ~25 segundos, que es la medicion con la que 6.4 dejo la
    // decision escrita para este punto.
    var INTERVAL_MS = 2000;

    // ~2 minutos. El tope no es prudencia: es la unica forma honesta de ensenar que NI
    // PricingPending NI CompensatingStock tienen timeout — un hueco sin duenno desde 4.4 y 4.9. Un
    // pedido atascado ahi no se resuelve nunca, y sondearlo para siempre gastaria cupo en silencio.
    // Al llegar al tope la pagina lo dice y ofrece recargar.
    var MAX_ATTEMPTS = 60;

    // Dos seguidos y para. Uno suelto puede ser un corte de red de un instante; dos significa que
    // el Gateway no esta, y seguir intentandolo cada 2 s no lo va a traer de vuelta.
    var MAX_CONSECUTIVE_FAILURES = 2;

    var orderId = root.dataset.orderId;
    var statusUrl = root.dataset.gateway + "/api/orders/" + orderId + "/status";

    var attempts = 0;
    var failures = 0;
    var timer = null;

    function node(role) {
        return root.querySelector('[data-role="' + role + '"]');
    }

    // El mismo switch que OrderProgress.Tracks, en el mismo orden y con los mismos textos.
    function tracks(stage) {
        switch (stage) {
            case null:
            case undefined:
                return [
                    ["Esperando a que arranque el pedido.", "waiting"],
                    ["Esperando a que arranque el pedido.", "waiting"]
                ];
            case "PricingPending":
                return [
                    ["Comprobando que el precio que viste sigue siendo el bueno.", "running"],
                    ["Apartando las unidades del almacén.", "running"]
                ];
            case "PricingPendingStockReserved":
                return [
                    ["Comprobando que el precio que viste sigue siendo el bueno.", "running"],
                    ["Unidades apartadas. Cobrando.", "running"]
                ];
            case "PricingPendingPaymentCompleted":
                return [
                    ["Comprobando que el precio que viste sigue siendo el bueno.", "running"],
                    ["Cobrado. Falta la última comprobación.", "done"]
                ];
            case "StockPending":
                return [
                    ["Precio confirmado.", "done"],
                    ["Apartando las unidades del almacén.", "running"]
                ];
            case "PaymentPending":
                return [
                    ["Precio confirmado.", "done"],
                    ["Unidades apartadas. Cobrando.", "running"]
                ];
            case "CancellingStockPending":
                return [
                    ["El precio ya no es válido.", "failed"],
                    ["Esperando al almacén para saber si hay algo que devolver.", "compensating"]
                ];
            case "CompensatingStock":
                return [
                    ["El pedido no salió adelante.", "failed"],
                    ["Devolviendo las unidades al almacén.", "compensating"]
                ];
            case "Confirmed":
                return [
                    ["Precio confirmado.", "done"],
                    ["Unidades apartadas y cobro aceptado.", "done"]
                ];
            case "Cancelled":
                return [
                    ["El pedido no salió adelante.", "failed"],
                    ["No queda nada apartado ni cobrado.", "failed"]
                ];
            default:
                // Un estado que la saga tiene y este archivo no conoce: se ensena crudo en vez de
                // inventarle un texto. Feo, pero deja ver que hay dos mapas que actualizar.
                return [
                    ["Estado desconocido: " + stage + ".", "running"],
                    ["Estado desconocido: " + stage + ".", "running"]
                ];
        }
    }

    // Las mismas clases y etiquetas que OrderProgress.BadgeClass y OrderProgress.Label.
    var BADGES = {
        done: ["text-bg-success", "Listo"],
        failed: ["text-bg-danger", "Fallido"],
        compensating: ["text-bg-warning", "Deshaciendo"],
        running: ["text-bg-primary", "En curso"],
        waiting: ["text-bg-secondary", "Esperando"]
    };

    function paintTrack(name, track) {
        var badge = node(name + "-badge");
        var detail = node(name + "-detail");
        var style = BADGES[track[1]] || BADGES.waiting;

        badge.className = "badge " + style[0];
        badge.textContent = style[1];
        detail.textContent = track[0];
    }

    function paint(status) {
        var outcome = node("outcome");
        var title = node("outcome-title");

        if (status.status === "Confirmed") {
            outcome.className = "alert alert-success d-flex flex-wrap align-items-center gap-2";
            title.textContent = "Pedido confirmado";
        } else if (status.status === "Cancelled") {
            outcome.className = "alert alert-danger d-flex flex-wrap align-items-center gap-2";
            title.textContent = "Pedido cancelado";
        } else {
            outcome.className = "alert alert-info d-flex flex-wrap align-items-center gap-2";
            title.textContent = "Pedido en curso";
        }

        var pair = tracks(status.stage);
        paintTrack("pricing", pair[0]);
        paintTrack("fulfilment", pair[1]);

        // textContent y nunca innerHTML: este texto lo compone Inventory concatenando una frase por
        // linea que fallo, o Payments con el importe. Viene de otro servicio, asi que se pinta como
        // TEXTO — con innerHTML, cualquier cosa que acabara ahi dentro se ejecutaria.
        var reason = node("reason");

        if (status.cancellationReason) {
            reason.textContent = status.cancellationReason;
            reason.classList.remove("d-none");
        } else {
            reason.textContent = "";
            reason.classList.add("d-none");
        }

        node("stage").textContent = status.stage || "(la saga aún no ha arrancado)";
    }

    function stopSpinner() {
        var spinner = node("spinner");

        if (spinner) {
            spinner.remove();
        }
    }

    function notice(message) {
        var box = node("polling-notice");

        box.textContent = message;
        box.classList.remove("d-none");
    }

    function stop(message) {
        if (timer) {
            window.clearTimeout(timer);
            timer = null;
        }

        stopSpinner();

        if (message) {
            notice(message);
        }
    }

    function schedule() {
        timer = window.setTimeout(poll, INTERVAL_MS);
    }

    function poll() {
        attempts += 1;

        if (attempts > MAX_ATTEMPTS) {
            // El pedido sigue en marcha y esta pagina deja de mirar. Se dice tal cual: la
            // alternativa —sondear para siempre— escondería que hay estados de la saga sin timeout.
            stop("El pedido sigue en curso y hemos dejado de comprobarlo para no saturar la tienda. "
                + "Recarga la página para volver a mirar.");
            return;
        }

        fetch(statusUrl, { headers: { "Accept": "application/json" } })
            .then(function (response) {
                if (response.status === 404) {
                    // Solo puede pasar si el pedido se borro entre el render y el sondeo. Se para:
                    // reintentar no lo va a devolver.
                    stop("Ese pedido ya no existe.");
                    return null;
                }

                if (response.status === 429) {
                    // El cupo orders-read agotado. Retry-After solo se puede LEER porque 5.3 lo
                    // expuso con WithExposedHeaders: por defecto el navegador deja ver el 429 y
                    // esconde el unico dato que lo hace accionable. Ver 5.2 para por que el numero
                    // es la ventana entera y no lo que queda de ella.
                    var retryAfter = response.headers.get("Retry-After");

                    stop("Demasiadas consultas seguidas. Vuelve a intentarlo en "
                        + (retryAfter || "60") + " segundos.");
                    return null;
                }

                if (!response.ok) {
                    throw new Error("HTTP " + response.status);
                }

                return response.json();
            })
            .then(function (status) {
                if (!status) {
                    return;
                }

                failures = 0;
                paint(status);

                // isFinal lo calcula Orders y mira el estado del PEDIDO, no el de la saga: la saga
                // llega a Confirmed un mensaje antes de que el pedido lo haga, asi que pararse con
                // la etapa dejaria la pagina ensenando "en curso" para siempre.
                if (status.isFinal) {
                    stop(null);
                    return;
                }

                schedule();
            })
            .catch(function () {
                failures += 1;

                if (failures >= MAX_CONSECUTIVE_FAILURES) {
                    // El mismo diagnostico que la vista Unavailable, en pequeno: esta tienda habla
                    // con un unico Gateway, asi que si no contesta no hay plan B — y no tenerlo es
                    // la regla 3 funcionando, no un fallo del diseno.
                    stop("No se puede contactar con la tienda para actualizar el estado. "
                        + "Los datos de arriba son los de la última comprobación que sí funcionó.");
                    return;
                }

                schedule();
            });
    }

    // data-poll lo decide el servidor: un pedido ya resuelto se pinta y se deja quieto, porque
    // sondear un desenlace que no va a cambiar es gastar cupo por nada.
    if (root.dataset.poll === "true") {
        schedule();
    }
})();
