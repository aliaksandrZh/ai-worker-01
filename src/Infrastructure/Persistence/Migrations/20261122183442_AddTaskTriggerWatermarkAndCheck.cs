using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskTriggerWatermarkAndCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "trigger_at_github",
                table: "task",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_task_has_trigger",
                table: "task",
                sql: "trigger_command_id IS NOT NULL OR trigger_github_event_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_task_has_trigger",
                table: "task");

            migrationBuilder.DropColumn(
                name: "trigger_at_github",
                table: "task");
        }
    }
}
