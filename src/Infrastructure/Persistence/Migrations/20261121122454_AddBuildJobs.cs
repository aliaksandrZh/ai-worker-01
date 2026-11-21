using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBuildJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "build_job",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_profile_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dispatch_payload = table.Column<string>(type: "jsonb", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_retry_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    image_tag = table.Column<string>(type: "text", nullable: true),
                    image_digest = table.Column<string>(type: "text", nullable: true),
                    build_log_reference = table.Column<string>(type: "text", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_build_job", x => x.id);
                    table.ForeignKey(
                        name: "fk_build_job_agent_profile_version_agent_profile_version_id",
                        column: x => x.agent_profile_version_id,
                        principalTable: "agent_profile_version",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_build_job_agent_profile_version_id",
                table: "build_job",
                column: "agent_profile_version_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_build_job_state_next_retry_at",
                table: "build_job",
                columns: new[] { "state", "next_retry_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "build_job");
        }
    }
}
