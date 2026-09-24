using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GameFeedback.Data.Migrations
{
    /// <summary>
    /// 多游戏支持：新增 <c>games</c> 与 <c>steam_credentials</c>，并给 <c>players</c>/<c>feedbacks</c>
    /// 补上必需的 <c>GameId</c>。
    /// <para>
    /// 手写而非照抄脚手架的原因：EF 会为必需列生成 <c>defaultValue: 0</c>，而 0 不对应任何游戏，
    /// 紧接着的外键约束必然失败。正确顺序是「建表 → 插占位游戏 → 加可空列 → 回填 → 收紧非空 → 建外键」。
    /// </para>
    /// <para>
    /// 占位游戏的 SteamAppId 与凭据<b>刻意留空</b>：Steam 配置已经从环境变量搬进数据库，
    /// 所以升级后必须由管理员在管理端录入。在录入完成之前，玩家登录以 401 steam_unavailable
    /// fail closed（已知且有意接受的升级窗口）。
    /// </para>
    /// </summary>
    public partial class MultiGameSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "steam_credentials",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EncryptedApiKey = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_steam_credentials", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "games",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Slug = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SteamAppId = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    steam_identity = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: "feedback-api"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CredentialId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_games", x => x.Id);
                    table.ForeignKey(
                        name: "FK_games_steam_credentials_CredentialId",
                        column: x => x.CredentialId,
                        principalTable: "steam_credentials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // 存量反馈与玩家必须有个归属。slug 用 default，管理员可以随时改名，
            // 但要记得客户端 BaseUrl 里写的是 slug。
            //
            // 条件插入：只有在确实有存量数据需要回填时才建这个占位游戏。
            // 全新数据库一个玩家一条反馈都没有，此时 games 表保持为空——管理端据此把管理员
            // 强制送到「添加游戏」页，让他一次性填好 slug/AppID/凭据。
            // 无条件插入会让每一次全新部署都自带一个「未配置的默认游戏」，那条首启约束就永远不生效了。
            migrationBuilder.Sql(
                """
                INSERT INTO games ("Slug", "Name", "SteamAppId", "steam_identity", "IsActive", "CreatedAt", "UpdatedAt")
                SELECT 'default', '默认游戏', NULL, 'feedback-api', TRUE, now(), now()
                WHERE EXISTS (SELECT 1 FROM players) OR EXISTS (SELECT 1 FROM feedbacks);
                """);

            migrationBuilder.AddColumn<int>(
                name: "GameId",
                table: "players",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GameId",
                table: "feedbacks",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE players SET "GameId" = (SELECT "Id" FROM games WHERE "Slug" = 'default');
                UPDATE feedbacks SET "GameId" = (SELECT "Id" FROM games WHERE "Slug" = 'default');
                """);

            migrationBuilder.AlterColumn<int>(
                name: "GameId",
                table: "players",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "GameId",
                table: "feedbacks",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            // 全局唯一的 SteamId 换成 (Game, SteamId) 复合唯一：同一个账号在每个游戏下各有一行。
            migrationBuilder.DropIndex(
                name: "IX_players_SteamId",
                table: "players");

            migrationBuilder.DropIndex(
                name: "IX_feedbacks_GameVersion",
                table: "feedbacks");

            migrationBuilder.CreateIndex(
                name: "IX_players_GameId_SteamId",
                table: "players",
                columns: new[] { "GameId", "SteamId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_feedbacks_GameId_CreatedAt",
                table: "feedbacks",
                columns: new[] { "GameId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_feedbacks_GameId_GameVersion",
                table: "feedbacks",
                columns: new[] { "GameId", "GameVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_feedbacks_GameId_Status",
                table: "feedbacks",
                columns: new[] { "GameId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_games_CredentialId",
                table: "games",
                column: "CredentialId");

            migrationBuilder.CreateIndex(
                name: "IX_games_Slug",
                table: "games",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_games_SteamAppId",
                table: "games",
                column: "SteamAppId",
                unique: true);

            // Restrict 而不是 Cascade：删除一个游戏绝不允许顺手删掉它名下的玩家反馈。
            migrationBuilder.AddForeignKey(
                name: "FK_feedbacks_games_GameId",
                table: "feedbacks",
                column: "GameId",
                principalTable: "games",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_players_games_GameId",
                table: "players",
                column: "GameId",
                principalTable: "games",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 回滚到「一个部署只服务一个游戏」在语义上要求数据里确实只有一个游戏。
            // 存在多个游戏时明确报错，而不是让后面重建全局唯一索引时抛出难以理解的约束冲突，
            // 更不是悄悄丢掉其他游戏的数据。
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF (SELECT count(*) FROM games) > 1 THEN
                        RAISE EXCEPTION '无法回滚 MultiGameSupport：games 表里有多个游戏，回滚会丢失多游戏数据。请先删除多余的游戏。';
                    END IF;
                END
                $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_feedbacks_games_GameId",
                table: "feedbacks");

            migrationBuilder.DropForeignKey(
                name: "FK_players_games_GameId",
                table: "players");

            migrationBuilder.DropTable(
                name: "games");

            migrationBuilder.DropTable(
                name: "steam_credentials");

            migrationBuilder.DropIndex(
                name: "IX_players_GameId_SteamId",
                table: "players");

            migrationBuilder.DropIndex(
                name: "IX_feedbacks_GameId_CreatedAt",
                table: "feedbacks");

            migrationBuilder.DropIndex(
                name: "IX_feedbacks_GameId_GameVersion",
                table: "feedbacks");

            migrationBuilder.DropIndex(
                name: "IX_feedbacks_GameId_Status",
                table: "feedbacks");

            migrationBuilder.DropColumn(
                name: "GameId",
                table: "players");

            migrationBuilder.DropColumn(
                name: "GameId",
                table: "feedbacks");

            migrationBuilder.CreateIndex(
                name: "IX_players_SteamId",
                table: "players",
                column: "SteamId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_feedbacks_GameVersion",
                table: "feedbacks",
                column: "GameVersion");
        }
    }
}
