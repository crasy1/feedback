using System;
using Steamworks;

namespace Godot;

[Singleton]
public partial class SInput:SteamComponent
{

    public override void _Ready()
    {
        base._Ready();
        SClient.Instance.SteamClientConnected += () =>
        {
            var controllers = SteamInput.Controllers;
            foreach (var controller in controllers)
            {
                var inputType = controller.InputType;
                Log.Info($"[steam] 已连接设备: {inputType}");
            }
        };
    }
}