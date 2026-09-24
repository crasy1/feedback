using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace GameFeedback.Services;

/// <summary>
/// 签发本地玩家访问令牌：sub = SteamID64，game = Game 的数字 Id，24 小时有效。
/// <para>
/// game 声明让令牌与被签发的游戏绑定：用一个游戏换来的令牌拿到另一个游戏的路径下使用会 401，
/// 而「路径里的 AppID」与「令牌里的 GameId」的一致性由服务器逐请求核对，客户端无从绕过。
/// </para>
/// </summary>
public class TokenService(IOptions<JwtOptions> jwtOptions)
{
    public const double TokenLifetimeHours = 24;

    /// <summary>令牌里的游戏声明名。改动它等于让所有已签发令牌失效。</summary>
    public const string GameClaimType = "game";

    public (string AccessToken, DateTime ExpiresAtUtc) CreateToken(string steamId, int gameId)
    {
        var jwt = jwtOptions.Value;
        var expiresAt = DateTime.UtcNow.AddHours(TokenLifetimeHours);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, steamId),
            new Claim(GameClaimType, gameId.ToString(CultureInfo.InvariantCulture)),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: jwt.Issuer,
            audience: jwt.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAt,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
