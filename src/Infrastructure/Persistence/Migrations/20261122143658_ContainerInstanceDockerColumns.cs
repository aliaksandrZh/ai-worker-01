using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PiAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContainerInstanceDockerColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "komodo_deployment_id",
                table: "container_instance",
                newName: "docker_container_id");

            migrationBuilder.RenameColumn(
                name: "deployment_name",
                table: "container_instance",
                newName: "container_name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "docker_container_id",
                table: "container_instance",
                newName: "komodo_deployment_id");

            migrationBuilder.RenameColumn(
                name: "container_name",
                table: "container_instance",
                newName: "deployment_name");
        }
    }
}
