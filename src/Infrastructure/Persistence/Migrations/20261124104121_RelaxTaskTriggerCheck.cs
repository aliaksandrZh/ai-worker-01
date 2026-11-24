using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RelaxTaskTriggerCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_task_has_trigger",
                table: "task");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_task_has_trigger",
                table: "task",
                sql: "trigger_command_id IS NOT NULL OR trigger_github_event_id IS NOT NULL");
        }
    }
}
