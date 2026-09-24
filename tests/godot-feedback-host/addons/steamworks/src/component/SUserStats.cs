using System;
using Steamworks;
using Steamworks.Data;

namespace Godot;

[Singleton]
public partial class SUserStats : SteamComponent
{
   
    public static event Action<Achievement> AchievementUnlocked;

    public override void _Ready()
    {
        base._Ready();
        SteamUserStats.OnAchievementProgress += (achievement, current, max) =>
        {
            if (current == 0 && max == 0)
            {
                Log.Info($"[steam] 成就 {achievement} 已解锁");
                AchievementUnlocked?.Invoke(achievement);
            }
            else
            {
                Log.Info($"[steam] 成就 {achievement} 进度： {current}/{max}");
            }
        };
        SteamUserStats.OnUserStatsStored += (result) => { Log.Info($"[steam] 保存用户统计信息 {result}"); };
        SteamUserStats.OnUserStatsReceived += (steamId, result) => { Log.Info($"[steam] 收到用户 {steamId} 统计信息 {result}"); };
        SteamUserStats.OnUserStatsUnloaded += (steamId) => { Log.Info($"[steam] 用户 {steamId} 统计信息已卸载"); };
    }
}