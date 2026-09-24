using Steamworks;
using Steamworks.Data;

namespace Godot;

/// <summary>保留 Legacy P2P 和 NetworkingMessages 后端，握手及数据语义统一由 SteamPeer 处理。</summary>
public partial class SteamP2PPeer : SteamPeer
{
    private SNetworkingSocketMessages? _messages;
    private SNetworking? _legacy;

    private SteamP2PPeer(int peerId, SteamSocketType socketType, SteamNetworkOptions? options = null,
        SteamId expectedServer = default) : base(peerId, socketType, options, expectedServer) { }

    public static SteamP2PPeer CreateServer(SteamSocketType socketType = SteamSocketType.P2PMessage,
        SteamNetworkOptions? options = null)
    {
        ValidateType(socketType);
        var peer = new SteamP2PPeer(ServerPeerId, socketType, options);
        peer.Activate();
        return peer;
    }

    public static SteamP2PPeer CreateClient(SteamId steamId, SteamSocketType socketType = SteamSocketType.P2PMessage,
        SteamNetworkOptions? options = null)
    {
        ValidateType(socketType);
        if (steamId == 0) throw new System.ArgumentOutOfRangeException(nameof(steamId));
        var peer = new SteamP2PPeer(0, socketType, options, steamId);
        peer.Activate();
        peer.OnSocketConnected(steamId);
        return peer;
    }

    private static void ValidateType(SteamSocketType type)
    {
        if (type is not (SteamSocketType.P2P or SteamSocketType.P2PMessage))
            throw new System.ArgumentOutOfRangeException(nameof(type));
    }

    protected override void OnCreate()
    {
        if (SteamSocketType == SteamSocketType.P2PMessage)
        {
            _messages = SNetworkingSocketMessages.Instance;
            _messages.EnsureMultiplayerChannels(Options.ChannelCount);
            _messages.ReceiveData += ReceiveData;
            _messages.UserConnectFailed += OnSocketDisconnected;
        }
        else
        {
            _legacy = SNetworking.Instance;
            _legacy.EnsureMultiplayerChannels(Options.ChannelCount);
            _legacy.ReceiveData += ReceiveData;
            _legacy.UserConnectFailed += OnSocketDisconnected;
        }
    }

    protected override void OnClose()
    {
        if (_messages is not null && IsInstanceValid(_messages))
        {
            _messages.ReceiveData -= ReceiveData;
            _messages.UserConnectFailed -= OnSocketDisconnected;
        }
        if (_legacy is not null && IsInstanceValid(_legacy))
        {
            _legacy.ReceiveData -= ReceiveData;
            _legacy.UserConnectFailed -= OnSocketDisconnected;
        }
    }

    protected override bool SendMsg(SteamId steamId, byte[] data, Channel channel = Channel.Msg,
        SendType sendType = SendType.Reliable)
    {
        if (!SteamClient.IsValid) return false;
        return SteamSocketType == SteamSocketType.P2PMessage
            ? SNetworkingSocketMessages.SendP2P(steamId, data, channel, sendType)
            : SNetworking.SendP2P(steamId, data, channel, sendType);
    }

    protected override void DisconnectTransport(SteamId steamId, bool force)
    {
        // 不使用延迟关闭：旧会话的计时任务不能关闭同 SteamId 的新会话。
        if (!SteamClient.IsValid) return;
        if (SteamSocketType == SteamSocketType.P2PMessage) _messages?.Disconnect(steamId);
        else _legacy?.Disconnect(steamId);
    }

    protected override void Receive() { } // 全局组件负责有预算的 native 轮询。
}
