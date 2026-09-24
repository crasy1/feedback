using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Steamworks;
using Steamworks.Data;

namespace Godot;

[Singleton]
public partial class SNetworking : SteamComponent
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
        _readBatch = ReadBatch;
        SteamNetworking.OnP2PSessionRequest += (steamId) =>
        {
            Log.Info($"[steam] p2p:收到 {steamId}连接请求");
            var success = SteamNetworking.AcceptP2PSessionWithUser(steamId);
            Log.Info($"[steam] p2p:接受 {steamId}连接请求: {success}");
            if (success && !ConnectedIds.Contains(steamId))
            {
                ConnectedIds.Add(steamId);
                EmitSignalUserConnected(steamId);
            }
        };
        SteamNetworking.OnP2PConnectionFailed += (steamId, error) =>
        {
            Log.Info($"[steam] p2p:与 {steamId} 连接失败,{error}");
            EmitSignalUserConnectFailed(steamId);
            if (ConnectedIds.Contains(steamId))
            {
                ConnectedIds.Remove(steamId);
            }
        };
        SClient.Instance.SteamClientConnected += () =>
        {
            SetProcess(true);
            // 添加队伍语音
            if (TeamVoice.Instance.GetParent() == null) AddChild(TeamVoice.Instance);
        };
        SClient.Instance.SteamClientDisconnected += () => { SetProcess(false); };
    }

    /// <summary>
    /// 与某人断开
    /// </summary>
    /// <param name="steamId"></param>
    /// <returns></returns>
    public bool Disconnect(SteamId steamId)
    {
        var result = SteamNetworking.CloseP2PSessionWithUser(steamId);
        if (result)
        {
            Log.Info($"[steam] p2p:与 {steamId} 断开连接");
            ConnectedIds.Remove(steamId);
            EmitSignalUserDisconnected(steamId);
        }

        return result;
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
            Log.Error("[steam] P2P 接收失败，等待重新连接", e);
        }
    }

    private int ReadBatch(int channel, int limit)
    {
        var count = 0;
        while (count < limit && SteamClient.IsValid && SteamNetworking.IsP2PPacketAvailable(channel))
        {
            var packet = SteamNetworking.ReadP2PPacket(channel);
            if (!packet.HasValue) break;
            count++;
            EmitSignalReceiveData(packet.Value.SteamId, channel, packet.Value.Data);
            if (!SteamClient.IsValid) break;
        }
        return count;
    }

    public static bool SendP2P(SteamId steamId, string content, Channel channel, SendType sendType = SendType.Reliable)
    {
        return SendP2P(steamId, Encoding.UTF8.GetBytes(content), channel, sendType);
    }

    public static bool SendP2P(SteamId steamId, byte[] data, Channel channel, SendType sendType = SendType.Reliable)
    {
        var p2PSend = sendType switch
        {
            SendType.Unreliable => P2PSend.Unreliable,
            SendType.NoNagle => P2PSend.Unreliable,
            SendType.NoDelay => P2PSend.UnreliableNoDelay,
            SendType.Reliable => P2PSend.Reliable,
            _ => P2PSend.Unreliable
        };
        return SteamNetworking.SendP2PPacket(steamId, data, data.Length, (int)channel, p2PSend);
    }
}
