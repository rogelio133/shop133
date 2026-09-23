using System.Net;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;

using Orders.API.Models;
using Orders.Domain.Entities;
using Orders.Domain.Sagas;
using Orders.Tests.Infrastructure;

using Shop133.TestUtilities;

using Xunit;

namespace Orders.Tests;

/// <summary>
/// <c>GET /orders/{id}/status</c>, el endpoint que 6.5 estrena para la página de
/// estado del pedido.
///
/// <para>
/// **Lo que estos tests fijan es que el endpoint junta dos tablas que no se
/// conocen.** <c>Orders</c> dice el desenlace —tres valores— y <c>OrderStates</c>
/// dice por dónde va el proceso —nueve desde 4.9—, y entre las dos no hay clave
/// foránea ni navegación: solo comparten el valor de la clave primaria. Un punto
/// que "simplificara" el endpoint leyendo únicamente <c>Orders</c> dejaría la
/// página sin ninguna etapa intermedia que enseñar, que es exactamente la
/// contradicción que 6.5 tuvo que resolver entre dos comentarios del repositorio.
/// </para>
///
/// <para>
/// **La fila de saga se siembra a mano** con <see cref="OrdersApiFactory.WithDbAsync"/>,
/// no la escribe la saga: esta fábrica desmonta MassTransit y no registra la
/// máquina de estados. El porqué —y lo que eso NO prueba— está en el <c>///</c> de
/// aquel método. Quien prueba las transiciones es <c>OrderStateMachineTests</c>.
/// </para>
///
/// <para>
/// Cada test estrena base de datos (ver <see cref="OrdersApiFactory"/>), así que
/// sembrar filas aquí no contamina a nadie.
/// </para>
/// </summary>
[Collection(OrdersApiCollection.Name)]
[Trait("Category", "Docker")]
public sealed class OrderStatusEndpointTests(SqlServerContainerFixture container) : IAsyncLifetime
{
    private const string CustomerEmail = "cliente@shop133.test";

    // Del seed de 1.4, como en CreateOrderTests: nada lo comprueba contra el
    // catálogo —desde 3.3 Orders congela lo que recibe— pero hace los fallos
    // legibles.
    private const int MugId = 1;
    private const string MugSku = "TAZA-001";
    private const string MugName = "Taza Talavera Puebla";
    private const decimal MugPrice = 249.00m;

    private readonly OrdersApiFactory factory = new(container);
    private HttpClient client = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await factory.InitializeAsync();

        client = factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        client?.Dispose();

