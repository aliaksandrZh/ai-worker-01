using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPollerStores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "work_item",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository_id = table.Column<Guid>(type: "uuid", nullable: false),
                    github_node_id = table.Column<string>(type: "text", nullable: false),
                    number = table.Column<long>(type: "bigint", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: true),
                    state = table.Column<string>(type: "text", nullable: false),
                    author_login = table.Column<string>(type: "text", nullable: false),
                    assignees = table.Column<string>(type: "jsonb", nullable: false),
                    labels = table.Column<string>(type: "jsonb", nullable: false),
                    created_at_github = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_github = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    closed_at_github = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    head_sha = table.Column<string>(type: "text", nullable: true),
                    base_sha = table.Column<string>(type: "text", nullable: true),
                    draft = table.Column<bool>(type: "boolean", nullable: true),
                    merged_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    requested_reviewers = table.Column<string>(type: "jsonb", nullable: true),
                    last_synced_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    cleanup_status = table.Column<string>(type: "text", nullable: true),
                    closed_at_local = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_work_item", x => x.id);
                    table.ForeignKey(
                        name: "fk_work_item_repository_repository_id",
                        column: x => x.repository_id,
                        principalTable: "repository",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "github_comment",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    github_comment_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    author_login = table.Column<string>(type: "text", nullable: true),
                    body = table.Column<string>(type: "text", nullable: false),
                    created_at_github = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_github = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    in_reply_to_comment_id = table.Column<long>(type: "bigint", nullable: true),
                    github_review_id = table.Column<long>(type: "bigint", nullable: true),
                    path = table.Column<string>(type: "text", nullable: true),
                    commit_sha = table.Column<string>(type: "text", nullable: true),
                    diff_position = table.Column<int>(type: "integer", nullable: true),
                    last_synced_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_github_comment", x => x.id);
                    table.ForeignKey(
                        name: "fk_github_comment_work_item_work_item_id",
                        column: x => x.work_item_id,
                        principalTable: "work_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "github_event",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository_id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    external_event_id = table.Column<string>(type: "text", nullable: true),
                    dedupe_key = table.Column<string>(type: "text", nullable: true),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at_github = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    processing_status = table.Column<string>(type: "text", nullable: false, defaultValue: "pending"),
                    processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    processing_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_github_event", x => x.id);
                    table.ForeignKey(
                        name: "fk_github_event_repository_repository_id",
                        column: x => x.repository_id,
                        principalTable: "repository",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_github_event_work_item_work_item_id",
                        column: x => x.work_item_id,
                        principalTable: "work_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "github_review",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    github_review_id = table.Column<long>(type: "bigint", nullable: false),
                    author_login = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    commit_sha = table.Column<string>(type: "text", nullable: true),
                    submitted_at_github = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_synced_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_github_review", x => x.id);
                    table.ForeignKey(
                        name: "fk_github_review_work_item_work_item_id",
                        column: x => x.work_item_id,
                        principalTable: "work_item",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_github_comment_kind_github_comment_id",
                table: "github_comment",
                columns: new[] { "kind", "github_comment_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_github_comment_work_item_id_created_at_github",
                table: "github_comment",
                columns: new[] { "work_item_id", "created_at_github" });

            migrationBuilder.CreateIndex(
                name: "ix_github_event_repository_id_processing_status_received_at",
                table: "github_event",
                columns: new[] { "repository_id", "processing_status", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ix_github_event_source_dedupe_key",
                table: "github_event",
                columns: new[] { "source", "dedupe_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_github_event_work_item_id",
                table: "github_event",
                column: "work_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_github_review_github_review_id",
                table: "github_review",
                column: "github_review_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_github_review_work_item_id",
                table: "github_review",
                column: "work_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_work_item_github_node_id",
                table: "work_item",
                column: "github_node_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_work_item_repository_id_number",
                table: "work_item",
                columns: new[] { "repository_id", "number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "github_comment");

            migrationBuilder.DropTable(
                name: "github_event");

            migrationBuilder.DropTable(
                name: "github_review");

            migrationBuilder.DropTable(
                name: "work_item");
        }
    }
}
