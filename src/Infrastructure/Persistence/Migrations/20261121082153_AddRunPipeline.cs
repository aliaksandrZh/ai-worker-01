using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRunPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "run_context_snapshot",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    included_sources = table.Column<string>(type: "jsonb", nullable: false),
                    excluded_sources = table.Column<string>(type: "jsonb", nullable: false),
                    content = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_run_context_snapshot", x => x.id);
                    table.ForeignKey(
                        name: "fk_run_context_snapshot_run_run_id",
                        column: x => x.run_id,
                        principalTable: "run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_run_context_snapshot_task_task_id",
                        column: x => x.task_id,
                        principalTable: "task",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "run_credential",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "varchar(64)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_run_credential", x => x.id);
                    table.ForeignKey(
                        name: "fk_run_credential_run_run_id",
                        column: x => x.run_id,
                        principalTable: "run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "run_event",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: true),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_run_event", x => x.id);
                    table.ForeignKey(
                        name: "fk_run_event_run_run_id",
                        column: x => x.run_id,
                        principalTable: "run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workspace",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_kind = table.Column<string>(type: "text", nullable: false),
                    storage_reference = table.Column<string>(type: "text", nullable: true),
                    branch_name = table.Column<string>(type: "text", nullable: true),
                    base_sha = table.Column<string>(type: "text", nullable: true),
                    current_sha = table.Column<string>(type: "text", nullable: true),
                    access_mode = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    storage_status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workspace", x => x.id);
                    table.ForeignKey(
                        name: "fk_workspace_task_task_id",
                        column: x => x.task_id,
                        principalTable: "task",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_run_context_snapshot_run_id",
                table: "run_context_snapshot",
                column: "run_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_run_context_snapshot_task_id",
                table: "run_context_snapshot",
                column: "task_id");

            migrationBuilder.CreateIndex(
                name: "ix_run_credential_run_id",
                table: "run_credential",
                column: "run_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_run_event_run_id_event_id",
                table: "run_event",
                columns: new[] { "run_id", "event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_run_event_run_id_received_at",
                table: "run_event",
                columns: new[] { "run_id", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ix_workspace_task_id",
                table: "workspace",
                column: "task_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "run_context_snapshot");

            migrationBuilder.DropTable(
                name: "run_credential");

            migrationBuilder.DropTable(
                name: "run_event");

            migrationBuilder.DropTable(
                name: "workspace");
        }
    }
}
