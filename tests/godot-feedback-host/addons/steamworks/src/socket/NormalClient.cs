using System;
using Steamworks;
using Steamworks.Data;

namespace Godot;

/// <summary>
/// 和普通socket一样通过host ip连接
/// </summary>
public partial class NormalClient : SteamSocket
{
    public ConnectionManager? ConnectionManager { set; get; }
    public ProtoConnection? IConnectionManager { set; get; }
    private string Host { set; get; }
    private ushort Port { set; get; }
    private NetAddress NetAddress { set; get; }

    public NormalClient(string host, ushort port)
    {
        Host = host;
        Port = port;
        if (string.IsNullOrWhiteSpace(host))
        {
            NetAddress = NetAddress.LocalHost(Port);
        }
        else
        {
            NetAddress = NetAddress.From(Host, Port);
        }

        SocketName = $"[{nameof(NormalClient)}] {NetAddress}";
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
            ConnectionManager = SteamNetworkingSockets.ConnectNormal(NetAddress, IConnectionManager);
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
            Log.Debug($"{SocketName} => 向 {ConnectionManager.ConnectionInfo.Identity} 发送消息 {msg.Type} {result}");
        }
    }

    public override void Close()
    {
        ConnectionManager?.Close();
        ConnectionManager = null;
        IConnectionManager = null;
        SetProcess(false);
        Log.Info($"{SocketName} => 关闭");
    }
}