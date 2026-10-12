using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LLMRouter.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SPEC127_AgentBridge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agentBridgeBypass",
                columns: table => new
                {
                    Pattern = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agentBridgeBypass", x => x.Pattern);
                });

            migrationBuilder.CreateTable(
                name: "agentBridgeMappings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AgentId = table.Column<string>(type: "TEXT", nullable: false),
                    SourceModel = table.Column<string>(type: "TEXT", nullable: false),
                    TargetModel = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agentBridgeMappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "agentBridgeState",
                columns: table => new
                {
                    AgentId = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DnsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CertInstalled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agentBridgeState", x => x.AgentId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agentBridgeBypass");

            migrationBuilder.DropTable(
                name: "agentBridgeMappings");

            migrationBuilder.DropTable(
                name: "agentBridgeState");
        }
    }
}
