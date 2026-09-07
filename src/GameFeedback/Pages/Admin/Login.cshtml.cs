using GameFeedback.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GameFeedback.RazorPages.Admin;

/// <summary>管理员登录页。Cookie 签发必须在静态 SSR / Razor Pages 上完成，
/// 不能在 Interactive Server 组件中进行。</summary>
public class LoginModel(SignInManager<IdentityUser> signInManager, ILogger<LoginModel> logger) : PageModel
{
    [BindProperty]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    public string? ErrorMessage { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var result = await signInManager.PasswordSignInAsync(Email, Password, isPersistent: false, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            logger.LogInformation("管理员登录成功");
            return LocalRedirect("/admin/feedback");
        }

        ErrorMessage = "邮箱或密码不正确。";
        return Page();
    }
}
