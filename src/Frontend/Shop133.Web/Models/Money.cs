using System.Globalization;

namespace Shop133.Web.Models;

/// <summary>
/// Formatea importes para la interfaz.
///
/// No es un modelo, y vive en <c>Models/</c> porque es la unica carpeta que <c>_ViewImports</c>
/// ya importa y crear una para una sola clase estatica seria peor. Se anota en vez de disimularlo.
///
/// Lo que resuelve NO es cosmetico: este proceso no configura localizacion en ninguna parte —no
/// hay <c>UseRequestLocalization</c> en <c>Program.cs</c>—, asi que <c>CultureInfo.CurrentCulture</c>
/// es la que diga el Windows de quien ejecute. Con un <c>ToString("C")</c> pelado, el MISMO
/// codigo pinta <c>$249.00</c>, <c>249,00 €</c> o <c>249,00 ¤</c> segun la maquina, y el marcador
/// dejaria de poder verificarse. La cultura se fija aqui, explicita y en un solo sitio.
///
/// Y hay un segundo motivo, medido: 3.3 y 4.8 comprobaron que un <c>decimal</c> PIERDE LOS CEROS
/// FINALES al viajar por JSON —se publico <c>249.00</c> y llego <c>"249"</c>—, asi que un
/// <c>@product.Price</c> crudo en la vista pintaria <c>249</c>. El formato de dos decimales los
/// devuelve.
///
/// Descartado el sufijo explicito (<c>249.00 MXN</c>), que quita toda ambiguedad frente al dolar
/// pero lee a factura y no a tienda; el seed de 1.4 son souvenirs mexicanos y <c>$249.00</c> es
/// lo que ensena una tienda mexicana.
/// </summary>
public static class Money
{
    /// <summary>
    /// La cultura con la que esta interfaz formatea NUMEROS, no solo importes.
    ///
    /// Se expone desde 6.3, cuando aparecio el primer numero de la interfaz que no es dinero: el
    /// tope de unidades por linea que <c>ShoppingCart</c> nombra al rechazar una cantidad. Un
    /// <c>:N0</c> pelado alli habria tenido exactamente el problema que el bloque de arriba
    /// describe —el separador de millares lo decidiria el Windows de quien ejecute— asi que en vez
    /// de declarar una segunda cultura en otro archivo, se comparte esta. El tipo se llama
    /// <c>Money</c> por su caso principal; el sitio unico donde vive la cultura es lo que no
    /// conviene duplicar.
    /// </summary>
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("es-MX");

    public static string Format(decimal amount) => amount.ToString("C", Culture);
}
