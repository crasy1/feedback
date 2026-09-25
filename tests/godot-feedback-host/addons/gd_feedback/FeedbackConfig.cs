using Godot;

namespace GdFeedback;

/// <summary>
/// 插件配置。团队默认值放在宿主自己的文件里（<c>res://feedback_config.tres</c>，短名
/// <c>res://feedback.tres</c> 等价），升级插件不会覆盖它；插件目录内的同名文件只作次级兜底，
/// 开发者的本机 BaseUrl 可以用 EditorSettings 覆盖，不进版本库。
/// <para>
/// 服务地址与 Steam AppID 都在这里：`BaseUrl` 只填服务地址，`SteamAppId` 填本游戏运行的 AppID，
/// 玩家 API 的 <c>/g/{appId}</c> 路径段由插件自己补（宿主因此不必手抄任何标识字符串）。
/// </para>
/// </summary>
[GlobalClass]
public partial class FeedbackConfig : Resource
{
    /// <summary>宿主覆盖文件（推荐放在项目根）。</summary>
    public const string ProjectConfigPath = "res://feedback_config.tres";

    /// <summary>
    /// 项目根下同样被接受的短名，内容与 <see cref="ProjectConfigPath"/> 完全同型。
    /// 两个文件都存在时以 <see cref="ProjectConfigPath"/> 为准。
    /// </summary>
    public const string ShortConfigPath = "res://feedback.tres";

    /// <summary>插件目录内的兜底配置。</summary>
    public const string AddonConfigPath = "res://addons/gd_feedback/feedback_config.tres";

    /// <summary>EditorSettings 里覆盖本机 BaseUrl 的键名（只在编辑器/编辑器运行时生效）。</summary>
    public const string EditorBaseUrlSetting = "gd_feedback/base_url";

    [Export]
    public string BaseUrl { get; set; } = "http://localhost:5087";

    /// <summary>
    /// 本游戏运行的 Steam AppID（纯数字，最多 10 位）。填了它，插件就把玩家 API 的路径补成
    /// <c>/g/{appId}/api/...</c>，<see cref="BaseUrl"/> 只填服务地址；留空则退回宿主注入的
    /// <see cref="IGameAppIdProvider"/>，两者都没有时才沿用"BaseUrl 里自带路径"的老行为。
    /// <para>
    /// 两个来源都给了以本配置为准。装填的是**地址**不是身份：服务端仍按自己的 games.SteamAppId
    /// 解析这个路径段，填错只会得到 <c>game_not_found</c> 或验票被拒，不会因此获得任何权限。
    /// </para>
    /// </summary>
    [Export]
    public string SteamAppId { get; set; } = string.Empty;

    [Export]
    public string Identity { get; set; } = "feedback-api";

    [Export]
    public double RequestTimeoutSeconds { get; set; } = 15.0;

    [Export]
    public bool AllowDebugLogin { get; set; }

    /// <summary>调试登录声明的 SteamID64；只在 <see cref="AllowDebugLogin"/> 为真时使用。</summary>
    [Export]
    public string DebugSteamId { get; set; } = string.Empty;

    /// <summary>显式代理；空表示使用 .NET 默认代理策略。</summary>
    [Export]
    public string Proxy { get; set; } = string.Empty;

    /// <summary>是否把访问令牌持久化到 <c>user://</c>；默认关闭（令牌只在内存里）。</summary>
    [Export]
    public bool CacheAccessToken { get; set; }

    [Export]
    public bool VerboseLogging { get; set; }

    /// <summary>
    /// 本次 <see cref="Load"/> 实际生效的文件路径；一个都没找到时为空串（用的是内置默认值）。
    /// 只用于诊断"到底读了哪个文件"——多个候选名都存在时，这件事不看日志是猜不出来的。
    /// </summary>
    public string LoadedFromPath { get; private set; } = string.Empty;

    /// <summary>按"宿主覆盖 → 宿主短名 → 插件兜底 → 内置默认"的顺序读取配置。</summary>
    public static FeedbackConfig Load()
    {
        FeedbackConfig? config = null;
        string loadedFrom = string.Empty;
        foreach (string candidate in new[] { ProjectConfigPath, ShortConfigPath, AddonConfigPath })
        {
            config = LoadFrom(candidate);
            if (config is not null)
            {
                loadedFrom = candidate;
                break;
            }
        }

        config ??= new FeedbackConfig();
        config.LoadedFromPath = loadedFrom;
#if TOOLS
        string? editorOverride = ReadEditorBaseUrlOverride();
        if (!string.IsNullOrWhiteSpace(editorOverride))
        {
            // LoadFrom 使用 CacheMode.Ignore，这里的赋值不会污染磁盘上的 .tres。
            config.BaseUrl = editorOverride!;
        }
#endif
        return config;
    }

    /// <summary>转成引擎无关的选项对象。</summary>
    public FeedbackOptions ToOptions() => new(
        BaseUrl,
        string.IsNullOrWhiteSpace(Identity) ? "feedback-api" : Identity,
        RequestTimeoutSeconds,
        AllowDebugLogin,
        string.IsNullOrWhiteSpace(DebugSteamId) ? null : DebugSteamId,
        string.IsNullOrWhiteSpace(Proxy) ? null : Proxy);

    private static FeedbackConfig? LoadFrom(string path)
    {
        if (!ResourceLoader.Exists(path))
        {
            return null;
        }
        return ResourceLoader.Load<FeedbackConfig>(path, cacheMode: ResourceLoader.CacheMode.Ignore);
    }

#if TOOLS
    private static string? ReadEditorBaseUrlOverride()
    {
        // 必须用 Engine.IsEditorHint()：OS.HasFeature("editor") 只表示"这是编辑器版二进制"，
        // 而用编辑器二进制以项目模式跑游戏（例如命令行 headless 运行）时并不在编辑器里，
        // 那时访问 EditorInterface.Singleton 会直接抛 "Can't retrieve singleton ... outside of editor"。
        if (!Engine.IsEditorHint())
        {
            return null;
        }

        EditorSettings? settings = EditorInterface.Singleton?.GetEditorSettings();
        if (settings is null || !settings.HasSetting(EditorBaseUrlSetting))
        {
            return null;
        }
        return settings.GetSetting(EditorBaseUrlSetting).AsString();
    }
#endif
}
