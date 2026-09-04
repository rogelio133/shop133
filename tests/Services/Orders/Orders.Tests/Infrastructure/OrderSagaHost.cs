using MassTransit;
// ITestHarness vive en MassTransit.Testing, mientras que AddMassTransitTestHarness está en
// MassTransit a secas. Los dos using hacen falta y el compilador solo se queja del segundo
// — el mismo tropiezo que anotó 3.7 y, antes, 3.2 con SystemTextJsonMessageSerializer.
using MassTransit.Testing;

using Microsoft.Extensions.DependencyInjection;

using Orders.Domain.Sagas;

using Xunit;

namespace Orders.Tests.Infrastructure;

/// <summary>
/// El host de los tests de la máquina de estados: la <see cref="OrderStateMachine"/> montada
/// sobre el transporte en memoria de MassTransit, con el espía que hace de Inventory.
///
/// **Sin base de datos, y es la primera vez que eso pasa en una suite de servicio.** Las
/// cuatro que existen desde 1.7 son <c>Category=Docker</c> porque prueban código que escribe
/// en SQL Server; esta prueba un proceso, y un proceso no necesita tabla para existir. Con
/// <c>InMemoryRepository()</c> los tests corren en milisegundos, que es lo que el roadmap
/// prometía del harness en 3.7 y que hasta hoy nunca se había cumplido.
///
/// *Descartado* usar el repositorio EF también aquí. Se prueba en
/// <see cref="OrderSagaDbHost"/>, y a propósito con otros tests: mezclarlo obligaría a montar
/// SQL Server para comprobar transiciones que no lo tocan, y volvería <c>Docker</c> la única
/// suite del repositorio que puede no serlo.
///
/// *Descartado* reutilizar <see cref="OrdersApiFactory"/>. Esa fábrica borra todo
/// <c>ServiceDescriptor</c> de MassTransit y registra un harness pelado, así que allí no hay
/// saga; devolvérsela significaría reescribir dentro del test el <c>AddMassTransit</c> entero
/// del <c>Program.cs</c> de Orders.API —outbox, repositorio EF, callback de endpoints y los
/// dos consumers— para probar una clase que no depende de ninguna de esas cosas. Es el mismo
/// criterio con el que la decisión 3 de docs/fase_3_7.md dejó a Inventory y Payments sin
/// <c>WebApplicationFactory</c>. **El precio, dicho en voz alta:** nada comprueba que el
/// <c>Program.cs</c> de Orders.API registre de verdad la saga con
/// <c>AddSagaStateMachine</c>; ese hueco es de 8.2, heredado de 3.7.
///
/// **Una instancia por test**, como los otros tres hosts del repositorio: xUnit construye la
/// clase de test una vez por método y este host es un campo de instancia. Aquí sale
/// prácticamente gratis — no hay contenedor que levantar ni base que migrar.
/// </summary>
public sealed class OrderSagaHost : IAsyncLifetime
{
    private ServiceProvider provider = null!;

    /// <summary>
    /// El harness ya arrancado. Publicar por <c>harness.Bus</c> y afirmar sobre
    /// <c>harness.Published</c>, <c>harness.Sent</c> y <c>harness.Consumed</c>.
    /// </summary>
    public ITestHarness Harness { get; private set; } = null!;

