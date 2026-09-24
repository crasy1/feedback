using System;
using System.Threading.Tasks;
using Steamworks;
using Steamworks.Data;

namespace Godot;

/// <summary>
/// https://wiki.facepunch.com/steamworks/Leaderboards
/// </summary>
[Singleton]
public partial class SServerStats : SteamComponent
{
    public override void _Ready()
    {
        base._Ready();
    }
}