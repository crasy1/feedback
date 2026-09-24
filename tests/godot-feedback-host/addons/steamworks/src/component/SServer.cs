using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Steamworks;
using Steamworks.Data;
using Steamworks.ServerList;

namespace Godot;

/// <summary>
/// https://partner.steamgames.com/doc/sdk/uploading/distributing_gs
/// 涉及专用服务器
/// </summary>
[Singleton]
public partial class SServer : SteamComponent
{
    public override void _Ready()
    {
        base._Ready();
        SteamServer.OnSteamNetAuthenticationStatus += (status) => { Log.Info($"[steam] 服务器网络验证 {status}"); };
        SteamServer.OnSteamServersConnected += () => { Log.Info($"[steam] 服务器连接成功"); };
        SteamServer.OnSteamServerConnectFailure += (result, s) => { Log.Info($"[steam] 服务器连接失败 {result},{s}"); };
        SteamServer.OnSteamServersDisconnected += (result) => { Log.Info($"[steam] 服务器断开连接 {result}"); };
        SteamServer.OnValidateAuthTicketResponse += (steamId1, steamId2, result) =>
        {
            Log.Info($"[steam] 服务器身份验证 {steamId1},{steamId2},{result}");
        };
        GameManager.AddBeforeGameQuitAction(this, StopServer);
    }

    public void StartServer(string modDir, string desc, string mapName = null)
    {
        var serverInit = new SteamServerInit(modDir, desc)
        {
            GamePort = 27015,
            QueryPort = 27016,
            Secure = true,
            DedicatedServer = true,
            VersionString = Project.Application.Version
        };
        try
        {
            SteamServer.Init(SteamConfig.AppId, serverInit, asyncCallbacks: false);
            Log.Info($"[steam] 启动steam服务器 {SteamServer.PublicIp} {SteamServer.Product}");
            SetProcess(true);
            SteamServer.MapName = mapName ?? Project.Application.GameName;
            SteamServer.ServerName = Project.Application.GameName;
            SteamServer.GameTags = $"{Project.Application.GameName},{Project.Application.Version}";
            SteamServer.LogOnAnonymous();
        }
        catch (Exception e)
        {
            Log.Error("创建steam服务器失败", e);
        }
    }

    /// <summary>
    /// 查找服务器
    /// https://wiki.facepunch.com/steamworks/Get_Server_List
    /// </summary>
    /// <param name="mapName"></param>
    /// <param name="serverType"></param>
    public async Task<List<ServerInfo>> ServerList(string mapName = null, ServerType serverType = ServerType.Internet)
    {
        using Base list = serverType switch
        {
            ServerType.Internet => new Internet(),
            ServerType.History => new History(),
            ServerType.Favourites => new Favourites(),
            ServerType.Friends => new Friends(),
            ServerType.LocalNetwork => new LocalNetwork(),
            _ => throw new ArgumentOutOfRangeException(nameof(serverType), serverType, null)
        };

        list.AddFilter("MAP", mapName);
        await list.RunQueryAsync();
        Log.Info($"[steam] ================服务器列表=================");
        foreach (var server in list.Responsive)
        {
            Log.Info($"{server.Address} {server.Name}");
        }

        return list.Responsive;
    }

    public void StopServer()
    {
        SetProcess(false);
        SteamServer.Shutdown();
        Log.Info($"[steam] 退出steam server");
    }

    public override void _Process(double delta)
    {
        try
        {
            if (SteamServer.IsValid)
            {
                SteamServer.RunCallbacks();
            }
        }
        catch (Exception e)
        {
            // ignored
        }
    }
}
