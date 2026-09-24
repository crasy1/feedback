using System;
using Steamworks;

namespace Godot;

[Singleton]
public partial class SUtil : SteamComponent
{
    public override void _Ready()
    {
        base._Ready();
        SteamUtils.OnGamepadTextInputDismissed += (submitted) =>
        {
            Log.Info($"[steam] 游戏手柄文本输入 {(submitted ? "已提交" : "已取消")}");
        };
        SteamUtils.OnIpCountryChanged += () => { Log.Info($"[steam] ip地址改变，当前ip地址为 {SteamUtils.IpCountry}"); };
        SteamUtils.OnLowBatteryPower += (minutesLeft) => { Log.Info($"[steam] 电量低，还剩 {minutesLeft} 分钟"); };
        SteamUtils.OnSteamShutdown += () => { Log.Info($"[steam] 软件 steam 关闭"); };
    }

    public void GetInfo()
    {
        Log.Info($@"[steam] 
----    {nameof(SteamUtils)}    ----
CurrentBatteryPower:        {SteamUtils.CurrentBatteryPower}
IpCountry:                  {SteamUtils.IpCountry}
IsSteamChinaLauncher:       {SteamUtils.IsSteamChinaLauncher}
SecondsSinceAppActive:      {SteamUtils.SecondsSinceAppActive}
SecondsSinceComputerActive: {SteamUtils.SecondsSinceComputerActive}
SteamUILanguage:            {SteamUtils.SteamUILanguage}
SteamServerTime:            {SteamUtils.SteamServerTime}
----    {nameof(SteamUtils)}    ----
");
    }
}