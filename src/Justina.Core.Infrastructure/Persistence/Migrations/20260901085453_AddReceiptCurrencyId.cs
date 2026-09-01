using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Justina.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReceiptCurrencyId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CurrencyId",
                table: "Receipts",
                type: "uniqueidentifier",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CurrencyId",
                table: "Receipts");
        }
    }
}
