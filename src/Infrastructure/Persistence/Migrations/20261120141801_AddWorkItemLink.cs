using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkItemLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "work_item_link",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    issue_work_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pr_work_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    relation_type = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_work_item_link", x => x.id);
                    table.ForeignKey(
                        name: "fk_work_item_link_work_item_issue_work_item_id",
                        column: x => x.issue_work_item_id,
                        principalTable: "work_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_work_item_link_work_item_pr_work_item_id",
                        column: x => x.pr_work_item_id,
                        principalTable: "work_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_work_item_link_issue_work_item_id_pr_work_item_id_relation_",
                table: "work_item_link",
                columns: new[] { "issue_work_item_id", "pr_work_item_id", "relation_type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_work_item_link_pr_work_item_id",
                table: "work_item_link",
                column: "pr_work_item_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "work_item_link");
        }
    }
}
