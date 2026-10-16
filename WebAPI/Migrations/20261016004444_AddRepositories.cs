using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoPilot.WebAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddRepositories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "repositories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    github_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    clone_url = table.Column<string>(type: "text", nullable: false),
                    html_url = table.Column<string>(type: "text", nullable: false),
                    default_branch = table.Column<string>(type: "text", nullable: false),
                    pinned = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_repositories", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_repositories_github_id",
                table: "repositories",
                column: "github_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "repositories");
        }
    }
}
