using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoPilot.WebAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskCost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "cost",
                table: "tasks",
                type: "numeric",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cost",
                table: "tasks");
        }
    }
}
