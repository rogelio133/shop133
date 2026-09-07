using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Catalog.Infrastructure.Migrations
{
    /// <summary>
    /// Las dos columnas con las que Product recuerda su precio anterior (4.8).
    ///
    /// ── Este archivo se editó a mano, y conviene saber por qué ──
    ///
    /// Tal y como lo generó "dotnet ef migrations add", el Up() traía además
    /// **50 UpdateData** —uno por cada fila del seed de 1.4— poniendo las dos
    /// columnas nuevas a NULL. Son no-ops: la columna acaba de crearse con
    /// AddColumn y ya vale NULL en todas las filas. EF los emite porque HasData
    /// compara la forma COMPLETA de cada fila sembrada contra el snapshot
    /// anterior, y al ganar la entidad dos propiedades las 50 filas cuentan como
    /// "cambiadas". No hay flag para evitarlo, igual que no lo hay para mantener
    /// un HasData fuera de InitialCreate (1.4, 3.4).
    ///
    /// Se borraron porque son 50 UPDATE inútiles en una migración cuyo trabajo son
    /// dos columnas, y porque son lo que hace que el comando avise "An operation
    /// was scaffolded that may result in the loss of data" — un aviso que aquí no
    /// significa nada y que asusta al leerlo. Borrarlos es seguro y es idempotente:
    /// el snapshot ya registra las dos columnas a null en las filas sembradas, así
    /// que el siguiente "migrations add" no las vuelve a generar.
    ///
    /// Lo que **no** se toca a mano nunca es CatalogDbContextModelSnapshot.cs.
    /// Editar el cuerpo de un Up()/Down() generado es legítimo; reescribir el
    /// snapshot es perder la referencia contra la que se compara el modelo.
    /// </summary>
    public partial class AddProductPriceHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "PreviousPrice",
                table: "Products",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PriceChangedAt",
                table: "Products",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreviousPrice",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "PriceChangedAt",
                table: "Products");
        }
    }
}
