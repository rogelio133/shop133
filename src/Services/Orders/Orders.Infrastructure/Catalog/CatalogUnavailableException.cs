namespace Orders.Infrastructure.Catalog;

/// <summary>
/// PHASE-2 DEBT: replaced by OrderCreated event in Phase 3.
///
/// "No se pudo hablar con Catalog." Es **el** síntoma que la Fase 2 existe para
/// hacer visible: Orders no puede aceptar un pedido si Catalog no contesta,
/// porque sin él no tiene precios que congelar.
///
/// Existe para que <c>HttpRequestException</c>, <c>TaskCanceledException</c> y
/// <c>JsonException</c> no se filtren a la capa API. El controller no debería
/// saber que por debajo hay HTTP; solo que la dependencia no respondió, y que eso
/// se traduce en un 502.
///
/// *Descartado* dejar que el controller capture las tres excepciones de
/// System.Net.Http directamente: repartiría el conocimiento del transporte entre
/// dos capas y, sobre todo, haría más difícil el borrado de la Fase 3 — al
/// desaparecer esta carpeta, el <c>catch</c> del controller se queda sin tipo y
/// el compilador señala exactamente lo que hay que quitar.
///
/// **Distingue de un producto inexistente**: eso NO es esta excepción. Un 404 de
/// Catalog es una respuesta perfectamente válida ("ese producto no existe") y
/// sale como <c>null</c>, que el controller convierte en 400. Confundir los dos
/// casos haría que un id mal escrito en el cuerpo pareciera una caída de
/// infraestructura.
/// </summary>
public sealed class CatalogUnavailableException : Exception
{
    public CatalogUnavailableException(string message)
        : base(message)
    {
    }

    public CatalogUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
