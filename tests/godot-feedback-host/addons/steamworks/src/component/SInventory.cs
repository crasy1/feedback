using System;
using Steamworks;

namespace Godot;

/// <summary>
/// https://wiki.facepunch.com/steamworks/Item_Store_Cart
/// </summary>
[Singleton]
public partial class SInventory: SteamComponent
{
    public override void _Ready()
    {
        base._Ready();
        SteamInventory.OnInventoryUpdated+= (result) =>
        {
            Log.Info($"[steam] 库存更新 {result}");
        };
        SteamInventory.OnDefinitionsUpdated+= () =>
        {
            Log.Info($"[steam] 定义更新");
        };
    }
}