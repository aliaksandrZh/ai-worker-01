using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrchestratorCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workflow",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    current_task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    failure_reason = table.Column<string>(type: "text", nullable: true),
                    initial_review_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow", x => x.id);
                    table.ForeignKey(
                        name: "fk_workflow_work_item_work_item_id",
                        column: x => x.work_item_id,
                        principalTable: "work_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "task",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    agent_profile_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    trigger_command_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_head_sha = table.Column<string>(type: "text", nullable: true),
                    target_base_sha = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    claimed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    failure_reason = table.Column<string>(type: "text", nullable: true),
                    retry_policy = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_task", x => x.id);
                    table.ForeignKey(
                        name: "fk_task_agent_command_trigger_command_id",
                        column: x => x.trigger_command_id,
                        principalTable: "agent_command",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_task_agent_profile_version_agent_profile_version_id",
                        column: x => x.agent_profile_version_id,
                        principalTable: "agent_profile_version",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_task_workflow_workflow_id",
                        column: x => x.workflow_id,
                        principalTable: "workflow",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "artifact",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    format = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_artifact", x => x.id);
                    table.ForeignKey(
                        name: "fk_artifact_task_task_id",
                        column: x => x.task_id,
                        principalTable: "task",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_artifact_workflow_workflow_id",
                        column: x => x.workflow_id,
                        principalTable: "workflow",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "orchestration_job",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_type = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    available_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    locked_by = table.Column<string>(type: "text", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orchestration_job", x => x.id);
                    table.ForeignKey(
                        name: "fk_orchestration_job_task_task_id",
                        column: x => x.task_id,
                        principalTable: "task",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "run",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_number = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_heartbeat_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_progress_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    exit_code = table.Column<int>(type: "integer", nullable: true),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    result_summary = table.Column<string>(type: "text", nullable: true),
                    log_storage_reference = table.Column<string>(type: "text", nullable: true),
                    log_size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    logs_completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    logs_deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_run", x => x.id);
                    table.ForeignKey(
                        name: "fk_run_task_task_id",
                        column: x => x.task_id,
                        principalTable: "task",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "task_artifact_input",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_task_artifact_input", x => x.id);
                    table.ForeignKey(
                        name: "fk_task_artifact_input_artifacts_artifact_id",
                        column: x => x.artifact_id,
                        principalTable: "artifact",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_task_artifact_input_task_task_id",
                        column: x => x.task_id,
                        principalTable: "task",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agent_session",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_profile_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    runtime_session_id = table.Column<string>(type: "text", nullable: true),
                    session_file_path = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_activity_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_heartbeat_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    file_deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    storage_status = table.Column<string>(type: "text", nullable: false, defaultValue: "available"),
                    metadata = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_session", x => x.id);
                    table.ForeignKey(
                        name: "fk_agent_session_agent_profile_version_agent_profile_version_id",
                        column: x => x.agent_profile_version_id,
                        principalTable: "agent_profile_version",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_agent_session_run_run_id",
                        column: x => x.run_id,
                        principalTable: "run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_agent_session_task_task_id",
                        column: x => x.task_id,
                        principalTable: "task",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "container_instance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    komodo_deployment_id = table.Column<string>(type: "text", nullable: true),
                    deployment_name = table.Column<string>(type: "text", nullable: true),
                    host_id = table.Column<string>(type: "text", nullable: true),
                    container_image = table.Column<string>(type: "text", nullable: true),
                    desired_state = table.Column<string>(type: "text", nullable: false),
                    observed_state = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_stopped_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    removed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_container_instance", x => x.id);
                    table.ForeignKey(
                        name: "fk_container_instance_agent_session_agent_session_id",
                        column: x => x.agent_session_id,
                        principalTable: "agent_session",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_container_instance_run_run_id",
                        column: x => x.run_id,
                        principalTable: "run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_session_agent_profile_version_id",
                table: "agent_session",
                column: "agent_profile_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_agent_session_run_id",
                table: "agent_session",
                column: "run_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agent_session_task_id",
                table: "agent_session",
                column: "task_id");

            migrationBuilder.CreateIndex(
                name: "ix_artifact_task_id",
                table: "artifact",
                column: "task_id");

            migrationBuilder.CreateIndex(
                name: "ix_artifact_workflow_id",
                table: "artifact",
                column: "workflow_id");

            migrationBuilder.CreateIndex(
                name: "ix_container_instance_agent_session_id",
                table: "container_instance",
                column: "agent_session_id");

            migrationBuilder.CreateIndex(
                name: "ix_container_instance_run_id",
                table: "container_instance",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_orchestration_job_status_available_at_priority",
                table: "orchestration_job",
                columns: new[] { "status", "available_at", "priority" });

            migrationBuilder.CreateIndex(
                name: "ix_orchestration_job_task_id",
                table: "orchestration_job",
                column: "task_id");

            migrationBuilder.CreateIndex(
                name: "ix_run_task_id_attempt_number",
                table: "run",
                columns: new[] { "task_id", "attempt_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_task_agent_profile_version_id",
                table: "task",
                column: "agent_profile_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_task_status_created_at",
                table: "task",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_task_trigger_command_id",
                table: "task",
                column: "trigger_command_id",
                unique: true,
                filter: "trigger_command_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_task_workflow_id_sequence",
                table: "task",
                columns: new[] { "workflow_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_task_one_unfinished_per_workflow",
                table: "task",
                column: "workflow_id",
                unique: true,
                filter: "status IN ('queued', 'claimed', 'starting', 'running')");

            migrationBuilder.CreateIndex(
                name: "ix_task_artifact_input_artifact_id",
                table: "task_artifact_input",
                column: "artifact_id");

            migrationBuilder.CreateIndex(
                name: "ix_task_artifact_input_task_id_artifact_id",
                table: "task_artifact_input",
                columns: new[] { "task_id", "artifact_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workflow_status_updated_at",
                table: "workflow",
                columns: new[] { "status", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_work_item_id",
                table: "workflow",
                column: "work_item_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "container_instance");

            migrationBuilder.DropTable(
                name: "orchestration_job");

            migrationBuilder.DropTable(
                name: "task_artifact_input");

            migrationBuilder.DropTable(
                name: "agent_session");

            migrationBuilder.DropTable(
                name: "artifact");

            migrationBuilder.DropTable(
                name: "run");

            migrationBuilder.DropTable(
                name: "task");

            migrationBuilder.DropTable(
                name: "workflow");
        }
    }
}
