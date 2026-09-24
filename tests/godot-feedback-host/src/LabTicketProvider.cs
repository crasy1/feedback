using GdFeedback;
using Steamworks;

/// <summary>
/// 宿主侧票据来源。gd_feedback 插件本身不引用 Steam —— 出票是宿主的责任；这里就用本工程
/// vendored 的 addons/steamworks（Facepunch.Steamworks）出票，或退回界面里手输的票据。
///
/// 线程安全：本类只持有**主线程快照下来的纯数据**（是否用 Steam、手输票据、日志回调），
/// 绝不读 Godot 节点。运行时可能在后台线程调到这里（插件只保证信号经 CallDeferred 回主线程，
/// 出票发生得更早），所以日志回调由 FeedbackLab 负责用 CallDeferred 落回主线程。
/// </summary>
internal sealed class LabTicketProvider(bool useSteam, string manualTicket, Action<string> log) : ITicketProvider
{
    private const double SteamTicketTimeoutSeconds = 10.0;

    /// <summary>Steam 当前状态（主线程调用；只碰 Facepunch 的托管状态，不做 native 调用）。</summary>
    internal static string Status() => SteamClient.IsValid
        ? "Steam 已初始化，可以用真实票据登录"
        : "Steam 未初始化：启用 steamworks 插件（Project Settings → Plugins，它会注册 SteamManager）并确认 Steam 客户端在运行";

    public async Task<string?> GetWebApiTicketAsync(string identity, CancellationToken cancellationToken)
    {
        if (useSteam)
        {
            if (!SteamClient.IsValid)
            {
                log($"{Status()}。本次将 fail closed（ticket_unavailable），不会退回匿名或手输票据。");
                return null;
            }

            AuthTicket? ticket = await SteamUser.GetAuthTicketForWebApiAsync(identity, SteamTicketTimeoutSeconds);
            if (ticket is null)
            {
                log("Steam 出票失败（回调超时或后端拒绝）。本次将 fail closed。");
                return null;
            }

            try
            {
                // Steam 后端要的是十六进制票据串；Facepunch 只给 byte[]，转换由调用方负责。
                // 票据内容绝不写进日志，只记长度。
                string hex = Convert.ToHexString(ticket.Data).ToLowerInvariant();
                log($"Steam 出票成功：identity={identity}，{hex.Length} 个十六进制字符（内容不写日志）");
                return hex;
            }
            finally
            {
                // AuthTicket 持有 native handle，必须释放。
                ticket.Dispose();
            }
        }

        log(manualTicket.Length == 0
            ? $"票据提供者被调用（identity={identity}）：未用 Steam 且手输票据为空 → 插件将 fail closed"
            : $"票据提供者被调用（identity={identity}）：使用手输票据（{manualTicket.Length} 字符）");
        return string.IsNullOrEmpty(manualTicket) ? null : manualTicket;
    }
}
