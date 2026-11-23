using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SnapshotContentExactBytes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The frozen RunContextSnapshot.content must survive storage byte-exact — a jsonb
            // column re-serializes (key order/whitespace), so the content_hash and the bytes served via
            // /input would diverge. `USING` is required (jsonb → text has no implicit cast); the cast is
            // lossless for valid JSON. Down: text → jsonb expects valid JSON (this column stores exactly that).
            migrationBuilder.Sql(
                "ALTER TABLE run_context_snapshot ALTER COLUMN content TYPE text USING content::text");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE run_context_snapshot ALTER COLUMN content TYPE jsonb USING content::jsonb");
        }
    }
}
