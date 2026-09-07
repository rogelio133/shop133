namespace Catalog.API;

/// <summary>
/// Los parámetros con los que 4.8 decide si la foto de precios de un pedido es
/// auténtica. Se enlazan desde la sección <c>Catalog</c> de appsettings.json.
///
/// **Van en appsettings.json y no en User Secrets**, y **sin guarda que reviente
/// al arrancar**: es el criterio literal de <c>PaymentSimulationOptions</c> en
/// Payments.API. No es un secreto, es una política de negocio que interesa que se
/// lea de un vistazo; y a diferencia de cualquier clave de
/// <c>ConnectionStrings</c>, su ausencia no deja el servicio a medias — hay un
/// valor por defecto sensato y el catálogo funciona igual. Una guarda aquí
/// convertiría un archivo opcional en obligatorio a cambio de nada.
///
/// **Y por qué no es una constante en el consumer ni en <c>Product</c>.** La
/// ventana no es una invariante de un producto: un producto no sabe cuánto dura un
/// checkout. Tampoco es un detalle de implementación de un consumer. Es una
/// *política* —cuánto tiempo se honra un precio que ya se ofreció—, la misma
/// especie que <c>Payments:DeclineAmountAbove</c>, y la pregunta que va a llegar
/// algún día ("el checkout se nos ha vuelto más lento, ensáncchala") no debería
/// ser una recompilación.
/// </summary>
public sealed class PricingValidationOptions
{
    /// <summary>El nombre de la sección de configuración.</summary>
    public const string SectionName = "Catalog";

    /// <summary>
    /// Cuántos minutos sigue siendo auténtico el precio anterior de un producto
    /// después de habérselo cambiado.
    ///
    /// **Es lo que separa esta validación de la que el roadmap llama incorrecta.**
    /// Comparar la foto contra el precio de hoy a secas rechazaría un pedido
    /// legítimo cuyo precio cambió a mitad del checkout, y congelar el precio que
    /// el cliente vio es el comportamiento correcto — todo el <c>///</c> de
    /// <c>Shop133.Contracts.OrderLine</c> existe para decir eso. Lo que se valida
    /// es que el precio de la foto sea uno que Catalog **llegó a ofrecer**, y esta
    /// ventana es el "y hace poco".
    ///
    /// 30 minutos por defecto: de sobra para un checkout humano y lo bastante corto
    /// para que un precio retirado no siga cobrándose al día siguiente. No hay
    /// medición detrás del número — no existe todavía el checkout de la Fase 6 con
    /// el que medirlo—, y por eso está en configuración y no clavado en el código.
    ///
    /// ── Minutos y no un TimeSpan, y no es capricho ──
    ///
    /// <c>IConfiguration</c> sabe enlazar <c>"00:30:00"</c> a un <c>TimeSpan</c>,
    /// pero un formato mal escrito no falla al arrancar: falla cuando alguien lee
    /// <c>IOptions.Value</c> por primera vez, o sea **dentro del consumer**, con lo
    /// que el mensaje acabaría en <c>order-created-pricing_error</c> a varios saltos
    /// de la causa. Eso anularía en silencio el argumento de "sin guarda no pasa
    /// nada" que justifica no poner una. Un <c>int</c> no se puede teclear en esa
    /// trampa.
    /// </summary>
    public int PricingSnapshotWindowMinutes { get; set; } = 30;

    /// <summary>
    /// La ventana ya en la unidad con la que hablan <c>Product.IsAuthenticPrice</c>
    /// y el consumer. Calculada, no enlazada: así la configuración se declara en la
    /// unidad segura de escribir y el código trabaja con la que expresa la
    /// intención.
    /// </summary>
    public TimeSpan SnapshotWindow => TimeSpan.FromMinutes(PricingSnapshotWindowMinutes);
}
