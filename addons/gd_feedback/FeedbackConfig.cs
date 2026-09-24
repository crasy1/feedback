using Godot;

namespace GdFeedback;

/// <summary>
/// 插件配置。团队默认值放在 <c>res://feedback_config.tres</c>（宿主自己的文件，升级插件不会覆盖），
/// 插件目录内的同名文件只作次级兜底；开发者的本机 BaseUrl 可以用 EditorSettings 覆盖，不进版本库。
/// </summary>
[GlobalClass]
public partial class FeedbackConfig : Resource
{
    /// <summary>宿主覆盖文件（推荐放在项目根）。</summary>
    public const string ProjectConfigPath = "res://feedback_config.tres";

    /// <summary>插件目录内的兜底配置。</summary>
    public const string AddonConfigPath = "res://addons/gd_feedback/feedback_config.tres";

    /// <summary>EditorSettings 里覆盖本机 BaseUrl 的键名（只在编辑器/编辑器运行时生效）。</summary>
    public const string EditorBaseUrlSetting = "gd_feedback/base_url";

    [Export]
    public string BaseUrl { get; set; } = "http://localhost:5087";

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

    /// <summary>按"宿主覆盖 → 插件兜底 → 内置默认"的顺序读取配置。</summary>
    public static FeedbackConfig Load()
    {
        FeedbackConfig config = LoadFrom(ProjectConfigPath) ?? LoadFrom(AddonConfigPath) ?? new FeedbackConfig();
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
