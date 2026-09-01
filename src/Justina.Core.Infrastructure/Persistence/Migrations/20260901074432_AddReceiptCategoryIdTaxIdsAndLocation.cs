using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Justina.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReceiptCategoryIdTaxIdsAndLocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CategoryId",
                table: "Receipts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Location",
                table: "Receipts",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxIds",
                table: "Receipts",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CategoryId",
                table: "Receipts");

            migrationBuilder.DropColumn(
                name: "Location",
                table: "Receipts");

            migrationBuilder.DropColumn(
                name: "TaxIds",
                table: "Receipts");
        }
    }
}
