using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Justina.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReceiptTaxLabels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TaxLabels",
                table: "Receipts",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TaxLabels",
                table: "Receipts");
        }
    }
}
