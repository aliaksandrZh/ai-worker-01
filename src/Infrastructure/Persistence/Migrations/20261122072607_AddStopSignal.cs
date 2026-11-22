using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStopSignal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "stop_reason",
                table: "run",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "stop_requested_at",
                table: "run",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "stop_reason",
                table: "run");

            migrationBuilder.DropColumn(
                name: "stop_requested_at",
                table: "run");
        }
    }
}
