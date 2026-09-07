using GameFeedback.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GameFeedback.Services;

/// <summary>启动时种子首个管理员：仅当数据库没有任何管理员账号时执行，绝不改动现有安装。</summary>
public class AdminSeeder(UserManager<IdentityUser> userManager, AppDbContext db, IOptions<AdminOptions> adminOptions, ILogger<AdminSeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        if (await db.Users.AnyAsync(cancellationToken))
        {
            return;
        }

        var admin = adminOptions.Value;
        var user = new IdentityUser
        {
            UserName = admin.SeedEmail,
            Email = admin.SeedEmail,
            EmailConfirmed = true,
        };
        var result = await userManager.CreateAsync(user, admin.SeedPassword);
        if (result.Succeeded)
        {
            logger.LogInformation("已创建初始管理员账号");
        }
        else
        {
            logger.LogError("初始管理员创建失败：{Errors}", string.Join(";", result.Errors.Select(e => e.Description)));
        }
    }
}
