using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GameFeedback.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "players",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SteamId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SteamName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    AvatarUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_players", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "feedbacks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlayerId = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Content = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    GameVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    BuildNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OperatingSystem = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Gpu = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Locale = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Map = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Character = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feedbacks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_feedbacks_players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "feedback_comments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FeedbackId = table.Column<int>(type: "integer", nullable: false),
                    AuthorType = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    PlayerId = table.Column<int>(type: "integer", nullable: true),
                    AdminUserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Content = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feedback_comments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_feedback_comments_feedbacks_FeedbackId",
                        column: x => x.FeedbackId,
                        principalTable: "feedbacks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_feedback_comments_FeedbackId",
                table: "feedback_comments",
                column: "FeedbackId");

            migrationBuilder.CreateIndex(
                name: "IX_feedbacks_CreatedAt",
                table: "feedbacks",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_feedbacks_GameVersion",
                table: "feedbacks",
                column: "GameVersion");

            migrationBuilder.CreateIndex(
                name: "IX_feedbacks_PlayerId",
                table: "feedbacks",
                column: "PlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_feedbacks_Status",
                table: "feedbacks",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_players_SteamId",
                table: "players",
                column: "SteamId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "feedback_comments");

            migrationBuilder.DropTable(
                name: "feedbacks");

            migrationBuilder.DropTable(
                name: "players");
        }
    }
}