        await factory.DisposeAsync();
    }

    /// <summary>
    /// Un pedido recién creado: pendiente, no final, y **sin etapa**.
    ///
    /// <c>Stage == null</c> es el caso que más fácil sería tratar como un error, y
    /// por eso tiene test propio. Significa que la instancia de saga todavía no
    /// existe, y no es una rareza de laboratorio: desde 4.5 el <c>OrderCreated</c>
    /// se escribe en el outbox y sale hacia RabbitMQ un instante después, así que
    /// entre el 201 del alta y el arranque de la saga hay una ventana real —y con
    /// el broker caído, larga—.
    ///
    /// Si el endpoint usara un <c>JOIN</c> normal en vez de un LEFT JOIN, esto
    /// sería un **404**: la respuesta más confusa posible para un pedido que el
    /// cliente acaba de ver crearse. Este test es lo único que lo impide.
    /// </summary>
    [Fact]
    public async Task GetStatus_OrderWithoutASagaRowYet_ReturnsPendingWithNoStage()
    {
        var created = await CreateOrderAsync();

        var status = await GetStatusAsync(created.Id);

        Assert.Equal(created.Id, status.Id);
        Assert.Equal(nameof(OrderStatus.Pending), status.Status);
        Assert.Null(status.Stage);
        Assert.Null(status.CancellationReason);
        Assert.False(status.IsFinal);

        // La misma fecha que devuelve GET /orders/{id}: sale de Order, no de la
        // saga. Son dos instantes distintos a propósito (el /// de
        // OrderState.CreatedAt lo explica) y este endpoint publica el del pedido.
        Assert.Equal(created.CreatedAt, status.CreatedAt);
    }

    /// <summary>
    /// Con la fila de saga puesta, el endpoint devuelve el **nombre real** del
    /// estado, sin traducir.
    ///
    /// Se usa <c>PricingPendingStockReserved</c> a propósito y no uno de nombre
    /// corto: es uno de los tres estados compuestos que 4.9 añadió al hacer que la
    /// validación del precio corra en paralelo con la reserva de stock, y son
    /// precisamente los que una interfaz con una sola barra de progreso no puede
    /// representar. Que este nombre llegue entero hasta el cliente es lo que permite
    /// que la página de 6.5 pinte dos pistas en vez de mentir con una.
    /// </summary>
    [Fact]
    public async Task GetStatus_OrderWithASagaRow_ReturnsTheRealStateName()
    {
        var created = await CreateOrderAsync();

        await SeedSagaAsync(created.Id, nameof(OrderStateMachine.PricingPendingStockReserved));

        var status = await GetStatusAsync(created.Id);

        Assert.Equal("PricingPendingStockReserved", status.Stage);

        // El pedido sigue Pending: la saga va por dentro y Order.Status solo se
        // mueve al final, cuando uno de los dos consumers de 4.3 procesa el evento
        // terminal. Son dos relojes, y el endpoint publica los dos.
        Assert.Equal(nameof(OrderStatus.Pending), status.Status);
        Assert.False(status.IsFinal);
    }

    /// <summary>
    /// Un pedido cancelado devuelve el motivo, y **es la primera vez que ese texto
    /// sale del sistema por HTTP**.
    ///
    /// Hasta 6.5 solo llegaba al correo de Notifications (4.6): <c>Order</c> no
    /// guarda el motivo —<c>Cancel()</c> no lo recibe desde 4.3— y el <c>///</c> de
    /// aquel método dejó escrito que una columna propia entraría "si algún día la
    /// interfaz tiene que enseñarle al cliente por qué se canceló su pedido …
    /// entonces con su caso de uso delante". El caso de uso está delante, y la
    /// respuesta fue que no hace falta la columna: el texto ya está persistido a un
    /// JOIN de distancia.
    /// </summary>
    [Fact]
    public async Task GetStatus_CancelledOrder_ReturnsTheReasonFromTheSagaRow()
    {
        const string Reason = "el importe 1197.00 supera el límite autorizado";

        var created = await CreateOrderAsync();

        await SeedSagaAsync(created.Id, nameof(OrderStateMachine.Cancelled), Reason);
        await CancelOrderAsync(created.Id);

        var status = await GetStatusAsync(created.Id);

        Assert.Equal(nameof(OrderStatus.Cancelled), status.Status);
        Assert.Equal("Cancelled", status.Stage);
        Assert.Equal(Reason, status.CancellationReason);

        // Lo que hace que el sondeo del navegador pare.
        Assert.True(status.IsFinal);
    }

    /// <summary>
    /// La columna <c>CancellationReason</c> es NOT NULL con cadena vacía por defecto
    /// (4.5), y el endpoint la normaliza a <c>null</c>.
    ///
    /// No es maquillaje: **hay un camino real que cancela sin escribirla**. De los
    /// caminos que llevan a <c>Cancelled</c>, los que publican <c>OrderCancelled</c>
    /// en la misma transición en la que reciben el motivo lo leen del mensaje que
    /// entra y nunca lo guardan en la instancia; solo lo escriben los que tienen que
    /// recordarlo para una transición posterior. Así que un pedido cancelado con el
    /// motivo vacío es un estado alcanzable, y el cliente tiene que poder
    /// distinguirlo con UNA comprobación, no con dos.
    /// </summary>
    [Fact]
    public async Task GetStatus_CancelledWithoutAStoredReason_ReturnsNullAndNotAnEmptyString()
    {
        var created = await CreateOrderAsync();

        await SeedSagaAsync(created.Id, nameof(OrderStateMachine.Cancelled));
        await CancelOrderAsync(created.Id);

        var status = await GetStatusAsync(created.Id);

        Assert.Equal(nameof(OrderStatus.Cancelled), status.Status);
        Assert.Null(status.CancellationReason);
        Assert.True(status.IsFinal);
    }

    /// <summary>
    /// <c>IsFinal</c> mira <c>Status</c> y no <c>Stage</c>, y este test es lo único
    /// que lo fija.
    ///
    /// El escenario es real y dura milisegundos: la saga ya llegó a
    /// <c>Confirmed</c> y publicó <c>OrderConfirmed</c>, pero el consumer de 4.3
    /// todavía no lo ha procesado, así que el pedido sigue <c>Pending</c>. Si
    /// <c>IsFinal</c> se calculara desde la etapa, el sondeo pararía aquí y la
    /// página se quedaría enseñando "pendiente" para siempre sobre un pedido que se
    /// confirmó un instante después.
    /// </summary>
    [Fact]
    public async Task GetStatus_SagaConfirmedButOrderNotYet_IsNotFinal()
    {
        var created = await CreateOrderAsync();

        await SeedSagaAsync(created.Id, nameof(OrderStateMachine.Confirmed));

        var status = await GetStatusAsync(created.Id);

        Assert.Equal("Confirmed", status.Stage);
        Assert.Equal(nameof(OrderStatus.Pending), status.Status);
        Assert.False(status.IsFinal);
    }

    /// <summary>
    /// Mismo 404 que <c>GET /orders/{id}</c>. Importa porque el sondeo del navegador
    /// lo trata distinto que un fallo de red: un 404 se pinta como "ese pedido no
    /// existe" y para, un error de conexión reintenta.
    /// </summary>
    [Fact]
    public async Task GetStatus_UnknownId_Returns404()
    {
        var response = await client.GetAsync($"/orders/{Guid.NewGuid()}/status", CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Ayudas ───────────────────────────────────────────────────────────────

    private async Task<OrderResponse> CreateOrderAsync()
    {
        var response = await client.PostAsJsonAsync(
            "/orders",
            new CreateOrderRequest
            {
                CustomerEmail = CustomerEmail,
                Items =
                [
                    new CreateOrderItemRequest
                    {
                        ProductId = MugId,
                        ProductSku = MugSku,
                        ProductName = MugName,
                        Quantity = 1,
                        UnitPrice = MugPrice,
                    },
                ],
            },
            CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<OrderResponse>(CancellationToken);

        Assert.NotNull(created);

        return created;
    }

    /// <summary>
    /// Escribe la fila de <c>OrderStates</c> que la saga escribiría. El
    /// <c>CorrelationId</c> **es** el <c>OrderId</c> —no hay conversión ni tabla de
    /// equivalencias (decisión 5 de docs/fase_0_3.md)— y es lo único que une esta
    /// fila con el pedido, porque entre las dos tablas no hay clave foránea.
    ///
    /// <c>RowVersion</c> no se toca: lo rellena SQL Server.
    /// </summary>
    private Task SeedSagaAsync(Guid orderId, string currentState, string cancellationReason = "") =>
        factory.WithDbAsync(async db =>
        {
            db.OrderStates.Add(new OrderState
            {
                CorrelationId = orderId,
                CurrentState = currentState,
                CustomerEmail = CustomerEmail,
                CreatedAt = DateTimeOffset.UtcNow,
                CancellationReason = cancellationReason,
            });

            await db.SaveChangesAsync(CancellationToken);
        });

    /// <summary>
    /// Mueve el pedido con <see cref="Order.Cancel"/>, no con un UPDATE a pelo: el
    /// método es el que sostiene la invariante de que solo se sale de
    /// <c>Pending</c>, y usarlo aquí hace que el test recorra el mismo camino que
    /// <c>OrderCancelledConsumer</c>.
    /// </summary>
    private Task CancelOrderAsync(Guid orderId) =>
        factory.WithDbAsync(async db =>
        {
            var order = await db.Orders.SingleAsync(candidate => candidate.Id == orderId, CancellationToken);

            order.Cancel();

            await db.SaveChangesAsync(CancellationToken);
        });

    private async Task<OrderStatusResponse> GetStatusAsync(Guid orderId)
    {
        var response = await client.GetAsync($"/orders/{orderId}/status", CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var status = await response.Content.ReadFromJsonAsync<OrderStatusResponse>(CancellationToken);

        Assert.NotNull(status);

        return status;
    }
}
