using System;
using Steamworks;

namespace Godot;

/// <summary>
/// https://wiki.facepunch.com/steamworks/Creating_Workshop_Items
/// </summary>
[Singleton]
public partial class SUgc : SteamComponent
{

    public override void _Ready()
    {
        base._Ready();
        SteamUGC.OnItemSubscribed += (appId, publishFileId) =>
        {
            Log.Info($"[steam] 订阅成功，appId: {appId}, publishFileId: {publishFileId}");
        };
        SteamUGC.OnItemUnsubscribed += (appId, publishFileId) =>
        {
            Log.Info($"[steam] 取消订阅，appId: {appId}, publishFileId: {publishFileId}");
        };
        SteamUGC.OnItemInstalled += (appId, publishFileId) =>
        {
            Log.Info($"[steam] 安装成功，appId: {appId}, publishFileId: {publishFileId}");
        };
        SteamUGC.OnDownloadItemResult += (result) =>
        {
            Log.Info($"[steam] 下载结果 {result}");
        };
    }
    
}