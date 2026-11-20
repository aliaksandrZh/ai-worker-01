using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentCommands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_command",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    github_comment_id = table.Column<long>(type: "bigint", nullable: false),
                    author_login = table.Column<string>(type: "text", nullable: true),
                    command_name = table.Column<string>(type: "text", nullable: true),
                    arguments = table.Column<string>(type: "jsonb", nullable: false),
                    parser_version = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    source_body_snapshot = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    parsed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    rejection_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_command", x => x.id);
                    table.ForeignKey(
                        name: "fk_agent_command_work_item_work_item_id",
                        column: x => x.work_item_id,
                        principalTable: "work_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_command_github_comment_id",
                table: "agent_command",
                column: "github_comment_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agent_command_status_created_at",
                table: "agent_command",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_agent_command_work_item_id",
                table: "agent_command",
                column: "work_item_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_command");
        }
    }
}
