using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingRecorder.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserCustomApiKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CustomApiKey",
                table: "Users",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CustomApiKey",
                table: "Users");
        }
    }
}
