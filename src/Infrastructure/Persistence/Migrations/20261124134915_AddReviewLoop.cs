using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewLoop : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "head_ref",
                table: "work_item",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "review_fix_rounds",
                table: "work_item",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "head_ref",
                table: "work_item");

            migrationBuilder.DropColumn(
                name: "review_fix_rounds",
                table: "work_item");
        }
    }
}
