using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace GameFeedback.Services;

/// <summary>签发本地玩家访问令牌：sub = SteamID64，24 小时有效。</summary>
public class TokenService(IOptions<JwtOptions> jwtOptions)
{
    public const double TokenLifetimeHours = 24;

    public (string AccessToken, DateTime ExpiresAtUtc) CreateToken(string steamId)
    {
        var jwt = jwtOptions.Value;
        var expiresAt = DateTime.UtcNow.AddHours(TokenLifetimeHours);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, steamId),
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
