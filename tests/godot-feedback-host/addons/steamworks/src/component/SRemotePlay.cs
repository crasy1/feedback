using System;
using Steamworks;

namespace Godot;

[Singleton]
public partial class SRemotePlay : SteamComponent
{

    public override void _Ready()
    {
        base._Ready();
        SteamRemotePlay.OnSessionConnected += session => {Log.Info($"[steam] 远程同乐 已连接 {session}"); };
        SteamRemotePlay.OnSessionDisconnected += session => { Log.Info($"[steam] 远程同乐 断开连接 {session}"); };
    }

    public static void Invite(SteamId steamId)
    {
        SteamRemotePlay.SendInvite(steamId);
    }
}