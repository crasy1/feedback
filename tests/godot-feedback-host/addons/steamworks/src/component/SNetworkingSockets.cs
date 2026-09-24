using System;
using System.Threading;
using Steamworks;
using Steamworks.Data;

namespace Godot;

[Singleton]
public partial class SNetworkingSockets : SteamComponent
{
    public override void _Ready()
    {
        base._Ready();
        // if (SteamConfig.Debug)
        // {
        //     SteamNetworkingSockets.OnConnectionStatusChanged += (c, ci) =>
        //     {
        //         Log.Info($"连接状态改变 {ci.Identity},{ci.Address},{ci.State},{ci.EndReason}");
        //     };
        // }
        ProtoBufUtil.Init();
        SteamNetworkingSockets.OnFakeIPResult += (NetAddress na) => { Log.Info($"[steam] fake ip 地址 {na}"); };
        SClient.Instance.SteamClientConnected += () => { SteamNetworkingUtils.InitRelayNetworkAccess(); };
        GameManager.AddBeforeGameQuitAction(this, CloseAllSocket);
    }

    private void CloseAllSocket()
    {
        if (GetChildCount() <= 0)
        {
            Log.Info("---------------- 未检测到[steam socket] ----------------");
            return;
        }

        Log.Info("---------------- 关闭[steam socket]开始 ----------------");
        foreach (var child in GetChildren())
        {
            if (child is SteamSocket socket)
            {
                if (IsInstanceValid(child))
                {
                    socket.Close();
                }
            }
        }

        Log.Info("---------------- 关闭[steam socket]结束 ----------------");
    }

    public static NormalServer CreateNormal(ushort port)
    {
        var normalServer = new NormalServer(10000);
        Instance.AddChild(normalServer);
        return normalServer;
    }

    public static NormalClient ConnectNormal(string host, ushort port)
    {
        var normalClient = new NormalClient(host, port);
        Instance.AddChild(normalClient);
        return normalClient;
    }

    public static RelayServer CreateRelay(int port)
    {
        var relayServer = new RelayServer(port);
        Instance.AddChild(relayServer);
        return relayServer;
    }

    public static RelayClient ConnectRelay(SteamId serverId, int port)
    {
        var relayClient = new RelayClient(serverId, port);
        Instance.AddChild(relayClient);
        return relayClient;
    }
}