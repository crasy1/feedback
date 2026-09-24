using System;
using System.Buffers;
using System.IO;
using Steamworks;

namespace Godot;

[Singleton]
public partial class SUser : SteamComponent
{
    private readonly MemoryStream _recordedVoice = new();

    /// <summary>
    /// 录到用户steamworks语音压缩数据
    /// </summary>
    [Signal]
    public delegate void RecordVoiceDataEventHandler(ulong steamId, byte[] compressData);

    /// <summary>
    /// 和steam服务器的网络状态
    /// </summary>
    public bool ServerConnected { set; get; } = true;

    public override void _Ready()
    {
        base._Ready();
        SetProcessInput(false);
        SteamUser.OnClientGameServerDeny += () => { Log.Info($"[steam] 游戏服务器已拒绝客户端连接"); };
        SteamUser.OnDurationControl += (durationControl) => { Log.Info($"[steam] 游戏时长控制"); };
        SteamUser.OnGameWebCallback += (url) => { Log.Info($"[steam] 游戏网页回调 {url}"); };
        SteamUser.OnLicensesUpdated += () => { Log.Info($"[steam] 已更新授权"); };
        SteamUser.OnMicroTxnAuthorizationResponse += (appId, orderId, userAuthorized) =>
        {
            Log.Info($"[steam] 用户响应微事务授权请求 {appId} {orderId} {userAuthorized}");
        };
        SteamUser.OnSteamServersConnected += () =>
        {
            Log.Info($"[steam] 已连接到Steam服务器");
            ServerConnected = true;
        };
        SteamUser.OnSteamServersDisconnected += () =>
        {
            // 无法与steam上的用户进行p2p通信
            Log.Info($"[steam] 已断开Steam服务器");
            ServerConnected = false;
        };
        SteamUser.OnSteamServerConnectFailure += () =>
        {
            Log.Info($"[steam] Steam服务器连接失败");
            ServerConnected = false;
        };
        SteamUser.OnValidateAuthTicketResponse += (steamId, steamId2, authResponse) =>
        {
            Log.Info($"[steam] 用户验证授权 {steamId} {steamId2} {authResponse}");
        };
        SClient.Instance.SteamClientConnected += OnClientConnected;
        SClient.Instance.SteamClientDisconnected += OnClientDisconnected;
    }

    private void OnClientConnected()
    {
        // Facepunch 的录音标记跨 Shutdown 保留，新会话需要显式复位。
        SteamUser.VoiceRecord = false;
        SteamUser.SampleRate = (uint)SteamConfig.SampleRate;
        SetProcess(true);
        SetProcessInput(true);
        Log.Info($"[steam] 设置音频采样率 {SteamUser.SampleRate}");
    }

    private void OnClientDisconnected()
    {
        SetProcess(false);
        SetProcessInput(false);
        _recordedVoice.SetLength(0);
    }

    public override void _ExitTree()
    {
        SClient.Instance.SteamClientConnected -= OnClientConnected;
        SClient.Instance.SteamClientDisconnected -= OnClientDisconnected;
        StopRecord();
        _recordedVoice.Dispose();
    }

    public override void _Input(InputEvent @event)
    {
        if (!InputMap.HasAction(Const.Action.Record))
        {
            return;
        }

        if (@event.IsActionPressed(Const.Action.Record))
        {
            StartRecord();
        }

        if (@event.IsActionReleased(Const.Action.Record))
        {
            StopRecord();
        }
    }

    public void StartRecord()
    {
        if (!SteamClient.IsValid || SteamUser.VoiceRecord)
        {
            return;
        }

        SteamUser.VoiceRecord = true;
        Log.Info($"[steam] 录音开始");
    }

    public void StopRecord()
    {
        if (!SteamClient.IsValid || !SteamUser.VoiceRecord)
        {
            return;
        }

        SteamUser.VoiceRecord = false;
        Log.Info($"[steam] 录音结束");
    }


    public override void _Process(double delta)
    {
        try
        {
            if (!SteamClient.IsValid || !SteamUser.HasVoiceData)
            {
                return;
            }

            _recordedVoice.SetLength(0);
            SteamUser.ReadVoiceData(_recordedVoice);
            if (_recordedVoice.Length > 0)
            {
                // 信号接收者可以保留数据，因此只复制有效字节，不能暴露复用缓冲区。
                EmitSignalRecordVoiceData(SteamClient.SteamId, _recordedVoice.ToArray());
            }
        }
        catch (Exception e)
        {
            Log.Error("[steam] 读取录音失败", e);
        }
    }

    /// <summary>
    /// 解压声音数组
    /// </summary>
    /// <param name="from"></param>
    /// <param name="length"></param>
    /// <returns>输出数据是原始单通道 16 位 PCM 音频</returns>
    public static unsafe byte[] DecompressVoice(byte[] from, int length = ushort.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (from.Length == 0 || !SteamClient.IsValid)
        {
            return [];
        }

        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            int writtenCount;
            fixed (byte* pCompressed = from)
            fixed (byte* pDestBuffer = buffer)
            {
                writtenCount = SteamUser.DecompressVoice((IntPtr)pCompressed, from.Length, (IntPtr)pDestBuffer,
                    length);
            }

            return writtenCount > 0 ? buffer.AsSpan(0, writtenCount).ToArray() : [];
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 读取并解压steamworks声音数据
    /// </summary>
    /// <returns></returns>
    public static byte[] ReadDecompressVoice()
    {
        if (!SteamClient.IsValid || !SteamUser.HasVoiceData)
        {
            return [];
        }

        using var memoryStream = new MemoryStream();
        SteamUser.ReadVoiceData(memoryStream);
        return DecompressVoice(memoryStream.ToArray());
    }

    public void GetInfo()
    {
        Log.Info($@"
----    {nameof(SteamUser)}    ----
SteamLevel:                         {SteamUser.SteamLevel}
SampleRate:                         {SteamUser.SampleRate}
IsBehindNAT:                        {SteamUser.IsBehindNAT}
IsPhoneIdentifying:                 {SteamUser.IsPhoneIdentifying}
IsPhoneVerified:                    {SteamUser.IsPhoneVerified}
IsPhoneRequiringVerification:       {SteamUser.IsPhoneRequiringVerification}
IsTwoFactorEnabled:                 {SteamUser.IsTwoFactorEnabled}
----    {nameof(SteamUser)}    ----
");
    }
}
