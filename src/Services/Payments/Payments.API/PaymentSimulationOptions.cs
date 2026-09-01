namespace Payments.API;

/// <summary>
/// Los parámetros de la pasarela simulada. Se enlazan desde la sección
/// <c>Payments</c> de appsettings.json.
///
/// **Van en appsettings.json y no en User Secrets**: no es un secreto, es una
/// regla de negocio de mentira que interesa que se lea de un vistazo. Mismo
/// criterio con el que <c>Services:CatalogBaseUrl</c> vivía en appsettings.json
/// en 2.3, frente a los connection strings y al URI del broker, que llevan
/// credenciales y no pueden estar versionados.
///
/// **Y sin guarda que reviente al arrancar**, al contrario que todas las claves
/// de <c>ConnectionStrings</c> de este repo. La diferencia no es de estilo: sin
/// connection string el servicio no puede hacer *nada* y conviene que muera
/// diciendo qué falta, mientras que sin esta sección hay un valor por defecto
/// perfectamente sensato y el servicio funciona. Una guarda aquí convertiría un
/// archivo opcional en obligatorio a cambio de nada.
/// </summary>
public sealed class PaymentSimulationOptions
{
    /// <summary>El nombre de la sección de configuración.</summary>
    public const string SectionName = "Payments";

    /// <summary>
    /// El límite por encima del cual la pasarela simulada rechaza el cobro.
    ///
    /// **Determinista y en función del importe, no aleatorio**, y esa es la
    /// decisión que más se va a notar en la Fase 4: el escenario 3 obligatorio
    /// —stock reservado y pago rechazado, o sea la compensación— tiene que poder
    /// forzarse a demanda, y con este umbral forzarlo es "pide más caro". Con un
    /// porcentaje de fallo aleatorio ese escenario llegaría por suerte, y un test
    /// del harness sobre un consumer aleatorio o inyecta el Random detrás de una
    /// interfaz o es intermitente, que es la peor clase de test.
    ///
    /// Frente a un interruptor global de "rechaza todo", esto permite además que
    /// en la misma ejecución del sistema convivan un pedido que pasa y otro que
    /// falla — que es justo lo que la página de estado de 6.5 y la traza de 7.4
    /// existen para enseñar.
    ///
    /// El valor por defecto de 1000 está elegido contra el catálogo de souvenirs
    /// de 1.4, cuyo producto más caro son 399.00: un pedido normal pasa solo, y
    /// para llegar al rechazo hay que pedir tres unidades del más caro. El camino
    /// feliz es el que sale por defecto; el otro hay que quererlo.
    /// </summary>
    public decimal DeclineAmountAbove { get; set; } = 1000m;
}
