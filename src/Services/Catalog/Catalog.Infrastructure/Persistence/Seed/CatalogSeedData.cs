namespace Catalog.Infrastructure.Persistence.Seed;

/// <summary>
/// Los datos de prueba del catálogo (1.4): 5 categorías y 50 productos de
/// souvenirs, 10 por categoría.
///
/// Se cargan con <c>HasData</c> desde las configuraciones de EF Core, así que
/// **acaban dentro de una migración** como sentencias <c>InsertData</c>. Eso
/// tiene tres consecuencias que conviene tener presentes antes de tocar este
/// archivo:
///
/// 1. **Los ids son fijos y explícitos.** <c>HasData</c> no puede depender de un
///    <c>IDENTITY</c>: necesita saber la clave para poder comparar el modelo con
///    la migración anterior y decidir si una fila es alta, cambio o baja. EF
///    genera <c>SET IDENTITY_INSERT</c> alrededor del INSERT.
/// 2. **No pasa por el constructor de la entidad.** EF materializa estas filas
///    por reflexión, así que las guardas de <c>Product.Apply</c> no se ejecutan
///    y nadie normaliza nada: los <c>Sku</c> de aquí abajo tienen que estar ya
///    en mayúsculas y sin espacios sobrantes, porque nada los va a arreglar.
/// 3. **Cambiar un dato exige una migración nueva.** Corregir un precio aquí no
///    hace nada por sí solo; hay que volver a ejecutar <c>migrations add</c>,
///    que genera el <c>UpdateData</c> correspondiente. Es el precio de que el
///    seed viaje con el esquema — y lo que hace que la fixture de Testcontainers
///    de 1.7 obtenga estos datos con solo llamar a <c>MigrateAsync()</c>.
///
/// Vive en su propio archivo y no dentro de las configuraciones porque son 55
/// filas de datos: mezcladas con el mapeo, taparían el esquema, que es lo que
/// alguien viene a leer a <c>Configurations/</c>.
/// </summary>
internal static class CatalogSeedData
{
    /// <summary>
    /// Ids de las categorías. Son constantes y no literales sueltos para que la
    /// tabla de productos de abajo se lea ("CategoryId = Tazas") y para que un
    /// error de tecleo sea un fallo de compilación y no 10 productos en la
    /// categoría equivocada.
    /// </summary>
    private const int Tazas = 1;

    private const int Llaveros = 2;

    private const int Playeras = 3;

    private const int Pines = 4;

    private const int Libretas = 5;

    /// <summary>
    /// El catálogo de categorías. Cinco filas fijas: no hay endpoint de alta, y
    /// añadir una sexta es editar este array y generar una migración.
    /// </summary>
    public static readonly object[] Categories =
    [
        new { Id = Tazas, Name = "Tazas" },
        new { Id = Llaveros, Name = "Llaveros" },
        new { Id = Playeras, Name = "Playeras" },
        new { Id = Pines, Name = "Pines" },
        new { Id = Libretas, Name = "Libretas" },
    ];

