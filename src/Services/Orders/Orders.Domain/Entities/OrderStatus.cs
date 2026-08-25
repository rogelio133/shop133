namespace Orders.Domain.Entities;

/// <summary>
/// El estado de un pedido. Son los tres que nombra el punto 2.1 del roadmap, y
/// también los tres estados *finales* que la OrderStateMachine de la Fase 4
/// puede dejar en <c>OrdersDb</c>.
///
/// ── Un enum, no una tabla de catálogo ──
///
/// Es la decisión opuesta a la que tomó 1.4 con <c>Category</c>, y el contraste
/// es lo que la justifica:
///
/// - Una **categoría** es texto de interfaz. Crece con un INSERT, la escribe
///   quien administra el catálogo y el nombre que ve el usuario no debe quedar
///   atrapado dentro de un identificador de C#. Por eso es una tabla.
/// - Un **estado de pedido** es una rama de la máquina de estados. Añadir uno
///   significa escribir la transición que lleva a él y el evento que lo publica,
///   o sea recompilar y desplegar Orders.Domain de todas formas. Una tabla no
///   ahorraría ese despliegue: solo añadiría un JOIN y una clave foránea para
///   sostener un dato que el código ya tiene que conocer por su nombre.
///
/// Dicho de otro modo: la tabla gana cuando el conjunto puede crecer **sin
/// tocar código**, y aquí no puede.
///
/// ── Los valores son explícitos a propósito ──
///
/// EF Core persiste un enum como su valor numérico (2.2). Sin los números
/// escritos, ese valor depende del *orden de declaración*: insertar un estado
/// nuevo en medio de la lista renumeraría en silencio todas las filas ya
/// guardadas. Con <c>= 1, = 2, = 3</c> el contrato con la base de datos es
/// visible y no se puede romper por reordenar.
///
/// ── Estados intermedios de la saga ──
///
/// La Fase 4.2 define un recorrido más largo — StockPending, StockReserved,
/// PaymentPending, CompensatingStock — pero esos son estados de la **instancia
/// de saga**, no del pedido. Van en el tipo de estado de la saga que persiste
/// 4.5, no aquí. Si al llegar a 4.2 resulta que el pedido también los necesita,
/// se añaden **al final** de esta lista, nunca en medio (ver el párrafo de
/// arriba).
/// </summary>
public enum OrderStatus
{
    /// <summary>
    /// Registrado y aceptado, sin resolver. Es el único estado alcanzable en la
    /// Fase 2: no hay todavía nada que confirme ni que cancele un pedido.
    /// </summary>
    Pending = 1,

    /// <summary>Stock reservado y pago cobrado. Camino feliz de la saga.</summary>
    Confirmed = 2,

    /// <summary>
    /// Terminado sin completarse, por cualquiera de los dos caminos de error:
    /// no había stock, o el pago se rechazó y el stock se liberó con
    /// ReleaseStock. El pedido no distingue cuál — el motivo viaja en
    /// <c>OrderCancelled.Reason</c>.
    /// </summary>
    Cancelled = 3,
}
