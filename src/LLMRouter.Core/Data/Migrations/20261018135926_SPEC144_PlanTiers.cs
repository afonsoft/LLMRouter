using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LLMRouter.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SPEC144_PlanTiers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Plan",
                table: "users",
                type: "TEXT",
                nullable: false,
                defaultValue: "free");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Plan",
                table: "users");
        }
    }
}
