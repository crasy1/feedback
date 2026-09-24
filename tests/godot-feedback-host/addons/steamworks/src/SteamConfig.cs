using Serilog.Events;

namespace Godot;

public static class SteamConfig
{
    private static readonly Resource Instance = ResourceLoader.Load(Const.Paths.SteamworksConfig);

    public static uint AppId
    {
        get => Instance.GetMeta(nameof(AppId), 480).AsUInt32();
        set => Save(nameof(AppId), value);
    }

    public static bool Debug
    {
        get => Instance.GetMeta(nameof(Debug), false).AsBool();
        set => Save(nameof(Debug), value);
    }

    public static bool AsServer
    {
        get => Instance.GetMeta(nameof(AsServer), false).AsBool();
        set => Save(nameof(AsServer), value);
    }

    public static bool CallbackDebug
    {
        get => Instance.GetMeta(nameof(CallbackDebug), false).AsBool();
        set => Save(nameof(CallbackDebug), value);
    }

    public static bool CustomBusLayout
    {
        get => Instance.GetMeta(nameof(CustomBusLayout), false).AsBool();
        set => Save(nameof(CustomBusLayout), value);
    }

    public static SampleRate SampleRate
    {
        get => (SampleRate)Instance.GetMeta(nameof(SampleRate), (int)SampleRate.Hz44100).AsInt32();
        set => Save(nameof(SampleRate), (int)value);
    }

    public static LogEventLevel FileLogLevel
    {
        get => (LogEventLevel)Instance.GetMeta(nameof(FileLogLevel), (int)LogEventLevel.Information).AsInt32();
        set => Save(nameof(FileLogLevel), (int)value);
    }

    public static LogEventLevel GdLogLevel
    {
        get => (LogEventLevel)Instance.GetMeta(nameof(GdLogLevel), (int)LogEventLevel.Information).AsInt32();
        set => Save(nameof(GdLogLevel), (int)value);
    }

    public static bool SplashEnabled
    {
        get => Instance.GetMeta(nameof(SplashEnabled), false).AsBool();
        set => Save(nameof(SplashEnabled), value);
    }

    public static SteamNetworkOptions NetworkOptions
    {
        get
        {
            var defaults = SteamNetworkOptions.Defaults;
            return new SteamNetworkOptions
            {
                ChannelCount = Instance.GetMeta("NetworkChannelCount", defaults.ChannelCount).AsInt32(),
                ReceivePacketsPerFrame = Instance.GetMeta("NetworkReceivePacketsPerFrame", defaults.ReceivePacketsPerFrame).AsInt32(),
                ReceiveBatchSize = Instance.GetMeta("NetworkReceiveBatchSize", defaults.ReceiveBatchSize).AsInt32(),
                ReceiveBudgetMilliseconds = Instance.GetMeta("NetworkReceiveBudgetMilliseconds", defaults.ReceiveBudgetMilliseconds).AsDouble(),
                MaxQueuedPackets = Instance.GetMeta("NetworkMaxQueuedPackets", defaults.MaxQueuedPackets).AsInt32(),
                MaxQueuedBytes = Instance.GetMeta("NetworkMaxQueuedBytes", defaults.MaxQueuedBytes).AsInt32(),
                HandshakeTimeoutSeconds = Instance.GetMeta("NetworkHandshakeTimeoutSeconds", defaults.HandshakeTimeoutSeconds).AsDouble(),
                MaxPendingConnections = Instance.GetMeta("NetworkMaxPendingConnections", defaults.MaxPendingConnections).AsInt32(),
                MaxConnections = Instance.GetMeta("NetworkMaxConnections", defaults.MaxConnections).AsInt32()
            };
        }
    }

    private static void Save(string key, Variant value)
    {
        Instance.SetMeta(key, value);
        ResourceSaver.Save(Instance, Const.Paths.SteamworksConfig);
    }
}
