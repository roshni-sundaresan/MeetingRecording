using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetingRecorder.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserOAuthKeyAndProviderName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OAuthKey",
                table: "Users",
                type: "nvarchar(max)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderName",
                table: "Users",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OAuthKey",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ProviderName",
                table: "Users");
        }
    }
}
