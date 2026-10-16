using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoPilot.WebAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskTitle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "title",
                table: "tasks",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "title",
                table: "tasks");
        }
    }
}
