using System;
using Steamworks;

namespace Godot;

[Singleton]
public partial class SParental : SteamComponent
{
    public override void _Ready()
    {
        base._Ready();
        SteamParental.OnSettingsChanged += () =>
        {
            Log.Info($"[steam] steam 家庭设置变更");
        };
    }
    
}