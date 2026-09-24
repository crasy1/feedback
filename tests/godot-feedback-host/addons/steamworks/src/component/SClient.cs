using System;
using Steamworks;

namespace Godot;

[Singleton]
public partial class SClient : SteamComponent
{
    [Signal]
    public delegate void SteamClientConnectedEventHandler();

    [Signal]
    public delegate void SteamClientDisconnectedEventHandler();

    public override void _Ready()
    {
        base._Ready();
        SteamClientConnected += () =>
        {
            SetProcess(true);
            Log.Info($"[steam] client 连接成功");
        };
        SteamClientDisconnected += () =>
        {
            SetProcess(false);
            Log.Info($"[steam] client 断开连接");
        };
        GameManager.AddBeforeGameQuitAction(this, Disconnect);
    }

    public void Connect()
    {
        var appId = SteamConfig.AppId;
        try
        {
            if (OS.IsDebugBuild())
            {
                SteamClient.Init(appId, asyncCallbacks: false);
                EmitSignalSteamClientConnected();
            }
            else
            {
                // 通过steam启动游戏
                if (!SteamClient.RestartAppIfNecessary(appId))
                {
                    SteamClient.Init(appId, asyncCallbacks: false);
                    EmitSignalSteamClientConnected();
                }
            }
        }
        catch (Exception e)
        {
            Log.Info($"[steam] client 初始化错误: {e.Message}");
            EmitSignalSteamClientDisconnected();
            var acceptDialog = new AcceptDialog();
            acceptDialog.Title = "退出游戏";
            acceptDialog.DialogText = "请先检查steam是否正常启动";
            acceptDialog.OkButtonText = "确定";
            acceptDialog.Size = Project.Display.ViewportSize / 4;
            this.GetAutoLoad<SteamManager>().AddChild(acceptDialog);
            acceptDialog.PopupCentered();
            acceptDialog.Show();
            acceptDialog.Confirmed += () => { GetTree().QuitNotification(); };
            acceptDialog.CloseRequested += () => { GetTree().QuitNotification(); };
        }
    }

    public void Disconnect()
    {
        SteamClient.Shutdown();
        EmitSignalSteamClientDisconnected();
        Log.Info($"[steam] 退出steam client");
    }

    public override void _Process(double delta)
    {
        try
        {
            if (SteamClient.IsValid)
            {
                SteamClient.RunCallbacks();
            }
        }
        catch (Exception e)
        {
            // ignored
        }
    }
}