    /// <summary>
    /// Los 50 productos. El <c>Sku</c> sigue el formato que 1.4 fija para el
    /// catálogo: <c>&lt;3 letras de la categoría&gt;-&lt;3 dígitos&gt;</c>, en
    /// mayúsculas. Es una **convención**, no una regla que el sistema imponga —
    /// la decisión 9 de docs/fase_1_1.md descartó exigir un regex, y 50 filas
    /// escritas por el propio proyecto no son motivo para reabrirlo.
    ///
    /// El prefijo tampoco está atado a la categoría por el esquema: nada impide
    /// un <c>TAZ-011</c> con <c>CategoryId = Playeras</c>. Poner una columna
    /// <c>Code</c> en <see cref="Entities.Category"/> daría la impresión
    /// contraria, que es justo por lo que no la tiene.
    ///
    /// Los precios están en pesos mexicanos. No hay columna de moneda: mientras
    /// el catálogo tenga una sola, una columna con el mismo valor en las 50
    /// filas no informa de nada. Entra el día que haya una segunda.
    ///
    /// Ningún <c>Stock</c> es 0 —para que 1.5 y la Fase 6 tengan siempre algo
    /// comprable— y los 50 son distintos entre sí, para que un copia-pega mal
    /// hecho se note al leer la tabla.
    /// </summary>
    public static readonly object[] Products =
    [
        // ── Tazas ────────────────────────────────────────────────────────────
        new
        {
            Id = 1,
            Sku = "TAZA-001",
            Name = "Taza Talavera Puebla",
            Description = "Taza de cerámica pintada a mano con el patrón azul cobalto de la talavera poblana. Capacidad de 350 ml, apta para microondas.",
            Price = 249.00m,
            Stock = 42,
            CategoryId = Tazas,
            ImageUrl = "/img/products/taza-001.jpg",
        },
        new
        {
            Id = 2,
            Sku = "TAZA-002",
            Name = "Taza Calavera Catrina",
            Description = "Taza esmaltada en negro con una Catrina serigrafiada en dorado. El diseño se revela por completo al llenarla con líquido caliente.",
            Price = 229.00m,
            Stock = 65,
            CategoryId = Tazas,
            ImageUrl = "/img/products/taza-002.jpg",
        },
        new
        {
            Id = 3,
            Sku = "TAZA-003",
            Name = "Taza Alebrije Oaxaqueño",
            Description = "Taza de 400 ml decorada con los colores y grecas de los alebrijes de San Martín Tilcajete. Cada pieza tiene variaciones de pintura.",
            Price = 269.00m,
            Stock = 28,
            CategoryId = Tazas,
            ImageUrl = "/img/products/taza-003.jpg",
        },
        new
        {
            Id = 4,
            Sku = "TAZA-004",
            Name = "Taza Sol Azteca",
            Description = "Taza de cerámica mate con la Piedra del Sol grabada en relieve. Asa reforzada y base antideslizante.",
            Price = 199.00m,
            Stock = 73,
            CategoryId = Tazas,
            ImageUrl = "/img/products/taza-004.jpg",
        },
        new
        {
            Id = 5,
            Sku = "TAZA-005",
            Name = "Taza Barro Negro",
            Description = "Taza artesanal de barro negro de San Bartolo Coyotepec, bruñida a mano. Pieza única; no apta para microondas.",
            Price = 289.00m,
            Stock = 19,
            CategoryId = Tazas,
            ImageUrl = "/img/products/taza-005.jpg",
        },
        new
        {
            Id = 6,
            Sku = "TAZA-006",
            Name = "Taza Lotería Mexicana",
            Description = "Taza de 350 ml con las cartas clásicas de la lotería impresas alrededor: el gallo, la sirena, el catrín y el nopal.",
            Price = 179.00m,
            Stock = 88,
            CategoryId = Tazas,
            ImageUrl = "/img/products/taza-006.jpg",
        },
        new
        {
            Id = 7,
            Sku = "TAZA-007",
            Name = "Taza Cactus Esmaltada",
            Description = "Taza con relieve de nopal esmaltado en verde y borde color arena. Diseño minimalista para uso diario.",
            Price = 189.00m,
            Stock = 54,
            CategoryId = Tazas,
            ImageUrl = "/img/products/taza-007.jpg",
        },
        new
        {
            Id = 8,
            Sku = "TAZA-008",
            Name = "Taza Frida Retrato",
            Description = "Taza de cerámica blanca con retrato ilustrado y corona de flores a todo color. Impresión resistente al lavavajillas.",
            Price = 259.00m,
            Stock = 37,
            CategoryId = Tazas,
            ImageUrl = "/img/products/taza-008.jpg",
        },
        new
        {
            Id = 9,
            Sku = "TAZA-009",
            Name = "Taza Talavera Mini Espresso",
            Description = "Taza de 90 ml para espresso, decorada a mano con motivos de talavera. Se vende individualmente.",
            Price = 149.00m,
            Stock = 96,
            CategoryId = Tazas,
            ImageUrl = "/img/products/taza-009.jpg",
        },
        new
        {
            Id = 10,
            Sku = "TAZA-010",
            Name = "Taza Pirámide de Teotihuacán",
            Description = "Taza de 400 ml con la silueta de la Pirámide del Sol grabada en láser sobre esmalte terracota.",
            Price = 239.00m,
            Stock = 31,
            CategoryId = Tazas,
            ImageUrl = "/img/products/taza-010.jpg",
        },

        // ── Llaveros ─────────────────────────────────────────────────────────
        new
        {
            Id = 11,
            Sku = "LLAV-001",
            Name = "Llavero Calavera de Plata",
            Description = "Llavero de calavera en plata .925 con detalles cincelados a mano. Argolla reforzada de acero inoxidable.",
            Price = 89.00m,
            Stock = 120,
            CategoryId = Llaveros,
            ImageUrl = "/img/products/llav-001.jpg",
        },
        new
        {
            Id = 12,
            Sku = "LLAV-002",
            Name = "Llavero Alebrije Tallado",
            Description = "Alebrije en miniatura tallado en madera de copal y pintado a mano. Cada pieza tiene una combinación de colores distinta.",
            Price = 79.00m,
            Stock = 84,
            CategoryId = Llaveros,
            ImageUrl = "/img/products/llav-002.jpg",
        },
        new
        {
            Id = 13,
            Sku = "LLAV-003",
            Name = "Llavero Sombrero Charro",
            Description = "Sombrero charro en miniatura de fieltro con bordado dorado en el ala. Incluye argolla y mosquetón.",
            Price = 55.00m,
            Stock = 110,
            CategoryId = Llaveros,
            ImageUrl = "/img/products/llav-003.jpg",
        },
        new
        {
            Id = 14,
            Sku = "LLAV-004",
            Name = "Llavero Piedra del Sol",
            Description = "Réplica del calendario azteca en zamak con acabado bronce envejecido. Diámetro de 4 cm.",
            Price = 69.00m,
            Stock = 92,
            CategoryId = Llaveros,
            ImageUrl = "/img/products/llav-004.jpg",
        },
        new
        {
            Id = 15,
            Sku = "LLAV-005",
            Name = "Llavero Cactus Bordado",
            Description = "Llavero de fieltro con nopal bordado a mano en hilo verde y flor rosa. Ligero y flexible.",
            Price = 49.00m,
            Stock = 105,
            CategoryId = Llaveros,
            ImageUrl = "/img/products/llav-005.jpg",
        },
        new
        {
            Id = 16,
            Sku = "LLAV-006",
            Name = "Llavero Máscara de Lucha Libre",
            Description = "Máscara de luchador en miniatura, cosida en tela elástica con detalles metálicos. Cuatro diseños clásicos surtidos.",
            Price = 75.00m,
            Stock = 67,
            CategoryId = Llaveros,
            ImageUrl = "/img/products/llav-006.jpg",
        },
        new
        {
            Id = 17,
            Sku = "LLAV-007",
            Name = "Llavero Chile Habanero",
            Description = "Chile habanero de resina con acabado brillante y hoja en verde esmaltado. Medida aproximada de 5 cm.",
            Price = 45.00m,
            Stock = 118,
            CategoryId = Llaveros,
            ImageUrl = "/img/products/llav-007.jpg",
        },
        new
        {
            Id = 18,
            Sku = "LLAV-008",
            Name = "Llavero Colibrí Esmaltado",
            Description = "Colibrí en metal esmaltado a fuego con degradado turquesa y verde. Pieza de dos caras.",
            Price = 95.00m,
            Stock = 41,
            CategoryId = Llaveros,
            ImageUrl = "/img/products/llav-008.jpg",
        },
        new
        {
            Id = 19,
            Sku = "LLAV-009",
            Name = "Llavero Talavera Redondo",
            Description = "Medallón de cerámica de talavera pintado a mano, montado sobre argolla de acero. Motivo floral azul.",
            Price = 59.00m,
            Stock = 76,
            CategoryId = Llaveros,
            ImageUrl = "/img/products/llav-009.jpg",
        },
        new
        {
            Id = 20,
            Sku = "LLAV-010",
            Name = "Llavero Ajolote de Resina",
            Description = "Ajolote rosa de resina translúcida con branquias en relieve. El favorito del catálogo entre los visitantes de Xochimilco.",
            Price = 85.00m,
            Stock = 58,
            CategoryId = Llaveros,
            ImageUrl = "/img/products/llav-010.jpg",
        },

        // ── Playeras ─────────────────────────────────────────────────────────
        new
        {
            Id = 21,
            Sku = "PLAY-001",
            Name = "Playera Ajolote Xochimilco",
            Description = "Playera de algodón peinado con ilustración de ajolote serigrafiada al frente. Corte unisex, tallas S a XL.",
            Price = 329.00m,
            Stock = 45,
            CategoryId = Playeras,
            ImageUrl = "/img/products/play-001.jpg",
        },
        new
        {
            Id = 22,
            Sku = "PLAY-002",
            Name = "Playera Lucha Libre",
            Description = "Playera negra con máscara de luchador estampada en cuatro tintas. Algodón 180 g, cuello reforzado.",
            Price = 349.00m,
            Stock = 62,
            CategoryId = Playeras,
            ImageUrl = "/img/products/play-002.jpg",
        },
        new
        {
            Id = 23,
            Sku = "PLAY-003",
            Name = "Playera Catrina Serigrafía",
            Description = "Playera blanca con Catrina serigrafiada a mano en tinta negra y detalles en dorado. Edición limitada.",
            Price = 299.00m,
            Stock = 71,
            CategoryId = Playeras,
            ImageUrl = "/img/products/play-003.jpg",
        },
        new
        {
            Id = 24,
            Sku = "PLAY-004",
            Name = "Playera Águila Real",
            Description = "Playera color arena con el águila del escudo nacional estampada en el pecho. Algodón orgánico.",
            Price = 279.00m,
            Stock = 53,
            CategoryId = Playeras,
            ImageUrl = "/img/products/play-004.jpg",
        },
        new
        {
            Id = 25,
            Sku = "PLAY-005",
            Name = "Playera Otomí Bordada",
            Description = "Playera con bordado otomí hecho a mano en el cuello y el puño, con figuras de animales en hilo multicolor.",
            Price = 399.00m,
            Stock = 22,
            CategoryId = Playeras,
            ImageUrl = "/img/products/play-005.jpg",
        },
        new
        {
            Id = 26,
            Sku = "PLAY-006",
            Name = "Playera Mapa de México",
            Description = "Playera azul marino con el mapa de México ilustrado por regiones y sus platillos típicos.",
            Price = 259.00m,
            Stock = 89,
            CategoryId = Playeras,
            ImageUrl = "/img/products/play-006.jpg",
        },
        new
        {
            Id = 27,
            Sku = "PLAY-007",
            Name = "Playera Cactus Minimalista",
            Description = "Playera blanca con un nopal en línea fina bordado en el bolsillo. Diseño discreto de uso diario.",
            Price = 249.00m,
            Stock = 94,
            CategoryId = Playeras,
            ImageUrl = "/img/products/play-007.jpg",
        },
        new
        {
            Id = 28,
            Sku = "PLAY-008",
            Name = "Playera Día de Muertos",
            Description = "Playera negra con ofrenda de Día de Muertos estampada a todo color, con tintas que brillan en la oscuridad.",
            Price = 359.00m,
            Stock = 38,
            CategoryId = Playeras,
            ImageUrl = "/img/products/play-008.jpg",
        },
        new
        {
            Id = 29,
            Sku = "PLAY-009",
            Name = "Playera Guerrero Azteca",
            Description = "Playera con guerrero águila ilustrado en el frente y glifos en la espalda. Algodón pesado de 200 g.",
            Price = 319.00m,
            Stock = 47,
            CategoryId = Playeras,
            ImageUrl = "/img/products/play-009.jpg",
        },
        new
        {
            Id = 30,
            Sku = "PLAY-010",
            Name = "Playera Talavera Estampada",
            Description = "Playera con patrón de azulejo de talavera repetido en todo el cuerpo. Estampado por sublimación.",
            Price = 289.00m,
            Stock = 66,
            CategoryId = Playeras,
            ImageUrl = "/img/products/play-010.jpg",
        },

        // ── Pines ────────────────────────────────────────────────────────────
        new
        {
            Id = 31,
            Sku = "PINS-001",
            Name = "Pin Ajolote Rosa",
            Description = "Pin de metal esmaltado en rosa con contorno dorado. Cierre de mariposa y respaldo grabado.",
            Price = 59.00m,
            Stock = 130,
            CategoryId = Pines,
            ImageUrl = "/img/products/pins-001.jpg",
        },
        new
        {
            Id = 32,
            Sku = "PINS-002",
            Name = "Pin Calavera Esmaltada",
            Description = "Calavera de azúcar en esmalte duro con flores de cempasúchil en naranja. Medida de 3 cm.",
            Price = 65.00m,
            Stock = 115,
            CategoryId = Pines,
            ImageUrl = "/img/products/pins-002.jpg",
        },
        new
        {
            Id = 33,
            Sku = "PINS-003",
            Name = "Pin Bandera de México",
            Description = "Pin rectangular con la bandera nacional en esmalte suave y baño de níquel. El clásico de la vitrina.",
            Price = 39.00m,
            Stock = 140,
            CategoryId = Pines,
            ImageUrl = "/img/products/pins-003.jpg",
        },
        new
        {
            Id = 34,
            Sku = "PINS-004",
            Name = "Pin Taco al Pastor",
            Description = "Pin de taco al pastor con piña, cilantro y tortilla en cinco colores de esmalte. Doble poste para que no gire.",
            Price = 49.00m,
            Stock = 126,
            CategoryId = Pines,
            ImageUrl = "/img/products/pins-004.jpg",
        },
        new
        {
            Id = 35,
            Sku = "PINS-005",
            Name = "Pin Cactus Dorado",
            Description = "Nopal en metal con baño dorado y esmalte verde translúcido. Acabado espejo.",
            Price = 55.00m,
            Stock = 102,
            CategoryId = Pines,
            ImageUrl = "/img/products/pins-005.jpg",
        },
        new
        {
            Id = 36,
            Sku = "PINS-006",
            Name = "Pin Máscara de Luchador",
            Description = "Máscara de lucha libre en esmalte rojo, plata y negro. Coleccionable de la serie de tres modelos.",
            Price = 69.00m,
            Stock = 87,
            CategoryId = Pines,
            ImageUrl = "/img/products/pins-006.jpg",
        },
        new
        {
            Id = 37,
            Sku = "PINS-007",
            Name = "Pin Colibrí Metálico",
            Description = "Colibrí en esmalte translúcido con degradado verde y azul sobre base de latón pulido.",
            Price = 79.00m,
            Stock = 64,
            CategoryId = Pines,
            ImageUrl = "/img/products/pins-007.jpg",
        },
        new
        {
            Id = 38,
            Sku = "PINS-008",
            Name = "Pin Sombrero Mariachi",
            Description = "Sombrero de mariachi en miniatura con grabado en el ala. El pin más económico del catálogo.",
            Price = 35.00m,
            Stock = 148,
            CategoryId = Pines,
            ImageUrl = "/img/products/pins-008.jpg",
        },
        new
        {
            Id = 39,
            Sku = "PINS-009",
            Name = "Pin Alebrije Miniatura",
            Description = "Alebrije en esmalte de siete colores con detalles en línea negra. Réplica de una talla oaxaqueña.",
            Price = 75.00m,
            Stock = 79,
            CategoryId = Pines,
            ImageUrl = "/img/products/pins-009.jpg",
        },
        new
        {
            Id = 40,
            Sku = "PINS-010",
            Name = "Pin Pirámide Maya",
            Description = "Pirámide de Chichén Itzá troquelada en metal con acabado bronce y fondo esmaltado en azul cielo.",
            Price = 45.00m,
            Stock = 133,
            CategoryId = Pines,
            ImageUrl = "/img/products/pins-010.jpg",
        },

        // ── Libretas ─────────────────────────────────────────────────────────
        new
        {
            Id = 41,
            Sku = "LIBR-001",
            Name = "Libreta Otomí Tapa Dura",
            Description = "Libreta de tapa dura forrada en tela con bordado otomí. 160 hojas de papel crema rayado y cinta separadora.",
            Price = 179.00m,
            Stock = 44,
            CategoryId = Libretas,
            ImageUrl = "/img/products/libr-001.jpg",
        },
        new
        {
            Id = 42,
            Sku = "LIBR-002",
            Name = "Libreta Talavera A5",
            Description = "Libreta tamaño A5 con portada de patrón de talavera y esquinas redondeadas. 120 hojas lisas.",
            Price = 149.00m,
            Stock = 68,
            CategoryId = Libretas,
            ImageUrl = "/img/products/libr-002.jpg",
        },
        new
        {
            Id = 43,
            Sku = "LIBR-003",
            Name = "Libreta Alebrije Cosida",
            Description = "Libreta de encuadernación japonesa cosida a mano, con portada ilustrada de alebrije. 96 hojas.",
            Price = 165.00m,
            Stock = 51,
            CategoryId = Libretas,
            ImageUrl = "/img/products/libr-003.jpg",
        },
        new
        {
            Id = 44,
            Sku = "LIBR-004",
            Name = "Libreta Catrina de Bolsillo",
            Description = "Libreta de bolsillo de 9 x 14 cm con Catrina en la portada. 64 hojas rayadas, ideal para notas rápidas.",
            Price = 89.00m,
            Stock = 112,
            CategoryId = Libretas,
            ImageUrl = "/img/products/libr-004.jpg",
        },
        new
        {
            Id = 45,
            Sku = "LIBR-005",
            Name = "Libreta de Papel Amate",
            Description = "Libreta con portada de papel amate elaborado en San Pablito, Puebla. Cada portada tiene una textura distinta.",
            Price = 159.00m,
            Stock = 33,
            CategoryId = Libretas,
            ImageUrl = "/img/products/libr-005.jpg",
        },
        new
        {
            Id = 46,
            Sku = "LIBR-006",
            Name = "Libreta Lotería Espiral",
            Description = "Libreta de espiral doble con las cartas de la lotería en la portada y separadores por sección. 140 hojas.",
            Price = 119.00m,
            Stock = 97,
            CategoryId = Libretas,
            ImageUrl = "/img/products/libr-006.jpg",
        },
        new
        {
            Id = 47,
            Sku = "LIBR-007",
            Name = "Libreta Códice Prehispánico",
            Description = "Libreta plegada en acordeón que imita un códice, con reproducción de glifos en la portada. 40 caras.",
            Price = 175.00m,
            Stock = 26,
            CategoryId = Libretas,
            ImageUrl = "/img/products/libr-007.jpg",
        },
        new
        {
            Id = 48,
            Sku = "LIBR-008",
            Name = "Libreta Cactus Punteada",
            Description = "Libreta de hoja punteada para bullet journal, con nopales ilustrados en la portada. 180 hojas de 100 g.",
            Price = 129.00m,
            Stock = 81,
            CategoryId = Libretas,
            ImageUrl = "/img/products/libr-008.jpg",
        },
        new
        {
            Id = 49,
            Sku = "LIBR-009",
            Name = "Libreta Mariposa Monarca",
            Description = "Libreta con monarcas ilustradas en la portada y en los cantos de las hojas. 120 hojas rayadas.",
            Price = 139.00m,
            Stock = 59,
            CategoryId = Libretas,
            ImageUrl = "/img/products/libr-009.jpg",
        },
        new
        {
            Id = 50,
            Sku = "LIBR-010",
            Name = "Libreta de Piel Repujada",
            Description = "Libreta forrada en piel repujada a mano con motivos prehispánicos y cierre de correa. 200 hojas de papel reciclado.",
            Price = 169.00m,
            Stock = 35,
            CategoryId = Libretas,
            ImageUrl = "/img/products/libr-010.jpg",
        },
    ];
}
