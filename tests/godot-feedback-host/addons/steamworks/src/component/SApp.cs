using System;
using Steamworks;

namespace Godot;

/// <summary>
/// https://partner.steamgames.com/doc/store/localization/languages
/// </summary>
[Singleton]
public partial class SApp : SteamComponent
{

    public override void _Ready()
    {
        base._Ready();
        SteamApps.OnDlcInstalled += (appId) => { Log.Info($"[steam] {appId} DLC已安装"); };
        SteamApps.OnNewLaunchParameters += () =>
        {
            Log.Info($"[steam] 启动命令:  {SteamApps.CommandLine}");
            // connect_lobby
        };
    }

    public void AppInfo()
    {
        Log.Info(@$"[steam] 
AppId:  {SteamConfig.AppId}
AvailableLanguages: {SteamApps.AvailableLanguages}
GameLanguage:  {SteamApps.GameLanguage}
Owner:  {SteamApps.AppOwner}
AppInstallDir:  {SteamApps.AppInstallDir()}
                 ");
    }
}