using MassTransit;

using Shop133.Contracts.Commands;
using Shop133.Contracts.Events;

namespace Orders.Tests.Infrastructure;

/// <summary>
/// El doble de Inventory dentro del harness: recibe el <c>ReleaseStock</c> que manda la saga
/// y contesta con <c>StockReleased</c>, que es lo que la saca de <c>CompensatingStock</c>.
///
/// **Existe para que el escenario 3 pueda afirmar lo que el roadmap pide.** Sin alguien que
/// conteste, la saga se queda esperando y no hay forma de comprobar ni el estado final
/// <c>Cancelled</c> ni el <c>OrderCancelled</c> que sale al llegar allí. El comando y su
/// respuesta ocurren **dentro de la misma etapa de bus** que las publicaciones del test, así
/// que el <c>InactivityTask</c> —que es de un solo uso, trampa 1 de 3.7— sigue cubriendo la
/// cadena entera con un solo <c>await</c>.
///
/// *Descartado* publicar el <c>StockReleased</c> desde el propio test después de comprobar
/// que la saga llegó a <c>CompensatingStock</c>: serían dos etapas de bus, exactamente el
/// fallo que 4.4 midió en <c>Inventory.Tests</c> (dos de cuatro tests en rojo y dos en verde
/// en la misma ejecución), y con el <c>InactivityTask</c> gastado solo se podría afirmar "al
/// menos uno", nunca "exactamente uno".
///
/// *Descartado* referenciar el <c>ReleaseStockConsumer</c> de verdad, el de Inventory.API:
/// haría que <c>Orders.Tests</c> referenciara otro servicio y necesitara <c>InventoryDb</c>
/// para probar una máquina de estados que no sabe nada de stock. Lo que se prueba aquí es la
/// saga, no la compensación de Inventory — ésa ya la cubre <c>ReleaseStockConsumerTests</c>
/// desde 4.4, y este espía es el otro extremo del mismo cable.
///
/// **No es idempotente y no debe serlo.** La regla 6 aplica a los consumers del sistema, no a
/// un doble de test: aquí interesa justamente contar cuántos comandos llegan, así que un
/// duplicado tiene que producir dos entradas y no una.
/// </summary>
public sealed class ReleaseStockSpyConsumer(ReleaseStockSpySwitch spySwitch) : IConsumer<ReleaseStock>
{
    public async Task Consume(ConsumeContext<ReleaseStock> context)
    {
        if (!spySwitch.Answers)
        {
            return;
        }

        await context.Publish(new StockReleased { OrderId = context.Message.OrderId });
    }
}

/// <summary>
/// El interruptor que decide si el espía contesta.
///
/// Existe para un solo test —el que comprueba que **el pedido no se cancela hasta que el
/// stock esté suelto**, que es la mitad de la regla 7 que solo se ve cuando Inventory calla—
/// y se registra como singleton en el host, así que cada test estrena el suyo.
///
/// *Descartado* un <c>static bool</c>: los tests de la clase <c>Fast</c> corren en paralelo
/// con los de otras collections, y un interruptor de proceso los acoplaría entre sí de una
/// forma que solo se manifestaría de vez en cuando.
/// </summary>
public sealed class ReleaseStockSpySwitch
{
    public bool Answers { get; set; } = true;
}
