using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Justina.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCandidateSummaries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CandidateSummaries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    RecipientUserId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CandidateId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    JobOpeningId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    StageId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CandidateName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    JobTitle = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RecipientName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SummaryText = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    CompanyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExternalInterviewId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CandidateSummaries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CandidateSummaries_Recipient_State",
                table: "CandidateSummaries",
                columns: new[] { "Channel", "RecipientUserId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_CandidateSummaries_State_CreatedAtUtc",
                table: "CandidateSummaries",
                columns: new[] { "State", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CandidateSummaries");
        }
    }
}
