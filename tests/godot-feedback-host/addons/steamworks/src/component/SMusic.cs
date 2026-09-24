using System;
using Steamworks;

namespace Godot;

[Singleton]
public partial class SMusic : SteamComponent
{

    public override void _Ready()
    {
        base._Ready();
        SteamMusic.OnVolumeChanged += (volume) =>
        {
            Log.Info($"[steam] 音乐音量已改变为 {volume}");
        };
        SteamMusic.OnPlaybackChanged += () =>
        {
            Log.Info($"[steam] 音乐播放状态已改变");
        };
    }
}