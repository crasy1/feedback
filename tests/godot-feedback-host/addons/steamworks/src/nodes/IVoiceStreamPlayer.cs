namespace Godot;

/// <summary>
/// 队伍语音接口
/// </summary>
public interface IVoiceStreamPlayer
{
    void SetStream(AudioStream stream);
    AudioStreamPlayback GetStreamPlayback();
    void Play(float fromPosition = 0.0f);
    void Stop();
    void SetBus(StringName bus);
    bool IsPlaying();
    void ReceiveRecordVoiceData(ulong steamId, byte[] data);
    void Exit();
}