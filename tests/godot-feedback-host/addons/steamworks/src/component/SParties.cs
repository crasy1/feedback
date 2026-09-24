using System;
using Steamworks;

namespace Godot;

[Singleton]
public partial class SParties : SteamComponent
{
    public override void _Ready()
    {
        base._Ready();
        SteamParties.OnActiveBeaconsUpdated += () =>
        {
            Log.Info($"[steam] steam party 活动信标列表可能已更改");
        };
        SteamParties.OnBeaconLocationsUpdated += () =>
        {
            Log.Info($"[steam] steam party 信标位置列表可能发生更改");
        };
    }
}