using System;
using Steamworks;

namespace Godot;

[Singleton]
public partial class SScreenshots : SteamComponent
{

    public override void _Ready()
    {
        base._Ready();
        SteamScreenshots.OnScreenshotReady += (screenshot) => { Log.Info($"[steam] 准备截图 {screenshot}"); };
        SteamScreenshots.OnScreenshotFailed += (result) => { Log.Info($"[steam] 截图失败{result}"); };
        SteamScreenshots.OnScreenshotRequested += () => { Log.Info($"[steam] 请求截图"); };
        SClient.Instance.SteamClientConnected+=()=>
        {
            SteamScreenshots.Hooked = true;
        };
    }
}