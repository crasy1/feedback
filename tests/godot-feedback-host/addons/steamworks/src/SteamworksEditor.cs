using System;
using Serilog.Events;

namespace Godot;

[Tool]
[SceneTree]
[Instantiable]
public partial class SteamworksEditor : Control
{
    
    public override void _Ready()
    {
        SteamUtil.InitEnvironment(SteamConfig.AppId);
        SetEnvButton.Pressed += () => { SteamUtil.InitEnvironment(SteamConfig.AppId); };
        UserDataButton.Pressed += () => { OS.ShellOpen(OS.GetUserDataDir()); };
        AppIdSpinBox.ValueChanged += (value) => { SteamConfig.AppId = (uint)value; };
        AppIdSpinBox.Value = SteamConfig.AppId;
        DebugUI.Toggled += (value) => { SteamConfig.Debug = value; };
        DebugUI.ButtonPressed = SteamConfig.Debug;
        AsServer.Toggled += (value) => { SteamConfig.AsServer = value; };
        AsServer.ButtonPressed = SteamConfig.AsServer;
        SampleRate.ItemSelected += (value) =>
        {
            SteamConfig.SampleRate = (SampleRate)SampleRate.GetItemText((int)value).ToInt();
        };
        for (int i = 0; i < SampleRate.GetItemCount(); i++)
        {
            if ((SampleRate)SampleRate.GetItemText(i).ToInt() == SteamConfig.SampleRate)
            {
                SampleRate.Selected = i;
            }
        }

        FileLogLevel.ItemSelected += (value) =>
        {
            SteamConfig.FileLogLevel = (LogEventLevel)FileLogLevel.GetItemId((int)value);
        };
        for (int i = 0; i < FileLogLevel.GetItemCount(); i++)
        {
            if ((LogEventLevel)FileLogLevel.GetItemId(i) == SteamConfig.FileLogLevel)
            {
                FileLogLevel.Selected = i;
            }
        }

        GdLogLevel.ItemSelected += (value) =>
        {
            SteamConfig.GdLogLevel = (LogEventLevel)GdLogLevel.GetItemId((int)value);
        };
        for (int i = 0; i < GdLogLevel.GetItemCount(); i++)
        {
            if ((LogEventLevel)GdLogLevel.GetItemId(i) == SteamConfig.GdLogLevel)
            {
                GdLogLevel.Selected = i;
            }
        }

        CustomBusLayout.Toggled += (value) =>
        {
            SteamConfig.CustomBusLayout = value;
            Project.Audio.BusLayout = value ? Const.Paths.CustomBusLayoutPath : Consts.DefaultBusLayoutPath;
        };
        CustomBusLayout.ButtonPressed = SteamConfig.CustomBusLayout;
        CustomBusLayout.EmitSignal(BaseButton.SignalName.Toggled, SteamConfig.CustomBusLayout);

       
        SplashButton.Toggled += (value) =>
        {
            SteamConfig.SplashEnabled = value;
            if (value)
            {
                Project.Application.ProjectSettingsOverride = Const.Paths.RuntimeOverrideCfgPath;
            }
            else
            {
                Project.Application.ProjectSettingsOverride = "";
            }

            ProjectSettings.Save();
        };

        SplashButton.ButtonPressed = SteamConfig.SplashEnabled;
        SplashButton.EmitSignal(BaseButton.SignalName.Toggled, SteamConfig.SplashEnabled);
    }

  

    public override void _ExitTree()
    {
    }
}