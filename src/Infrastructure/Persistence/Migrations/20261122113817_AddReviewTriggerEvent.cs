using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewTriggerEvent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "trigger_github_event_id",
                table: "task",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_task_trigger_github_event_id",
                table: "task",
                column: "trigger_github_event_id",
                unique: true,
                filter: "trigger_github_event_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_task_github_event_trigger_github_event_id",
                table: "task",
                column: "trigger_github_event_id",
                principalTable: "github_event",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_task_github_event_trigger_github_event_id",
                table: "task");

            migrationBuilder.DropIndex(
                name: "ix_task_trigger_github_event_id",
                table: "task");

            migrationBuilder.DropColumn(
                name: "trigger_github_event_id",
                table: "task");
        }
    }
}
