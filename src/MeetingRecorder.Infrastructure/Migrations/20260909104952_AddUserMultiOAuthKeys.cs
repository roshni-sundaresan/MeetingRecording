using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingRecorder.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserMultiOAuthKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GoogleOAuthKey",
                table: "Users",
                type: "nvarchar(max)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MicrosoftOAuthKey",
                table: "Users",
                type: "nvarchar(max)",
                maxLength: 8000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GoogleOAuthKey",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "MicrosoftOAuthKey",
                table: "Users");
        }
    }
}
