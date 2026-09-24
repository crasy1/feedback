using System.Collections.Generic;
using Steamworks;
using Steamworks.Data;

namespace Godot;

[Singleton]
public partial class TeamVoice : Node
{
    [Signal]
    public delegate void MemberJoinEventHandler(ulong steamId);

    [Signal]
    public delegate void MemberLeaveEventHandler(ulong steamId);

    /// <summary>
    /// 队伍成员
    /// </summary>
    private readonly Dictionary<SteamId, IVoiceStreamPlayer> _teamMembers = new();


    public override void _Ready()
    {
        Name = GetType().Name;
        SetProcessMode(ProcessModeEnum.Always);
        SetProcess(false);
        SetPhysicsProcess(false);
        SNetworking.Instance.ReceiveData += OnReceiveVoice;
        SUser.Instance.RecordVoiceData += OnRecordVoiceData;
    }

    public override void _ExitTree()
    {
        SNetworking.Instance.ReceiveData -= OnReceiveVoice;
        SUser.Instance.RecordVoiceData -= OnRecordVoiceData;
        _teamMembers.Clear();
    }
    

    /// <summary>
    /// 获取该用户的播放器
    /// </summary>
    /// <param name="steamId"></param>
    /// <returns></returns>
    public IVoiceStreamPlayer? GetTeamMember(SteamId steamId)
    {
        return _teamMembers.GetValueOrDefault(steamId);
    }

    /// <summary>
    /// 取消用户静音
    /// </summary>
    /// <param name="steamId"></param>
    public void Play(SteamId steamId)
    {
        if (!IsPlaying(steamId))
        {
            GetTeamMember(steamId)?.Play();
            Log.Info($"队伍语音取消静音 {steamId}");
        }
    }

    /// <summary>
    /// 静音用户
    /// </summary>
    /// <param name="steamId"></param>
    public void Mute(SteamId steamId)
    {
        if (IsPlaying(steamId))
        {
            GetTeamMember(steamId)?.Stop();
            Log.Info($"队伍语音静音 {steamId}");
        }
    }

    /// <summary>
    /// 用户是否在播放
    /// </summary>
    /// <param name="steamId"></param>
    /// <returns></returns>
    public bool IsPlaying(SteamId steamId)
    {
        return GetTeamMember(steamId)?.IsPlaying() ?? false;
    }

    /// <summary>
    /// 移除成员和播放器
    /// </summary>
    /// <param name="steamId"></param>
    public void RemoveTeamMember(SteamId steamId)
    {
        if (!_teamMembers.ContainsKey(steamId))
        {
            return;
        }

        GetTeamMember(steamId)?.Exit();
        _teamMembers.Remove(steamId);
        EmitSignalMemberLeave(steamId);
        Log.Info($"队伍语音移除 {steamId}");
    }

    /// <summary>
    /// 移除所有成员和播放器
    /// </summary>
    /// <param name="steamId"></param>
    public void RemoveAllTeamMember()
    {
        foreach (var steamId in _teamMembers.Keys)
        {
            GetTeamMember(steamId)?.Exit();
            _teamMembers.Remove(steamId);
            EmitSignalMemberLeave(steamId);
        }

        Log.Info($"退出队伍语音");
    }

    /// <summary>
    /// 添加成员和播放器
    /// </summary>
    /// <param name="steamId"></param>
    public void AddTeamMember(SteamId steamId)
    {
        if (steamId == SteamClient.SteamId || _teamMembers.ContainsKey(steamId))
        {
            return;
        }

        var voiceStreamPlayer = new VoiceStreamPlayer();
        voiceStreamPlayer.Name = steamId.ToString();
        AddChild(voiceStreamPlayer);
        _teamMembers.TryAdd(steamId, voiceStreamPlayer);
        // 默认播放队伍语音
        voiceStreamPlayer.Play();
        EmitSignalMemberJoin(steamId);
        Log.Info($"队伍语音添加 {steamId}");
    }


    // 录音并发送给所有玩家
    private void OnRecordVoiceData(ulong steamId, byte[] compressData)
    {
        foreach (var memberId in _teamMembers.Keys)
        {
            SNetworking.SendP2P(memberId, compressData, Channel.Voice,SendType.NoDelay);
        }
    }


    // 接收其他玩家录音,根据id使用不同的播放器播放
    private void OnReceiveVoice(ulong steamId, int channel, byte[] data)
    {
        if (channel == (int)Channel.Voice && _teamMembers.TryGetValue(steamId, out var voiceStreamPlayer))
        {
            voiceStreamPlayer.ReceiveRecordVoiceData(steamId, data);
        }
    }
}
