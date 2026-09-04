using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingRecorder.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledMeetings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScheduledMeetings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Provider = table.Column<int>(type: "int", nullable: false),
                    StartTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TimeZone = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    JoinUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    MeetingCode = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Passcode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ExternalMeetingId = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AttendeesJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledMeetings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduledMeetings_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledMeetings_StartTime",
                table: "ScheduledMeetings",
                column: "StartTime");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledMeetings_UserId",
                table: "ScheduledMeetings",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScheduledMeetings");
        }
    }
}