    /// <summary>
    /// El interruptor del espía. Ponerlo a <c>false</c> **antes** de publicar hace que
    /// Inventory calle, que es como se comprueba que la saga espera en
    /// <c>CompensatingStock</c> sin cancelar el pedido.
    /// </summary>
    public ReleaseStockSpySwitch Spy { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var services = new ServiceCollection();

        // OrderStateMachine pide un ILogger<T> por constructor: sin esto,
        // AddSagaStateMachine no puede resolverla y el host no arranca.
        services.AddLogging();

        services.AddSingleton<ReleaseStockSpySwitch>();

        services.AddMassTransitTestHarness(configure =>
        {
            // La saga con repositorio en memoria — el que tuvo Orders.API entre 4.1 y 4.5.
            // Lo que se prueba en esta clase son las transiciones y los mensajes que salen,
            // y ésos son idénticos con los dos repositorios: 4.5 no cambió ni una línea de
            // la máquina de estados. La persistencia es OrderStatePersistenceTests.
            configure.AddSagaStateMachine<OrderStateMachine, OrderState>()
                .InMemoryRepository();

            // ── El nombre del endpoint del espía va explícito, y es carga estructural ──
            //
            // El formatter kebab de abajo derivaría "release-stock-spy" del nombre del tipo,
            // y la saga manda su comando a la URI literal queue:release-stock. Con el nombre
            // por convención, el Send llegaría a una cola que nadie lee — sin error y sin
            // aviso, que es exactamente el modo de fallo del que avisa el /// de
            // InventoryReleaseStockEndpoint.
            //
            // Escrito así, esta línea es lo que ata el destino de la saga desde el lado de
            // Orders, igual que ReleaseStockConsumerTests lo ata desde el de Inventory.
            configure.AddConsumer<ReleaseStockSpyConsumer>()
                .Endpoint(endpoint => endpoint.Name = "release-stock");

            // El mismo formatter que el Program.cs de Orders.API. Nombra la cola de la saga a
            // partir del tipo de la INSTANCIA: OrderState -> "order-state", no
            // "order-state-machine".
            configure.SetKebabCaseEndpointNameFormatter();

            // ── Lo que convierte esta suite en Fast de verdad, y no estaba previsto ──
            //
            // Con el valor por defecto los 9 tests tardaban **23,1 s**; con éste, 3,9 s. El
            // coste no era el trabajo —el transporte en memoria resuelve la saga entera en
            // menos de un milisegundo— sino la ventana de silencio que InactivityTask espera
            // antes de darse por satisfecha: un segundo por test, más el sondeo.
            //
            // 500 ms y no los 200 ms con los que se midió: el margen sobra tres órdenes de
            // magnitud sobre el trabajo real, y el modo de fallo de quedarse corto es el
            // peor que hay — el await vuelve con mensajes en vuelo y un Assert.Empty pasa
            // **por no haber llegado a ocurrir nada**. Es la trampa 1 de 3.7 con otro
            // disfraz. Lo que hace que un descuido aquí no pase inadvertido es que todos los
            // tests de la clase llevan además una afirmación positiva (el estado alcanzado,
            // el evento que salió), y ésas no pueden pasar sin que el trabajo termine.
            configure.SetTestTimeouts(testInactivityTimeout: TimeSpan.FromMilliseconds(500));

            configure.UsingInMemory((context, cfg) =>
            {
                // Por defecto el transporte en memoria entrega en paralelo. Aquí eso rompería
                // el orden de los eventos dentro de un mismo pedido, que es justo lo que
                // estos tests recorren. Medido por 3.7 en otro sitio y con la misma causa.
                //
                // Es POR ENDPOINT, así que ordena order-state consigo mismo — que es lo que
                // hace falta: los seis eventos de la saga entran todos por esa cola.
                cfg.ConcurrentMessageLimit = 1;

                // Sin esto la saga se registra y no escucha nada, en silencio. Es el mismo
                // fallo que 3.1 se adelantó a evitar dejando la llamada puesta con cero
                // consumers.
                cfg.ConfigureEndpoints(context);
            });
        });

        provider = services.BuildServiceProvider(validateScopes: true);

        Spy = provider.GetRequiredService<ReleaseStockSpySwitch>();
        Harness = provider.GetRequiredService<ITestHarness>();

        await Harness.Start();
    }

    /// <summary>
    /// El harness de la saga, por el que se llega a las instancias vivas.
    /// </summary>
    public ISagaStateMachineTestHarness<OrderStateMachine, OrderState> SagaHarness =>
        Harness.GetSagaStateMachineHarness<OrderStateMachine, OrderState>();

    /// <summary>
    /// La instancia de un pedido, o <c>null</c> si la saga nunca arrancó para él.
    /// </summary>
    public OrderState? Instance(Guid orderId) => SagaHarness.Sagas.Contains(orderId);

    /// <summary>
    /// En qué estado está la saga de un pedido, por nombre, o <c>null</c> si no existe.
    ///
    /// Se lee de <c>OrderState.CurrentState</c> —un <c>string</c> desde 2.1— y se compara
    /// contra el nombre de la propiedad de la máquina de estados. Que se pueda consultar
    /// después de terminar es la decisión que 4.2 aplazó y 4.5 cerró: los estados terminales
    /// son estados normales y no <c>Finalize()</c>, precisamente para que este punto pueda
    /// afirmar el desenlace.
    /// </summary>
    public string? State(Guid orderId) => Instance(orderId)?.CurrentState;

    public async ValueTask DisposeAsync() => await provider.DisposeAsync();
}
