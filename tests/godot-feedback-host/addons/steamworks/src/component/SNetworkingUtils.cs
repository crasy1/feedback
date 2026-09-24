using System;
using System.Threading.Tasks;
using Steamworks;
using Steamworks.Data;

namespace Godot;

[Singleton]
public partial class SNetworkingUtils : SteamComponent
{
    public override void _Ready()
    {
        base._Ready();
        SteamNetworkingUtils.OnDebugOutput += (output, s) => { Log.Info($"[steam] 接收调试网络信息 {output},{s}"); };
        // SClient.Instance.SteamClientConnected += () =>
        // {
        //     SteamNetworkingUtils.InitRelayNetworkAccess();
        // };
    }
    
}