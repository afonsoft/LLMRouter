using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LLMRouter.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVirtualKeysLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "usageHistory",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserKeyId",
                table: "usageHistory",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TeamId",
                table: "apiKeys",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "apiKeys",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "creditLedger",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Delta = table.Column<double>(type: "REAL", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    UsageId = table.Column<long>(type: "INTEGER", nullable: true),
                    BalanceAfter = table.Column<double>(type: "REAL", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_creditLedger", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "userKeys",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    ModelsAllow = table.Column<string>(type: "TEXT", nullable: false),
                    BudgetUsd = table.Column<double>(type: "REAL", nullable: false),
                    BudgetPeriod = table.Column<string>(type: "TEXT", nullable: false),
                    RateLimitGroupId = table.Column<string>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    SpendUsd = table.Column<double>(type: "REAL", nullable: false),
                    SpendPeriodStart = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_userKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: true),
                    OidcSubject = table.Column<string>(type: "TEXT", nullable: true),
                    OidcIssuer = table.Column<string>(type: "TEXT", nullable: true),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "creditLedger");

            migrationBuilder.DropTable(
                name: "userKeys");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "usageHistory");

            migrationBuilder.DropColumn(
                name: "UserKeyId",
                table: "usageHistory");

            migrationBuilder.DropColumn(
                name: "TeamId",
                table: "apiKeys");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "apiKeys");
        }
    }
}
