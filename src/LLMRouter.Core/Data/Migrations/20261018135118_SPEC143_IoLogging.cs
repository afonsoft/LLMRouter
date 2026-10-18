using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LLMRouter.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SPEC143_IoLogging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Prompt",
                table: "requestDetails",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Response",
                table: "requestDetails",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Prompt",
                table: "requestDetails");

            migrationBuilder.DropColumn(
                name: "Response",
                table: "requestDetails");
        }
    }
}
