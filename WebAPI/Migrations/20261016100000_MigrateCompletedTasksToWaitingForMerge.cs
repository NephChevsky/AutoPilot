using AutoPilot.WebAPI.Database;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoPilot.WebAPI.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20261016100000_MigrateCompletedTasksToWaitingForMerge")]
    public partial class MigrateCompletedTasksToWaitingForMerge : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE tasks SET status = 'WaitingForMerge' WHERE status = 'Completed' AND branch_name IS NOT NULL;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE tasks SET status = 'Completed' WHERE status = 'WaitingForMerge';");
        }
    }
}
