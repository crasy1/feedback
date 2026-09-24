using System;
using System.Linq;
using Steamworks;
using Steamworks.Data;

namespace Godot;

/// <summary>
/// 通过friend steamId 连接，走steam中继网络，延迟较高
/// </summary>
public partial class RelayClient : SteamSocket
{
    public ConnectionManager? ConnectionManager { set; get; }
    public ProtoConnection? IConnectionManager { set; get; }
    private int Port { set; get; }
    private SteamId ServerId { set; get; }

    public RelayClient(SteamId serverId, int port)
    {
        Port = port;
        ServerId = serverId;
        SocketName = $"[{nameof(RelayClient)}] {serverId}";
    }

    public override void _Ready()
    {
        base._Ready();
        Disconnected += (id) => this.RemoveAndQueueFree();
    }

    public override void _Process(double delta)
    {
        ConnectionManager?.Receive();
    }

    public void Connect()
    {
        try
        {
            IConnectionManager = new(this);
            ConnectionManager = SteamNetworkingSockets.ConnectRelay(ServerId, Port, IConnectionManager);
            Log.Info($"{SocketName} => 创建");
        }
        catch (Exception e)
        {
            Log.Error($"{SocketName} => 创建异常, {e.Message}");
        }
    }

    public override void Send(ProtoBufMsg msg, SendType sendType = SendType.Reliable)
    {
        if (ConnectionManager is { Connected: true })
        {
            var result = ConnectionManager.Connection.SendMsg(msg, sendType);
            Log.Info($"{SocketName} => 向 {ConnectionManager.ConnectionInfo.Identity} 发送消息 {msg.Type} {result}");
        }
    }

    public override void Close()
    {
        ConnectionManager?.Close();
        ConnectionManager = null;
        IConnectionManager = null;
        Log.Info($"{SocketName} => 关闭");
    }
}