using System;
using System.Linq;
using Steamworks;
using Steamworks.Data;

namespace Godot;

/// <summary>
/// 通过friend steamId 连接，走steam中继网络，延迟较高
/// </summary>
public partial class RelayServer : SteamSocket
{
    public bool Started { private set; get; }
    public SocketManager? SocketManager { set; get; }
    public ProtoSocket? ISocketManager { set; get; }
    private int Port { set; get; }

    public RelayServer(int port)
    {
        Port = port;
        SocketName = $"[{nameof(RelayServer)}] {Port}";
    }

    public override void _Ready()
    {
        base._Ready();
    }

    public override void _Process(double delta)
    {
        SocketManager?.Receive();
    }

    public void Create()
    {
        try
        {
            ISocketManager = new(this);
            SocketManager = SteamNetworkingSockets.CreateRelaySocket(Port, ISocketManager);
            SetProcess(true);
            Log.Info($"{SocketName} => 创建");
            Started = true;
        }
        catch (Exception e)
        {
            Log.Error($"{SocketName} => 创建异常, {e.Message}");
            Started = false;
        }
    }

    public override void Send(ProtoBufMsg msg, SendType sendType = SendType.Reliable)
    {
        if (SocketManager != null && Started)
        {
            foreach (var connection in SocketManager.Connected)
            {
                var result = connection.SendMsg(msg, sendType);
                Log.Info($"{SocketName} => 向 {connection.Id} 发送消息 {msg.Type} {result}");
            }
        }
    }

    public void Send(SteamId steamId, ProtoBufMsg msg, SendType sendType = SendType.Reliable)
    {
        if (SocketManager != null && Started && ISocketManager != null)
        {
            var connection = ISocketManager.Connections.FirstOrDefault(kv => steamId == kv.Value.Id).Key;
            var result = connection.SendMsg(msg, sendType);
            Log.Info($"{SocketName} => 向 {connection.Id} 发送消息 {msg.Type} {result}");
        }
    }

    public override void Close()
    {
        SocketManager?.Close();
        SetProcess(false);
        SocketManager = null;
        ISocketManager = null;
        Log.Info($"{SocketName} => 关闭");
        Started = false;
        this.RemoveAndQueueFree();
    }
}