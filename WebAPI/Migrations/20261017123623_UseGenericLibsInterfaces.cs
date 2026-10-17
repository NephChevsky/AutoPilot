using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoPilot.WebAPI.Migrations
{
    /// <inheritdoc />
    public partial class UseGenericLibsInterfaces : Migration
    {
        private static readonly string[] SoftDeletableTables =
            ["users", "devices", "repositories", "tasks", "conversations", "chats", "chat_messages"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Unique GitHub id must ignore soft-deleted rows
            migrationBuilder.DropIndex(
                name: "ix_repositories_github_id",
                table: "repositories");

            // Renames (data preserved)
            migrationBuilder.RenameColumn(name: "creation_date_time", table: "tasks", newName: "created_at");
            migrationBuilder.RenameColumn(name: "creation_date_time", table: "conversations", newName: "created_at");
            migrationBuilder.RenameColumn(name: "creation_date_time", table: "chat_messages", newName: "created_at");
            migrationBuilder.RenameColumn(name: "creation_date_time", table: "chats", newName: "created_at");
            migrationBuilder.RenameColumn(name: "update_date_time", table: "chats", newName: "updated_at");
            migrationBuilder.RenameIndex(
                name: "ix_chats_user_id_update_date_time",
                table: "chats",
                newName: "ix_chats_user_id_updated_at");

            // New updated_at columns on tables that already had created_at: start equal to created_at
            foreach (string table in new[] { "tasks", "conversations", "chat_messages" })
            {
                migrationBuilder.AddColumn<DateTime>(
                    name: "updated_at",
                    table: table,
                    type: "timestamp with time zone",
                    nullable: false,
                    defaultValueSql: "now()");
                migrationBuilder.Sql($"UPDATE {table} SET updated_at = created_at;");
                migrationBuilder.Sql($"ALTER TABLE {table} ALTER COLUMN updated_at DROP DEFAULT;");
            }

            // Tables with no previous timestamps (devices already has both): existing rows get the migration time
            foreach (string table in new[] { "users", "repositories" })
            {
                foreach (string column in new[] { "created_at", "updated_at" })
                {
                    migrationBuilder.AddColumn<DateTime>(
                        name: column,
                        table: table,
                        type: "timestamp with time zone",
                        nullable: false,
                        defaultValueSql: "now()");
                    migrationBuilder.Sql($"ALTER TABLE {table} ALTER COLUMN {column} DROP DEFAULT;");
                }
            }

            foreach (string table in SoftDeletableTables)
            {
                migrationBuilder.AddColumn<bool>(
                    name: "is_deleted",
                    table: table,
                    type: "boolean",
                    nullable: false,
                    defaultValue: false);
            }

            // Owner (chats / chat_messages): backfill from the chat's user
            migrationBuilder.AddColumn<Guid>(
                name: "owner",
                table: "chats",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));
            migrationBuilder.AddColumn<Guid>(
                name: "owner",
                table: "chat_messages",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));
            migrationBuilder.Sql("UPDATE chats SET owner = user_id;");
            migrationBuilder.Sql("UPDATE chat_messages m SET owner = c.user_id FROM chats c WHERE c.id = m.chat_id;");

            migrationBuilder.CreateIndex(
                name: "ix_repositories_github_id",
                table: "repositories",
                column: "github_id",
                unique: true,
                filter: "is_deleted = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_repositories_github_id",
                table: "repositories");

            migrationBuilder.DropColumn(name: "owner", table: "chats");
            migrationBuilder.DropColumn(name: "owner", table: "chat_messages");

            foreach (string table in SoftDeletableTables)
            {
                migrationBuilder.DropColumn(name: "is_deleted", table: table);
            }

            migrationBuilder.DropColumn(name: "created_at", table: "users");
            migrationBuilder.DropColumn(name: "updated_at", table: "users");
            migrationBuilder.DropColumn(name: "created_at", table: "repositories");
            migrationBuilder.DropColumn(name: "updated_at", table: "repositories");
            migrationBuilder.DropColumn(name: "updated_at", table: "tasks");
            migrationBuilder.DropColumn(name: "updated_at", table: "conversations");
            migrationBuilder.DropColumn(name: "updated_at", table: "chat_messages");

            migrationBuilder.RenameIndex(
                name: "ix_chats_user_id_updated_at",
                table: "chats",
                newName: "ix_chats_user_id_update_date_time");
            migrationBuilder.RenameColumn(name: "updated_at", table: "chats", newName: "update_date_time");
            migrationBuilder.RenameColumn(name: "created_at", table: "chats", newName: "creation_date_time");
            migrationBuilder.RenameColumn(name: "created_at", table: "chat_messages", newName: "creation_date_time");
            migrationBuilder.RenameColumn(name: "created_at", table: "conversations", newName: "creation_date_time");
            migrationBuilder.RenameColumn(name: "created_at", table: "tasks", newName: "creation_date_time");

            migrationBuilder.CreateIndex(
                name: "ix_repositories_github_id",
                table: "repositories",
                column: "github_id",
                unique: true);
        }
    }
}
