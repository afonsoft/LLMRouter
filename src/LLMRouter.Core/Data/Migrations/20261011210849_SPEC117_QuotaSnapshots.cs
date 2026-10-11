using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LLMRouter.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SPEC117_QuotaSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "quotaSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    ConnectionId = table.Column<string>(type: "TEXT", nullable: true),
                    WindowKey = table.Column<string>(type: "TEXT", nullable: false),
                    RemainingPercentage = table.Column<double>(type: "REAL", nullable: true),
                    IsExhausted = table.Column<int>(type: "INTEGER", nullable: false),
                    NextResetAt = table.Column<string>(type: "TEXT", nullable: true),
                    WindowDurationMs = table.Column<long>(type: "INTEGER", nullable: true),
                    RawData = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quotaSnapshots", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "quotaSnapshots");
        }
    }
}
