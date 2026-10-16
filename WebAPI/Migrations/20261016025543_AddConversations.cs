using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoPilot.WebAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    creation_date_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conversations", x => x.id);
                    table.ForeignKey(
                        name: "fk_conversations_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_conversations_task_id",
                table: "conversations",
                column: "task_id");

            migrationBuilder.Sql(@"
                INSERT INTO conversations (id, task_id, author, content, creation_date_time)
                SELECT gen_random_uuid(), id, 'Neph', task, creation_date_time FROM tasks;
                INSERT INTO conversations (id, task_id, author, content, creation_date_time)
                SELECT gen_random_uuid(), id, 'Autopilot', response, COALESCE(completion_date_time, creation_date_time) FROM tasks WHERE response IS NOT NULL;");

            migrationBuilder.DropColumn(
                name: "response",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "task",
                table: "tasks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conversations");

            migrationBuilder.AddColumn<string>(
                name: "response",
                table: "tasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "task",
                table: "tasks",
                type: "text",
                nullable: false,
                defaultValue: "");
        }
    }
}
