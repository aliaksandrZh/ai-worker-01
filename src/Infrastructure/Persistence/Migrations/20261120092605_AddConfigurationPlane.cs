using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConfigurationPlane : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_profile",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_profile", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "repository",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    github_repository_id = table.Column<long>(type: "bigint", nullable: false),
                    owner = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    full_name = table.Column<string>(type: "text", nullable: false),
                    default_branch = table.Column<string>(type: "text", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    configuration = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_repository", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "agent_profile_version",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    source_commit_sha = table.Column<string>(type: "text", nullable: true),
                    source_directory = table.Column<string>(type: "text", nullable: false),
                    config_snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    config_hash = table.Column<string>(type: "text", nullable: false),
                    runtime_type = table.Column<string>(type: "text", nullable: false),
                    system_prompt = table.Column<string>(type: "text", nullable: true),
                    skills_configuration = table.Column<string>(type: "jsonb", nullable: true),
                    mcp_configuration = table.Column<string>(type: "jsonb", nullable: true),
                    tools_configuration = table.Column<string>(type: "jsonb", nullable: true),
                    environment_configuration = table.Column<string>(type: "jsonb", nullable: true),
                    resource_configuration = table.Column<string>(type: "jsonb", nullable: true),
                    capabilities = table.Column<string>(type: "jsonb", nullable: true),
                    retry_policy = table.Column<string>(type: "jsonb", nullable: true),
                    permission_policy = table.Column<string>(type: "jsonb", nullable: true),
                    bridge_configuration = table.Column<string>(type: "jsonb", nullable: true),
                    startup_timeout = table.Column<TimeSpan>(type: "interval", nullable: true),
                    idle_timeout = table.Column<TimeSpan>(type: "interval", nullable: true),
                    max_run_duration = table.Column<TimeSpan>(type: "interval", nullable: true),
                    max_context_tokens = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    container_image = table.Column<string>(type: "text", nullable: true),
                    image_digest = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_profile_version", x => x.id);
                    table.ForeignKey(
                        name: "fk_agent_profile_version_agent_profile_agent_profile_id",
                        column: x => x.agent_profile_id,
                        principalTable: "agent_profile",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sync_cursor",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stream = table.Column<string>(type: "text", nullable: false),
                    cursor_value = table.Column<string>(type: "text", nullable: true),
                    last_success_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_cursor", x => x.id);
                    table.ForeignKey(
                        name: "fk_sync_cursor_repository_repository_id",
                        column: x => x.repository_id,
                        principalTable: "repository",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agent_profile_routing_rule",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository_id = table.Column<Guid>(type: "uuid", nullable: true),
                    task_type = table.Column<string>(type: "text", nullable: false),
                    agent_profile_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_profile_routing_rule", x => x.id);
                    table.ForeignKey(
                        name: "fk_agent_profile_routing_rule_agent_profile_version_agent_prof",
                        column: x => x.agent_profile_version_id,
                        principalTable: "agent_profile_version",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_profile_name",
                table: "agent_profile",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agent_profile_routing_rule_agent_profile_version_id",
                table: "agent_profile_routing_rule",
                column: "agent_profile_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_agent_profile_routing_rule_repository_id_task_type_priority",
                table: "agent_profile_routing_rule",
                columns: new[] { "repository_id", "task_type", "priority" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agent_profile_version_agent_profile_id_version",
                table: "agent_profile_version",
                columns: new[] { "agent_profile_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_repository_full_name",
                table: "repository",
                column: "full_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_repository_github_repository_id",
                table: "repository",
                column: "github_repository_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sync_cursor_repository_id_stream",
                table: "sync_cursor",
                columns: new[] { "repository_id", "stream" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_profile_routing_rule");

            migrationBuilder.DropTable(
                name: "sync_cursor");

            migrationBuilder.DropTable(
                name: "agent_profile_version");

            migrationBuilder.DropTable(
                name: "repository");

            migrationBuilder.DropTable(
                name: "agent_profile");
        }
    }
}
