using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameFeedback.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFeedbackEnvInfoAndPlaytime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Cpu",
                table: "feedbacks",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MemoryTotalMb",
                table: "feedbacks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlaytimeMinutes",
                table: "feedbacks",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Cpu",
                table: "feedbacks");

            migrationBuilder.DropColumn(
                name: "MemoryTotalMb",
                table: "feedbacks");

            migrationBuilder.DropColumn(
                name: "PlaytimeMinutes",
                table: "feedbacks");
        }
    }
}
