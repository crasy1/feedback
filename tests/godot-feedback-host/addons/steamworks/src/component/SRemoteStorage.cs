using System;
using System.Collections.Generic;
using System.Linq;
using Steamworks;

namespace Godot;

[Singleton]
public partial class SRemoteStorage : SteamComponent
{
    public override void _Ready()
    {
        base._Ready();
    }

    public void GetInfo()
    {
        Log.Info(@$"[steam] 
----    {nameof(SteamRemoteStorage)}    ----
QuotaBytes:                 {SteamRemoteStorage.QuotaBytes}
QuotaUsedBytes:             {SteamRemoteStorage.QuotaUsedBytes}
QuotaRemainingBytes:        {SteamRemoteStorage.QuotaRemainingBytes}
FileCount:                  {SteamRemoteStorage.FileCount}
Files:                      {string.Join(",", SteamRemoteStorage.Files)}
----    {nameof(SteamRemoteStorage)}    ----
");
    }

    public List<string> GetFileList()
    {
        var files = SteamRemoteStorage.Files.ToList();
        foreach (var file in files)
        {
            Log.Info($"[steam] 云文件 {file}: 最近修改时间 {FileTime(file)},大小 {FileSize(file)} b");
        }

        return files;
    }

    public string? ReadString(string filename)
    {
        if (FileExists(filename))
        {
            var content = SteamRemoteStorage.FileRead(filename);
            return System.Text.Encoding.UTF8.GetString(content);
        }

        return null;
    }

    public byte[]? ReadBytes(string filename)
    {
        if (FileExists(filename))
        {
            return SteamRemoteStorage.FileRead(filename);
        }

        return null;
    }

    public bool Write(string filename, byte[] bytes)
    {
        var result = SteamRemoteStorage.FileWrite(filename, bytes);
        Log.Info($"[steam] 上传文件 {filename} 到云 {result}");
        return result;
    }

    public bool Write(string filename, string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return Write(filename, bytes);
    }

    public bool FileExists(string filename)
    {
        var result = SteamRemoteStorage.FileExists(filename);
        Log.Info($"[steam] {(result ? "读取" : "不存在")}云文件 {filename}");
        return result;
    }

    public bool FileDelete(string filename)
    {
        var result = SteamRemoteStorage.FileDelete(filename);
        Log.Info($"[steam] 删除云文件 {filename} {result}");
        return result;
    }

    public DateTime FileTime(string filename)
    {
        return SteamRemoteStorage.FileTime(filename).ToLocalTime();
    }

    /// <summary>
    /// size or 0
    /// </summary>
    /// <param name="filename"></param>
    /// <returns></returns>
    public int FileSize(string filename)
    {
        return SteamRemoteStorage.FileSize(filename);
    }
}