using Godot;
using System;

/// <summary>
/// 使用godot组件实时播放steamworks音频流
/// </summary>
public partial class VoiceStream : Node
{
    [Signal]
    public delegate void SpeakEventHandler();

    [Signal]
    public delegate void SilentEventHandler();

    /// <summary>
    /// 缓冲区
    /// </summary>
    public float BufferLength { get; set; } = 0.1f;

    /// <summary>
    /// 采样率 需要与音频流一致，否则音色会失真
    /// </summary>
    public SampleRate SampleRate { get; set; } = SteamConfig.SampleRate;

    /// <summary>
    /// 通道数 1=单声道, 2=立体声
    /// </summary>
    public int Channels { get; set; } = 1;

    /// <summary>
    /// 位数
    /// </summary>
    public int Bit { get; set; } = 16;

    private const float Pcm16 = 32768.0f;

    /// <summary>
    /// 每位
    /// </summary>
    private const int B = 8;

    private AudioStreamGenerator AudioStreamGenerator { set; get; }

    private AudioStreamGeneratorPlayback? Playback { set; get; }

    private AudioEffectSpectrumAnalyzerInstance? AudioEffectSpectrumAnalyzerInstance;

    private bool LastFrameIsPlaying { set; get; }

    /// <summary>
    /// 连续检测到声音的帧数，达到阈值后通知开始说话。
    /// </summary>
    private int _activeFrames;

    private IVoiceStreamPlayer AudioPlayer { set; get; }

    private int ActiveFrames
    {
        set
        {
            var wasSpeaking = _activeFrames >= MinActiveFrames;
            _activeFrames = value;
            if (wasSpeaking && value < MinActiveFrames)
            {
                EmitSignalSilent();
            }
            else if (!wasSpeaking && value >= MinActiveFrames)
            {
                EmitSignalSpeak();
            }
        }
        get => _activeFrames;
    }

    private const int MinActiveFrames = 10;

    public override void _Ready()
    {
        SetPhysicsProcess(false);
        SetProcess(true);
        SetProcessMode(ProcessModeEnum.Always);
        AudioStreamGenerator = new AudioStreamGenerator()
        {
            MixRate = (int)SampleRate,
            BufferLength = BufferLength
        };
        var parent = GetParent();
        if (parent is IVoiceStreamPlayer audioStreamPlayer)
        {
            AudioPlayer = audioStreamPlayer;
            if (AudioServer.GetBusIndex(Game.AudioBus.TeamVoice) >= 0)
            {
                audioStreamPlayer.SetBus(Game.AudioBus.TeamVoice);
                AudioEffectSpectrumAnalyzerInstance = Game.AudioBus.TeamVoiceAudioEffectSpectrumAnalyzer();
            }
            else
            {
                audioStreamPlayer.SetBus("Master");
            }
            audioStreamPlayer.SetStream(AudioStreamGenerator);
        }
        else
        {
            SetProcess(false);
            Log.Error($"{nameof(VoiceStream)}父节点不是 {nameof(IVoiceStreamPlayer)}");
            this.RemoveAndQueueFree();
        }
    }

    public void ReceiveRecordVoiceData(ulong steamId, byte[] compressData)
    {
        if (UpdatePlaybackState(refreshPlayback: true))
        {
            PushData(SUser.DecompressVoice(compressData));
        }
    }

    /// <summary>
    /// 播放音频流
    /// </summary>
    private void PlayStream()
    {
        var currentPlayback = AudioPlayer.GetStreamPlayback() as AudioStreamGeneratorPlayback;
        if (Playback != currentPlayback)
        {
            StopStream();
            Playback = currentPlayback;
        }
    }

    /// <summary>
    /// 停止音频流
    /// </summary>
    private void StopStream()
    {
        Playback?.Stop();
        Playback?.ClearBuffer();
        Playback = null;
        ActiveFrames = 0;
    }


    private bool UpdatePlaybackState(bool refreshPlayback = false)
    {
        var playing = AudioPlayer?.IsPlaying() ?? false;
        switch (playing)
        {
            // Stop/Play 可以发生在两帧之间；旧播放仍可能处于渐出，收包前核对当前句柄。
            case true when refreshPlayback || !LastFrameIsPlaying || Playback?.IsPlaying() != true:
                PlayStream();
                break;
            case false when LastFrameIsPlaying:
                StopStream();
                break;
        }

        LastFrameIsPlaying = playing;
        return playing;
    }

    public override void _Process(double delta)
    {
        if (!UpdatePlaybackState())
        {
            return;
        }

        // 统计连续有声帧，静音时复位。
        var magnitude = AudioEffectSpectrumAnalyzerInstance?.GetMagnitudeForFrequencyRange(0, (int)SampleRate);
        if (magnitude.HasValue)
        {
            var volumeDb = Mathf.LinearToDb(Mathf.Max(magnitude.Value.X, magnitude.Value.Y));
            // 设置静音检测阈值（通常-60dB以下视为静音）
            ActiveFrames = volumeDb < Consts.MinDb ? 0 : Mathf.Min(MinActiveFrames, ActiveFrames + 1);
        }
    }

    /// <summary>
    /// 是否静音
    /// </summary>
    /// <returns></returns>
    public bool IsSilence()
    {
        if (AudioPlayer?.IsPlaying() != true || Playback == null || !Playback.IsPlaying())
        {
            return true;
        }

        return ActiveFrames < MinActiveFrames;
    }

    // 处理音频帧
    private void PushData(byte[] decompress)
    {
        if (Playback == null || decompress.Length == 0)
        {
            return;
        }

        var frameBuffer = DecodePcmFrames(decompress, Channels, Bit, Playback.GetFramesAvailable());
        if (frameBuffer.Length > 0)
        {
            Playback.PushBuffer(frameBuffer);
        }
    }

    internal static Vector2[] DecodePcmFrames(ReadOnlySpan<byte> pcm, int channels, int bit, int maxFrames)
    {
        // Steam 输出为 16 位 PCM；只接受完整的单声道或双声道帧。
        if (bit != 16 || channels is not (1 or 2) || maxFrames <= 0)
        {
            return [];
        }

        var frameBytes = sizeof(short) * channels;
        var framesToPush = Math.Min(maxFrames, pcm.Length / frameBytes);
        if (framesToPush == 0)
        {
            return [];
        }

        var frameBuffer = new Vector2[framesToPush];
        var position = 0;
        // 填充音频数据
        for (var i = 0; i < framesToPush; i++)
        {
            var left = 0f;
            var right = 0f;

            // 读取16位PCM样本 (小端字节序)
            if (channels == 1)
            {
                // 单声道 - 复制到左右声道
                // 读取当前位置的两个字节，通过位运算(a | b << 8)组合成一个16位的short值
                var sample = (short)(pcm[position] | (pcm[position + 1] << B));
                // 将short值转换为-1.0到1.0范围的浮点数（通过除以32768.0f）
                left = right = sample / Pcm16;
                position += frameBytes;
            }
            else
            {
                // 立体声 - 分别读取左右声道
                var leftSample = (short)(pcm[position] | (pcm[position + 1] << B));
                var rightSample = (short)(pcm[position + 2] | (pcm[position + 3] << B));
                left = leftSample / Pcm16;
                right = rightSample / Pcm16;
                position += frameBytes;
            }

            frameBuffer[i] = new Vector2(left, right);
        }

        return frameBuffer;
    }
}
