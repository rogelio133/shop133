namespace Inventory.Infrastructure.Persistence.Seed;

/// <summary>
/// El stock reservable de partida (3.4): 50 filas, una por cada producto que
/// siembra el catálogo en 1.4.
///
/// Se cargan con <c>HasData</c> desde <c>StockItemConfiguration</c>, así que
/// **acaban dentro de una migración** como sentencias <c>InsertData</c>. Las
/// tres consecuencias son las mismas que documenta <c>CatalogSeedData</c>:
///
/// 1. **Los ids son fijos y explícitos.** Aquí no hay ningún <c>IDENTITY</c> que
///    esquivar —<c>StockItem.ProductId</c> es una clave que pone Catalog— así
///    que no se genera <c>SET IDENTITY_INSERT</c>, al contrario que en Catalog.
/// 2. **No pasa por el constructor de la entidad.** EF materializa estas filas
///    por reflexión, así que las guardas de <c>StockItem</c> no se ejecutan: los
///    números de aquí abajo tienen que ser válidos por sí solos, porque nada los
///    va a arreglar. En particular <c>QuantityReserved</c> hay que escribirlo,
///    aunque el constructor lo pondría a 0 solo.
/// 3. **Cambiar un dato exige una migración nueva.** Es el precio de que el seed
///    viaje con el esquema — y lo que hará que la fixture de Testcontainers de
///    3.7 obtenga estos datos con solo llamar a <c>MigrateAsync()</c>, igual que
///    <c>Catalog.Tests</c> desde 1.7.
///
/// **Los ids 1–50 coinciden con los del catálogo, y las cantidades NO.** Lo
/// primero es lo que hace demostrable el camino feliz de 3.4 y 3.5: sin filas
/// aquí, todo pedido se rechazaría y no habría nada que enseñar. Lo segundo es
/// deliberado — <c>Product.Stock</c> es el número que el catálogo *muestra* y
/// este es el reservable; son dos columnas con dos dueños distintos y ninguna
/// sincronización entre ellas. Sembrarlas con el mismo valor sugeriría una
/// relación que no existe y que nadie mantiene.
///
/// Que Inventory conozca los ids de Catalog no rompe la regla 1: son datos de
/// arranque, no una consulta a <c>CatalogDb</c>. Un producto dado de alta por
/// <c>POST /products</c> después del seed **no** tendrá fila aquí, y por eso su
/// pedido acaba en <c>StockRejected</c> — que es exactamente la mitad de
/// existencia que la decisión 2b de docs/fase_3_3.md le encargó a este punto.
///
/// Ninguna cantidad es 0 y las 50 son distintas entre sí, por el mismo motivo
/// que en el catálogo: que siempre haya algo comprable, y que un copia-pega mal
/// hecho se note al leer la tabla. Para probar el rechazo por falta de stock no
/// hace falta una fila agotada — basta pedir más unidades de las que hay.
/// </summary>
internal static class InventorySeedData
{
    public static readonly object[] StockItems =
    [
        // ── Tazas (TAZA-001 … TAZA-010) ──────────────────────────────────────
        new { ProductId = 1, QuantityOnHand = 42, QuantityReserved = 0 },
        new { ProductId = 2, QuantityOnHand = 65, QuantityReserved = 0 },
        new { ProductId = 3, QuantityOnHand = 18, QuantityReserved = 0 },
        new { ProductId = 4, QuantityOnHand = 73, QuantityReserved = 0 },
        new { ProductId = 5, QuantityOnHand = 31, QuantityReserved = 0 },
        new { ProductId = 6, QuantityOnHand = 57, QuantityReserved = 0 },
        new { ProductId = 7, QuantityOnHand = 24, QuantityReserved = 0 },
        new { ProductId = 8, QuantityOnHand = 88, QuantityReserved = 0 },
        new { ProductId = 9, QuantityOnHand = 12, QuantityReserved = 0 },
        new { ProductId = 10, QuantityOnHand = 46, QuantityReserved = 0 },

        // ── Llaveros (LLAV-001 … LLAV-010) ───────────────────────────────────
        new { ProductId = 11, QuantityOnHand = 120, QuantityReserved = 0 },
        new { ProductId = 12, QuantityOnHand = 95, QuantityReserved = 0 },
        new { ProductId = 13, QuantityOnHand = 140, QuantityReserved = 0 },
        new { ProductId = 14, QuantityOnHand = 78, QuantityReserved = 0 },
        new { ProductId = 15, QuantityOnHand = 163, QuantityReserved = 0 },
        new { ProductId = 16, QuantityOnHand = 54, QuantityReserved = 0 },
        new { ProductId = 17, QuantityOnHand = 132, QuantityReserved = 0 },
        new { ProductId = 18, QuantityOnHand = 87, QuantityReserved = 0 },
        new { ProductId = 19, QuantityOnHand = 109, QuantityReserved = 0 },
        new { ProductId = 20, QuantityOnHand = 71, QuantityReserved = 0 },

        // ── Playeras (PLAY-001 … PLAY-010) ───────────────────────────────────
        new { ProductId = 21, QuantityOnHand = 35, QuantityReserved = 0 },
        new { ProductId = 22, QuantityOnHand = 22, QuantityReserved = 0 },
        new { ProductId = 23, QuantityOnHand = 48, QuantityReserved = 0 },
        new { ProductId = 24, QuantityOnHand = 16, QuantityReserved = 0 },
        new { ProductId = 25, QuantityOnHand = 61, QuantityReserved = 0 },
        new { ProductId = 26, QuantityOnHand = 29, QuantityReserved = 0 },
        new { ProductId = 27, QuantityOnHand = 43, QuantityReserved = 0 },
        new { ProductId = 28, QuantityOnHand = 19, QuantityReserved = 0 },
        new { ProductId = 29, QuantityOnHand = 55, QuantityReserved = 0 },
        new { ProductId = 30, QuantityOnHand = 27, QuantityReserved = 0 },

        // ── Pines (PINS-001 … PINS-010) ──────────────────────────────────────
        new { ProductId = 31, QuantityOnHand = 210, QuantityReserved = 0 },
        new { ProductId = 32, QuantityOnHand = 175, QuantityReserved = 0 },
        new { ProductId = 33, QuantityOnHand = 240, QuantityReserved = 0 },
        new { ProductId = 34, QuantityOnHand = 158, QuantityReserved = 0 },
        new { ProductId = 35, QuantityOnHand = 192, QuantityReserved = 0 },
        new { ProductId = 36, QuantityOnHand = 226, QuantityReserved = 0 },
        new { ProductId = 37, QuantityOnHand = 143, QuantityReserved = 0 },
        new { ProductId = 38, QuantityOnHand = 268, QuantityReserved = 0 },
        new { ProductId = 39, QuantityOnHand = 181, QuantityReserved = 0 },
        new { ProductId = 40, QuantityOnHand = 205, QuantityReserved = 0 },

        // ── Libretas (LIBR-001 … LIBR-010) ───────────────────────────────────
        new { ProductId = 41, QuantityOnHand = 68, QuantityReserved = 0 },
        new { ProductId = 42, QuantityOnHand = 52, QuantityReserved = 0 },
        new { ProductId = 43, QuantityOnHand = 91, QuantityReserved = 0 },
        new { ProductId = 44, QuantityOnHand = 37, QuantityReserved = 0 },
        new { ProductId = 45, QuantityOnHand = 76, QuantityReserved = 0 },
        new { ProductId = 46, QuantityOnHand = 44, QuantityReserved = 0 },
        new { ProductId = 47, QuantityOnHand = 83, QuantityReserved = 0 },
        new { ProductId = 48, QuantityOnHand = 59, QuantityReserved = 0 },
        new { ProductId = 49, QuantityOnHand = 102, QuantityReserved = 0 },
        new { ProductId = 50, QuantityOnHand = 33, QuantityReserved = 0 },
    ];
}
