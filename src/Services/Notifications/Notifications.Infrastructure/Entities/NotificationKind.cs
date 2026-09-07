namespace Notifications.Infrastructure.Entities;

/// <summary>
/// Qué clase de aviso se le mandó al cliente. Hay exactamente dos porque la saga
/// tiene exactamente dos desenlaces: <c>OrderConfirmed</c> y <c>OrderCancelled</c>.
///
/// **Es un enum y no una tabla de lookup**, al revés que <c>Category</c> en 1.4 y
/// con el mismo criterio que <c>OrderStatus</c> (2.1) y <c>PaymentStatus</c>
/// (3.5): una tabla gana cuando el conjunto puede crecer sin recompilar, y una
/// notificación nueva significa un evento nuevo, un consumer nuevo y una cola
/// nueva. Nunca aparece sola.
///
/// Los valores van **explícitos** porque EF persiste el ordinal: insertar uno en
/// medio de la lista renumeraría en silencio todas las filas ya guardadas, y aquí
/// duele el doble porque el valor es además **la mitad de la clave primaria** (ver
/// <c>NotificationConfiguration</c>). Los valores nuevos van al final, nunca en
/// medio.
/// </summary>
public enum NotificationKind
{
    /// <summary>El pedido salió bien: stock reservado y pago cobrado.</summary>
    Confirmation = 1,

    /// <summary>
    /// El pedido no llegó a completarse, por cualquiera de los dos caminos de
    /// error. **No se distingue cuál**: el <c>///</c> de <c>OrderCancelled</c> dice
    /// desde 0.3 que para eso está el <c>Reason</c>, y aquí ese texto acaba dentro
    /// del cuerpo del email. Partir esto en dos valores obligaría al consumer a
    /// deducir el motivo de un texto libre, que es justo lo que ese campo dice que
    /// nadie debe parsear.
    /// </summary>
    Cancellation = 2,
}
