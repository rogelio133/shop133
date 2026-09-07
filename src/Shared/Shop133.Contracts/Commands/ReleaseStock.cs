namespace Shop133.Contracts.Commands;

/// <summary>
/// La compensación. Enviado por la saga a Inventory.API cuando el pago falla
/// después de que el stock se haya reservado, para devolver las unidades.
///
/// No existe ningún camino en el que un StockReserved acabe en Cancelled sin
/// pasar por aquí; si lo hubiera, el stock quedaría bloqueado para siempre sin
/// que nadie lo notara. Desde 4.4 eso es literal: la saga no pasa a Cancelled
/// por el camino del pago rechazado hasta que Inventory contesta StockReleased.
///
/// Su consumidor tiene que ser idempotente de verdad: un ReleaseStock
/// entregado dos veces devolvería el stock dos veces, creando unidades de la
/// nada. Es peor que un duplicado de ReserveStock, que solo bloquea de más.
///
/// ── Por qué ya no lleva Lines (decidido en 4.4) ──
///
/// Hasta 4.4 este comando llevaba, como ReserveStock, la lista completa de
/// OrderLine. La sección Pendiente de docs/fase_3_2.md dejó por escrito que la
/// decisión se tomaría "cuando se supiera cómo quedó la tabla de reservas", y la
/// decisión 6 de docs/fase_3_4.md la dejó cerrada de hecho sin cerrarla de
/// nombre: **la clave primaria de StockReservations *es* el OrderId**, con las
/// líneas colgando de ella. Así que soltar el stock de un pedido es un SELECT
/// por clave primaria, y repetir las líneas aquí sería mandarle a Inventory un
/// dato que ya tiene.
///
/// Que sea redundante no es el único motivo: sería una **segunda fuente para lo
/// mismo**, y las dos podrían contradecirse. Un ReleaseStock cuyas líneas no
/// coincidieran con la reserva obligaría al consumer a decidir a cuál hace caso
/// — una pregunta sin respuesta buena que desaparece si el dato no viaja.
///
/// El tercer motivo mira a 4.5: las líneas tendrían que estar en algún sitio
/// para poder mandarlas, y ese sitio sería OrderState, la instancia de la saga.
/// Persistir una colección en la fila de la saga (columna JSON o tipo owned) es
/// coste real, y sería para devolverle a Inventory lo que Inventory le dijo a la
/// saga.
///
/// *Descartado* conservarlas para que el consumer no dependa de encontrar la
/// fila de reserva. No es una ventaja: si la fila no está, el estado es
/// incoherente —solo se manda ReleaseStock a un pedido cuyo StockReserved
/// publicó el propio Inventory— y soltar unidades a ciegas guiándose por el
/// mensaje sería inventarse una reserva que nadie registró.
///
/// Este cambio es incompatible bajo la regla 4 de CLAUDE.md, y salió gratis
/// porque en 4.4 el comando todavía no tenía ni un consumidor en todo el
/// repositorio: ningún proyecto dejó de compilar. ReserveStock sigue con sus
/// Lines y sigue sin llamante — ver la decisión 2 de docs/fase_4_1.md.
/// </summary>
public sealed record ReleaseStock
{
    public required Guid OrderId { get; init; }
}
