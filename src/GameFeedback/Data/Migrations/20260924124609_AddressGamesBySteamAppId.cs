using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameFeedback.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// 把寻址标识从自造的 slug 换成 Steam AppID（见 ADR-0007 的修订）。
    /// <para>
    /// 这里只删列：<c>games.SteamAppId</c> 本来就在，玩家 API 的路径段改用它。
    /// 删除的应用前提是「还没有任何已发布构建把 BaseUrl 指向 <c>/g/&lt;slug&gt;</c>」，
    /// 所以不需要保留别名解析。
    /// </para>
    /// </summary>
    public partial class AddressGamesBySteamAppId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_games_Slug",
                table: "games");

            migrationBuilder.DropColumn(
                name: "Slug",
                table: "games");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Slug",
                table: "games",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            // slug 的值无法还原（当初为什么删掉它，见 ADR-0007 的修订）。这里按行号生成唯一且合法
            // 的占位 slug，好让下面的唯一索引建得起来——**必须赶在建索引之前**：
            // 否则多行都会是空串，唯一索引直接建失败。
            //
            // 注意这些值是合成的，客户端不认识：回滚应用版本之后，必须人工把每个游戏的 slug
            // 改回客户端 BaseUrl 里用的那个，否则玩家会拿到 404 game_not_found。
            migrationBuilder.Sql("""UPDATE games SET "Slug" = 'game-' || "Id";""");

            migrationBuilder.CreateIndex(
                name: "IX_games_Slug",
                table: "games",
                column: "Slug",
                unique: true);
        }
    }
}
