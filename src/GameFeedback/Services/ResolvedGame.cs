namespace GameFeedback.Services;

/// <summary>
/// 一次请求解析出的 Game，连同它可用的 Steam 凭据。
/// <para>
/// 这是 <b>class 而不是 record</b>，且 <see cref="ToString"/> 刻意不含 <see cref="ApiKey"/>：
/// record 自动生成的 ToString 会把每个属性都打出来，任何一次
/// <c>logger.LogInformation("{Game}", game)</c> 都会把发行商密钥写进日志。
/// </para>
/// </summary>
public sealed class ResolvedGame
{
    public required int Id { get; init; }

    /// <summary>该游戏的 Steam AppID，也是请求路径里的那一段。解析成功时必然非空。</summary>
    public string? AppId { get; init; }

    public required string Name { get; init; }

    public required bool IsActive { get; init; }

    public required string Identity { get; init; }

    public string? ApiKey { get; init; }

    /// <summary>凭据存在但解不开（key ring 丢了）。与「没配凭据」要分开报，否则没法诊断。</summary>
    public bool CredentialUnreadable { get; init; }

    /// <summary>AppID 与可用凭据齐备；不齐备时该游戏的登录 fail closed。</summary>
    public bool IsFullyConfigured => !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrEmpty(ApiKey);

    public override string ToString() =>
        $"Game(Id={Id}, AppId={AppId}, IsActive={IsActive}, Configured={IsFullyConfigured})";
}
