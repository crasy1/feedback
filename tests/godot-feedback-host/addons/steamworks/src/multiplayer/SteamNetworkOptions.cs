using System;

namespace Godot;

/// <summary>一次网络会话使用的配置快照，避免每帧读取 Resource。</summary>
public sealed record SteamNetworkOptions
{
    public static SteamNetworkOptions Defaults { get; } = new();
    public int ChannelCount { get; init; } = 4;
    public int ReceivePacketsPerFrame { get; init; } = 128;
    public int ReceiveBatchSize { get; init; } = 16;
    public double ReceiveBudgetMilliseconds { get; init; } = 2;
    public int MaxQueuedPackets { get; init; } = 1024;
    public int MaxQueuedBytes { get; init; } = 8 * 1024 * 1024;
    public double HandshakeTimeoutSeconds { get; init; } = 10;
    public int MaxPendingConnections { get; init; } = 32;
    public int MaxConnections { get; init; } = 32;

    public void Validate()
    {
        if (ChannelCount is < 1 or > 128 || ReceivePacketsPerFrame < 1 || ReceiveBatchSize < 1 ||
            !double.IsFinite(ReceiveBudgetMilliseconds) || ReceiveBudgetMilliseconds <= 0 ||
            MaxQueuedPackets < 1 || MaxQueuedBytes < 1 || MaxPendingConnections < 1 || MaxConnections < 1 ||
            !double.IsFinite(HandshakeTimeoutSeconds) || HandshakeTimeoutSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SteamNetworkOptions), "Steam 网络预算或通道配置无效。");
        }
    }
}
