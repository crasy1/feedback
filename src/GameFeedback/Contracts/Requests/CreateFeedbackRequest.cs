using System.ComponentModel;

namespace GameFeedback.Contracts.Requests;

/// <summary>
/// 创建反馈请求；元数据由客户端提供、全部可选，SteamID 永远来自认证主体。
/// <c>Cpu</c> 与 <c>MemoryTotalMb</c> 通常由反馈客户端自动采集：它们被视为咨询性数据，
/// 越界只会被服务端丢弃，不会导致 400（玩家既没有输入也无法修正它们）。
/// </summary>
public record CreateFeedbackRequest(
    [property: Description("反馈类型：Bug / Suggestion / Other（忽略大小写）")]
    string? Type,
    [property: Description("标题，1-200 字符")]
    string? Title,
    [property: Description("反馈正文，1-10000 字符")]
    string? Content,
    [property: Description("游戏版本号，如 1.2.3")]
    string? GameVersion,
    [property: Description("构建号")]
    string? BuildNumber,
    [property: Description("操作系统，如 Windows 11 (build 22631)")]
    string? OperatingSystem,
    [property: Description("显卡型号，如 RTX 4070")]
    string? Gpu,
    [property: Description("CPU 型号（客户端自动采集，<=120 字符；超长会被丢弃而不是报错）")]
    string? Cpu,
    [property: Description("物理内存总量（MB，客户端自动采集；超出 1..4194304 会被丢弃而不是报错）")]
    int? MemoryTotalMb,
    [property: Description("语言区域，如 zh-CN")]
    string? Locale,
    [property: Description("地图/关卡名，如 arena_01")]
    string? Map,
    [property: Description("角色名")]
    string? Character);

public record CreateCommentRequest(
    [property: Description("评论内容，1-5000 字符")]
    string? Content);
