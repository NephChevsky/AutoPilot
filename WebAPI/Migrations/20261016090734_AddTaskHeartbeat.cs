using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoPilot.WebAPI.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskHeartbeat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "heartbeat_date_time",
                table: "tasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "processing_instance_id",
                table: "tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "processing_process_id",
                table: "tasks",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "heartbeat_date_time",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "processing_instance_id",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "processing_process_id",
                table: "tasks");
        }
    }
}
