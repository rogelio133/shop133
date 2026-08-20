using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SeedSouvenirCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "Categories",
                columns: new[] { "Id", "Name" },
                values: new object[,]
                {
                    { 1, "Tazas" },
                    { 2, "Llaveros" },
                    { 3, "Playeras" },
                    { 4, "Pines" },
                    { 5, "Libretas" }
                });

            migrationBuilder.InsertData(
                table: "Products",
                columns: new[] { "Id", "CategoryId", "Description", "ImageUrl", "Name", "Price", "Sku", "Stock" },
                values: new object[,]
                {
                    { 1, 1, "Taza de cerámica pintada a mano con el patrón azul cobalto de la talavera poblana. Capacidad de 350 ml, apta para microondas.", "/img/products/taza-001.jpg", "Taza Talavera Puebla", 249.00m, "TAZA-001", 42 },
                    { 2, 1, "Taza esmaltada en negro con una Catrina serigrafiada en dorado. El diseño se revela por completo al llenarla con líquido caliente.", "/img/products/taza-002.jpg", "Taza Calavera Catrina", 229.00m, "TAZA-002", 65 },
                    { 3, 1, "Taza de 400 ml decorada con los colores y grecas de los alebrijes de San Martín Tilcajete. Cada pieza tiene variaciones de pintura.", "/img/products/taza-003.jpg", "Taza Alebrije Oaxaqueño", 269.00m, "TAZA-003", 28 },
                    { 4, 1, "Taza de cerámica mate con la Piedra del Sol grabada en relieve. Asa reforzada y base antideslizante.", "/img/products/taza-004.jpg", "Taza Sol Azteca", 199.00m, "TAZA-004", 73 },
                    { 5, 1, "Taza artesanal de barro negro de San Bartolo Coyotepec, bruñida a mano. Pieza única; no apta para microondas.", "/img/products/taza-005.jpg", "Taza Barro Negro", 289.00m, "TAZA-005", 19 },
                    { 6, 1, "Taza de 350 ml con las cartas clásicas de la lotería impresas alrededor: el gallo, la sirena, el catrín y el nopal.", "/img/products/taza-006.jpg", "Taza Lotería Mexicana", 179.00m, "TAZA-006", 88 },
                    { 7, 1, "Taza con relieve de nopal esmaltado en verde y borde color arena. Diseño minimalista para uso diario.", "/img/products/taza-007.jpg", "Taza Cactus Esmaltada", 189.00m, "TAZA-007", 54 },
                    { 8, 1, "Taza de cerámica blanca con retrato ilustrado y corona de flores a todo color. Impresión resistente al lavavajillas.", "/img/products/taza-008.jpg", "Taza Frida Retrato", 259.00m, "TAZA-008", 37 },
                    { 9, 1, "Taza de 90 ml para espresso, decorada a mano con motivos de talavera. Se vende individualmente.", "/img/products/taza-009.jpg", "Taza Talavera Mini Espresso", 149.00m, "TAZA-009", 96 },
                    { 10, 1, "Taza de 400 ml con la silueta de la Pirámide del Sol grabada en láser sobre esmalte terracota.", "/img/products/taza-010.jpg", "Taza Pirámide de Teotihuacán", 239.00m, "TAZA-010", 31 },
                    { 11, 2, "Llavero de calavera en plata .925 con detalles cincelados a mano. Argolla reforzada de acero inoxidable.", "/img/products/llav-001.jpg", "Llavero Calavera de Plata", 89.00m, "LLAV-001", 120 },
                    { 12, 2, "Alebrije en miniatura tallado en madera de copal y pintado a mano. Cada pieza tiene una combinación de colores distinta.", "/img/products/llav-002.jpg", "Llavero Alebrije Tallado", 79.00m, "LLAV-002", 84 },
                    { 13, 2, "Sombrero charro en miniatura de fieltro con bordado dorado en el ala. Incluye argolla y mosquetón.", "/img/products/llav-003.jpg", "Llavero Sombrero Charro", 55.00m, "LLAV-003", 110 },
                    { 14, 2, "Réplica del calendario azteca en zamak con acabado bronce envejecido. Diámetro de 4 cm.", "/img/products/llav-004.jpg", "Llavero Piedra del Sol", 69.00m, "LLAV-004", 92 },
                    { 15, 2, "Llavero de fieltro con nopal bordado a mano en hilo verde y flor rosa. Ligero y flexible.", "/img/products/llav-005.jpg", "Llavero Cactus Bordado", 49.00m, "LLAV-005", 105 },
                    { 16, 2, "Máscara de luchador en miniatura, cosida en tela elástica con detalles metálicos. Cuatro diseños clásicos surtidos.", "/img/products/llav-006.jpg", "Llavero Máscara de Lucha Libre", 75.00m, "LLAV-006", 67 },
                    { 17, 2, "Chile habanero de resina con acabado brillante y hoja en verde esmaltado. Medida aproximada de 5 cm.", "/img/products/llav-007.jpg", "Llavero Chile Habanero", 45.00m, "LLAV-007", 118 },
                    { 18, 2, "Colibrí en metal esmaltado a fuego con degradado turquesa y verde. Pieza de dos caras.", "/img/products/llav-008.jpg", "Llavero Colibrí Esmaltado", 95.00m, "LLAV-008", 41 },
                    { 19, 2, "Medallón de cerámica de talavera pintado a mano, montado sobre argolla de acero. Motivo floral azul.", "/img/products/llav-009.jpg", "Llavero Talavera Redondo", 59.00m, "LLAV-009", 76 },
                    { 20, 2, "Ajolote rosa de resina translúcida con branquias en relieve. El favorito del catálogo entre los visitantes de Xochimilco.", "/img/products/llav-010.jpg", "Llavero Ajolote de Resina", 85.00m, "LLAV-010", 58 },
                    { 21, 3, "Playera de algodón peinado con ilustración de ajolote serigrafiada al frente. Corte unisex, tallas S a XL.", "/img/products/play-001.jpg", "Playera Ajolote Xochimilco", 329.00m, "PLAY-001", 45 },
                    { 22, 3, "Playera negra con máscara de luchador estampada en cuatro tintas. Algodón 180 g, cuello reforzado.", "/img/products/play-002.jpg", "Playera Lucha Libre", 349.00m, "PLAY-002", 62 },
                    { 23, 3, "Playera blanca con Catrina serigrafiada a mano en tinta negra y detalles en dorado. Edición limitada.", "/img/products/play-003.jpg", "Playera Catrina Serigrafía", 299.00m, "PLAY-003", 71 },
                    { 24, 3, "Playera color arena con el águila del escudo nacional estampada en el pecho. Algodón orgánico.", "/img/products/play-004.jpg", "Playera Águila Real", 279.00m, "PLAY-004", 53 },
                    { 25, 3, "Playera con bordado otomí hecho a mano en el cuello y el puño, con figuras de animales en hilo multicolor.", "/img/products/play-005.jpg", "Playera Otomí Bordada", 399.00m, "PLAY-005", 22 },
                    { 26, 3, "Playera azul marino con el mapa de México ilustrado por regiones y sus platillos típicos.", "/img/products/play-006.jpg", "Playera Mapa de México", 259.00m, "PLAY-006", 89 },
                    { 27, 3, "Playera blanca con un nopal en línea fina bordado en el bolsillo. Diseño discreto de uso diario.", "/img/products/play-007.jpg", "Playera Cactus Minimalista", 249.00m, "PLAY-007", 94 },
                    { 28, 3, "Playera negra con ofrenda de Día de Muertos estampada a todo color, con tintas que brillan en la oscuridad.", "/img/products/play-008.jpg", "Playera Día de Muertos", 359.00m, "PLAY-008", 38 },
                    { 29, 3, "Playera con guerrero águila ilustrado en el frente y glifos en la espalda. Algodón pesado de 200 g.", "/img/products/play-009.jpg", "Playera Guerrero Azteca", 319.00m, "PLAY-009", 47 },
                    { 30, 3, "Playera con patrón de azulejo de talavera repetido en todo el cuerpo. Estampado por sublimación.", "/img/products/play-010.jpg", "Playera Talavera Estampada", 289.00m, "PLAY-010", 66 },
                    { 31, 4, "Pin de metal esmaltado en rosa con contorno dorado. Cierre de mariposa y respaldo grabado.", "/img/products/pins-001.jpg", "Pin Ajolote Rosa", 59.00m, "PINS-001", 130 },
                    { 32, 4, "Calavera de azúcar en esmalte duro con flores de cempasúchil en naranja. Medida de 3 cm.", "/img/products/pins-002.jpg", "Pin Calavera Esmaltada", 65.00m, "PINS-002", 115 },
                    { 33, 4, "Pin rectangular con la bandera nacional en esmalte suave y baño de níquel. El clásico de la vitrina.", "/img/products/pins-003.jpg", "Pin Bandera de México", 39.00m, "PINS-003", 140 },
                    { 34, 4, "Pin de taco al pastor con piña, cilantro y tortilla en cinco colores de esmalte. Doble poste para que no gire.", "/img/products/pins-004.jpg", "Pin Taco al Pastor", 49.00m, "PINS-004", 126 },
                    { 35, 4, "Nopal en metal con baño dorado y esmalte verde translúcido. Acabado espejo.", "/img/products/pins-005.jpg", "Pin Cactus Dorado", 55.00m, "PINS-005", 102 },
                    { 36, 4, "Máscara de lucha libre en esmalte rojo, plata y negro. Coleccionable de la serie de tres modelos.", "/img/products/pins-006.jpg", "Pin Máscara de Luchador", 69.00m, "PINS-006", 87 },
                    { 37, 4, "Colibrí en esmalte translúcido con degradado verde y azul sobre base de latón pulido.", "/img/products/pins-007.jpg", "Pin Colibrí Metálico", 79.00m, "PINS-007", 64 },
                    { 38, 4, "Sombrero de mariachi en miniatura con grabado en el ala. El pin más económico del catálogo.", "/img/products/pins-008.jpg", "Pin Sombrero Mariachi", 35.00m, "PINS-008", 148 },
                    { 39, 4, "Alebrije en esmalte de siete colores con detalles en línea negra. Réplica de una talla oaxaqueña.", "/img/products/pins-009.jpg", "Pin Alebrije Miniatura", 75.00m, "PINS-009", 79 },
                    { 40, 4, "Pirámide de Chichén Itzá troquelada en metal con acabado bronce y fondo esmaltado en azul cielo.", "/img/products/pins-010.jpg", "Pin Pirámide Maya", 45.00m, "PINS-010", 133 },
                    { 41, 5, "Libreta de tapa dura forrada en tela con bordado otomí. 160 hojas de papel crema rayado y cinta separadora.", "/img/products/libr-001.jpg", "Libreta Otomí Tapa Dura", 179.00m, "LIBR-001", 44 },
                    { 42, 5, "Libreta tamaño A5 con portada de patrón de talavera y esquinas redondeadas. 120 hojas lisas.", "/img/products/libr-002.jpg", "Libreta Talavera A5", 149.00m, "LIBR-002", 68 },
                    { 43, 5, "Libreta de encuadernación japonesa cosida a mano, con portada ilustrada de alebrije. 96 hojas.", "/img/products/libr-003.jpg", "Libreta Alebrije Cosida", 165.00m, "LIBR-003", 51 },
                    { 44, 5, "Libreta de bolsillo de 9 x 14 cm con Catrina en la portada. 64 hojas rayadas, ideal para notas rápidas.", "/img/products/libr-004.jpg", "Libreta Catrina de Bolsillo", 89.00m, "LIBR-004", 112 },
                    { 45, 5, "Libreta con portada de papel amate elaborado en San Pablito, Puebla. Cada portada tiene una textura distinta.", "/img/products/libr-005.jpg", "Libreta de Papel Amate", 159.00m, "LIBR-005", 33 },
                    { 46, 5, "Libreta de espiral doble con las cartas de la lotería en la portada y separadores por sección. 140 hojas.", "/img/products/libr-006.jpg", "Libreta Lotería Espiral", 119.00m, "LIBR-006", 97 },
                    { 47, 5, "Libreta plegada en acordeón que imita un códice, con reproducción de glifos en la portada. 40 caras.", "/img/products/libr-007.jpg", "Libreta Códice Prehispánico", 175.00m, "LIBR-007", 26 },
                    { 48, 5, "Libreta de hoja punteada para bullet journal, con nopales ilustrados en la portada. 180 hojas de 100 g.", "/img/products/libr-008.jpg", "Libreta Cactus Punteada", 129.00m, "LIBR-008", 81 },
                    { 49, 5, "Libreta con monarcas ilustradas en la portada y en los cantos de las hojas. 120 hojas rayadas.", "/img/products/libr-009.jpg", "Libreta Mariposa Monarca", 139.00m, "LIBR-009", 59 },
                    { 50, 5, "Libreta forrada en piel repujada a mano con motivos prehispánicos y cierre de correa. 200 hojas de papel reciclado.", "/img/products/libr-010.jpg", "Libreta de Piel Repujada", 169.00m, "LIBR-010", 35 }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 1);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 2);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 3);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 4);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 5);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 6);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 7);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 8);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 9);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 10);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 11);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 12);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 13);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 14);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 15);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 16);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 17);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 18);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 19);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 20);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 21);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 22);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 23);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 24);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 25);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 26);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 27);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 28);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 29);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 30);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 31);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 32);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 33);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 34);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 35);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 36);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 37);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 38);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 39);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 40);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 41);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 42);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 43);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 44);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 45);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 46);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 47);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 48);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 49);

            migrationBuilder.DeleteData(
                table: "Products",
                keyColumn: "Id",
                keyValue: 50);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 1);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 2);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 3);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 4);

            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 5);
        }
    }
}
