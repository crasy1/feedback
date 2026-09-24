using System;
using NAudio.Wave;

namespace Godot;

/// <summary>
/// 使用NAudio组件实时播放steamworks音频流
/// </summary>
public partial class NVoiceStreamPlayer : Node
{
    /// <summary>
    /// 采样率 需要与音频流一致，否则音色会失真
    /// </summary>
    [Export(PropertyHint.Enum, "11025,22050,24000,32000,44100,48000")]
    public int SampleRate = 44100;

    /// <summary>
    /// 音量 分贝
    /// </summary>
    [Export(PropertyHint.Range, "-60,0,1")]
    private float _volumeDb = 1;

    public float VolumeDb
    {
        set
        {
            _volumeDb = Mathf.Clamp(value, -60, 0);
            if (WaveOutEvent != null)
            {
                WaveOutEvent.Volume = Mathf.DbToLinear(_volumeDb);
            }
        }
        get => _volumeDb;
    }

    private BufferedWaveProvider BufferedWaveProvider { set; get; }
    private WaveOutEvent WaveOutEvent { set; get; }

    public override void _Ready()
    {
        SetProcess(false);
        SetPhysicsProcess(false);
        var waveFormat = new WaveFormat(SampleRate, 16, 1);
        BufferedWaveProvider = new BufferedWaveProvider(waveFormat)
        {
            // 避免内存溢出
            DiscardOnBufferOverflow = true,
            // 0.5秒缓冲区
            BufferDuration = TimeSpan.FromSeconds(0.5)
        };
        WaveOutEvent = new WaveOutEvent
        {
            // 播放延迟
            DesiredLatency = 50,
            // 越多稳定性越好但延迟越高
            NumberOfBuffers = 2,
            Volume = Mathf.DbToLinear(_volumeDb)
        };
        WaveOutEvent.PlaybackStopped += (sender, args) =>
        {
            BufferedWaveProvider.ClearBuffer();
            Log.Info($"播放停止");
        };
    }

    public void ReceiveRecordVoiceData(ulong steamId, byte[] compressData)
    {
        if (IsPlaying())
        {
            AddSamples(SUser.DecompressVoice(compressData));
        }
    }

    public void Play(float fromPosition = 0.0f)

    {
        WaveOutEvent.Init(BufferedWaveProvider);
        WaveOutEvent.Play();
    }

    public void Stop()
    {
        WaveOutEvent.Stop();
    }

    public bool IsPlaying() => WaveOutEvent.PlaybackState == PlaybackState.Playing;

    private void AddSamples(byte[] data)
    {
        BufferedWaveProvider.AddSamples(data, 0, data.Length);
    }
}