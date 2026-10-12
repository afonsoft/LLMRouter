using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LLMRouter.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SPEC129_GamiDepth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Skill",
                table: "a2aTasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "leaderboard",
                columns: table => new
                {
                    ApiKeyId = table.Column<string>(type: "TEXT", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", nullable: false),
                    Score = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leaderboard", x => new { x.ApiKeyId, x.Scope });
                });

            migrationBuilder.CreateTable(
                name: "userLevels",
                columns: table => new
                {
                    ApiKeyId = table.Column<string>(type: "TEXT", nullable: false),
                    TotalXp = table.Column<long>(type: "INTEGER", nullable: false),
                    CurrentLevel = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_userLevels", x => x.ApiKeyId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "leaderboard");

            migrationBuilder.DropTable(
                name: "userLevels");

            migrationBuilder.DropColumn(
                name: "Skill",
                table: "a2aTasks");
        }
    }
}
