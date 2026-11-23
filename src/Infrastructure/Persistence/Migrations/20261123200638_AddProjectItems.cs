using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "project_sync",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner = table.Column<string>(type: "text", nullable: false),
                    project_number = table.Column<int>(type: "integer", nullable: false),
                    etag = table.Column<string>(type: "text", nullable: true),
                    last_success_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_sync", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "project_item",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_sync_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<long>(type: "bigint", nullable: false),
                    item_node_id = table.Column<string>(type: "text", nullable: true),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    repository_id = table.Column<Guid>(type: "uuid", nullable: true),
                    repository_full_name = table.Column<string>(type: "text", nullable: true),
                    number = table.Column<int>(type: "integer", nullable: true),
                    title = table.Column<string>(type: "text", nullable: false),
                    field_values = table.Column<string>(type: "jsonb", nullable: false),
                    created_at_github = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_github = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_synced_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_item", x => x.id);
                    table.ForeignKey(
                        name: "fk_project_item_project_sync_project_sync_id",
                        column: x => x.project_sync_id,
                        principalTable: "project_sync",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_project_item_repository_repository_id",
                        column: x => x.repository_id,
                        principalTable: "repository",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_project_item_project_sync_id_item_id",
                table: "project_item",
                columns: new[] { "project_sync_id", "item_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_project_item_repository_id",
                table: "project_item",
                column: "repository_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_sync_owner_project_number",
                table: "project_sync",
                columns: new[] { "owner", "project_number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_item");

            migrationBuilder.DropTable(
                name: "project_sync");
        }
    }
}
