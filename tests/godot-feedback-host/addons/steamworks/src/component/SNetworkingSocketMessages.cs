using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Steamworks;
using Steamworks.Data;

namespace Godot;

[Singleton]
public partial class SNetworkingSocketMessages : SteamComponent
{
    private SteamReceivePump _receivePump = null!;
    private Func<int, int, int> _readBatch = null!;

    internal void EnsureMultiplayerChannels(int count)
    {
        for (var channel = SteamPeerProtocol.FirstDataChannel; channel < SteamPeerProtocol.LaneCount(count); channel++)
            if (!Channels.Contains(channel)) Channels.Add(channel);
    }
    [Signal]
    public delegate void ReceiveDataEventHandler(ulong steamId, int channel, byte[] data);

    [Signal]
    public delegate void UserConnectedEventHandler(ulong steamId);

    [Signal]
    public delegate void UserConnectFailedEventHandler(ulong steamId);

    [Signal]
    public delegate void UserDisconnectedEventHandler(ulong steamId);

    /// <summary>
    /// 已连接的玩家id
    /// </summary>
    public readonly List<SteamId> ConnectedIds = new();

    public static readonly List<int> Channels = Enum.GetValues(typeof(Channel))
        .Cast<Channel>()
        .Select(c => (int)c)
        .ToList();

    public override void _Ready()
    {
        base._Ready();
        var options = SteamConfig.NetworkOptions;
        options.Validate();
        _receivePump = new SteamReceivePump(options);
        _readBatch = (channel, count) => SteamClient.IsValid
            ? SteamNetworkingMessages.Receive(channel, count, receiveToEnd: false) : 0;
        SteamNetworkingMessages.OnSessionRequest += (identity) =>
        {
            var steamId = identity.SteamId;
            var success = SteamNetworkingMessages.AcceptSessionWithUser(ref identity);
            Log.Info($"[steam] p2p msg:接受 {steamId}连接请求: {success}");
            if (success && !ConnectedIds.Contains(steamId))
            {
                ConnectedIds.Add(steamId);
                EmitSignalUserConnected(steamId);
            }
        };
        SteamNetworkingMessages.OnSessionFailed += (connectionInfo) =>
        {
            var steamId = connectionInfo.Identity.SteamId;
            Log.Info($"[steam] p2p msg:与 {steamId} 连接失败");
            EmitSignalUserConnectFailed(steamId);
            ConnectedIds.Remove(steamId);
        };
        SteamNetworkingMessages.OnMessage += (identity, data, size, channel) =>
        {
            unsafe
            {
                var span = new Span<byte>((byte*)data.ToPointer(), size);
                EmitSignalReceiveData(identity.SteamId, channel, span.ToArray());
            }
        };
        SClient.Instance.SteamClientConnected += () => { SetProcess(true); };
        SClient.Instance.SteamClientDisconnected += () => { SetProcess(false); };
    }

    public override void _Process(double delta)
    {
        try
        {
            if (SteamClient.IsValid) _receivePump.Poll(Channels, _readBatch);
        }
        catch (Exception e)
        {
            SetProcess(false);
            Log.Error("[steam] NetworkingMessages 接收失败，等待重新连接", e);
        }
    }

    /// <summary>
    /// 与某人断开
    /// </summary>
    /// <param name="steamId"></param>
    /// <returns></returns>
    public bool Disconnect(SteamId steamId)
    {
        var netIdentity = (NetIdentity)steamId;
        var result = SteamNetworkingMessages.CloseSessionWithUser(ref netIdentity);
        if (result)
        {
            Log.Info($"[steam] p2p msg:与 {steamId} 断开连接");
            if (ConnectedIds.Remove(steamId))
            {
                EmitSignalUserDisconnected(steamId);
            }
        }

        return result;
    }

    public static bool SendP2P(SteamId steamId, string content, Channel channel, SendType sendType = SendType.Reliable)
    {
        return SendP2P(steamId, Encoding.UTF8.GetBytes(content), channel, sendType);
    }

    public static bool SendP2P(NetIdentity steamId, byte[] data, Channel channel, SendType sendType = SendType.Reliable)
    {
        return SteamNetworkingMessages.SendMessageToUser(ref steamId, data, data.Length, (int)channel, sendType) ==
               Result.OK;
    }
}
