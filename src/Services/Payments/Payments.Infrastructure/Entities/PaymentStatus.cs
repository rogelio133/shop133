namespace Payments.Infrastructure.Entities;

/// <summary>
/// El desenlace de un cobro. Un cobro nace ya resuelto: la pasarela simulada de
/// 3.5 responde en el mismo <c>Consume</c>, así que no existe un estado
/// intermedio "en proceso".
///
/// **Es un enum y no una tabla de lookup**, al revés que <c>Category</c> en 1.4
/// y con el mismo criterio que <c>OrderStatus</c> en 2.1: una tabla gana cuando
/// el conjunto puede crecer sin recompilar, y un desenlace nuevo de un cobro
/// —un reembolso, un cobro pendiente de 3DS— trae código nuevo de todas formas.
///
/// Los valores van **explícitos** porque EF persiste el ordinal: insertar uno
/// en medio de la lista renumeraría en silencio todas las filas ya guardadas.
/// Los estados nuevos van al final, nunca en medio.
/// </summary>
public enum PaymentStatus
{
    /// <summary>Cobrado. Hay <c>TransactionId</c> y no hay motivo de fallo.</summary>
    Completed = 1,

    /// <summary>Rechazado. Hay motivo de fallo y no hay <c>TransactionId</c>.</summary>
    Failed = 2,
}
