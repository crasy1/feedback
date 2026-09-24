using System;
using System.Buffers.Binary;

namespace Godot;

internal enum SteamPeerMessageKind : byte
{
    Data,
    Hello,
    Welcome,
    Acknowledge,
    Disconnect
}

internal readonly record struct SteamPeerHeader(
    SteamPeerMessageKind Kind,
    MultiplayerPeer.TransferModeEnum Mode,
    int PeerId,
    int Channel,
    ulong SenderSession,
    ulong RecipientSession);

/// <summary>
/// 插件自有的 SMP2 协议。Facepunch 回调丢失的 lane/可靠性在此保留，不读取其内部结构。
/// 两端必须使用相同版本；控制消息不包含游戏负载。
/// </summary>
internal static class SteamPeerProtocol
{
    public const byte Version = 2;
    public const int HeaderSize = 32;
    public const int FirstDataChannel = 3;
    public const string LobbyVersionKey = "steam_multiplayer_protocol";
    public const string LobbyVersion = "2";
    private const uint Magic = 0x32504D53; // SMP2

    public static int TransportChannel(int channel, MultiplayerPeer.TransferModeEnum mode) =>
        FirstDataChannel + channel * 2 + (mode == MultiplayerPeer.TransferModeEnum.Unreliable ? 1 : 0);

    public static int LaneCount(int channels) => FirstDataChannel + channels * 2;

    public static MultiplayerPeer.TransferModeEnum EffectiveMode(MultiplayerPeer.TransferModeEnum mode) =>
        mode == MultiplayerPeer.TransferModeEnum.Unreliable
            ? MultiplayerPeer.TransferModeEnum.Unreliable
            : MultiplayerPeer.TransferModeEnum.Reliable;

    public static byte[] Encode(SteamPeerHeader header, ReadOnlySpan<byte> payload = default)
    {
        var bytes = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
        bytes[4] = Version;
        bytes[5] = (byte)header.Kind;
        bytes[6] = (byte)header.Mode;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), header.PeerId);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), header.Channel);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), header.SenderSession);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), header.RecipientSession);
        payload.CopyTo(bytes.AsSpan(HeaderSize));
        return bytes;
    }

    public static bool TryDecode(ReadOnlySpan<byte> bytes, int channelCount, out SteamPeerHeader header)
    {
        header = default;
        if (bytes.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic ||
            bytes[4] != Version || bytes[7] != 0 || bytes[5] > (byte)SteamPeerMessageKind.Disconnect ||
            (bytes[6] != (byte)MultiplayerPeer.TransferModeEnum.Reliable &&
             bytes[6] != (byte)MultiplayerPeer.TransferModeEnum.Unreliable))
        {
            return false;
        }

        header = new SteamPeerHeader((SteamPeerMessageKind)bytes[5], (MultiplayerPeer.TransferModeEnum)bytes[6],
            BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]));
        if (header.PeerId < 1 || header.SenderSession == 0 || header.Channel < 0 || header.Channel >= channelCount)
        {
            return false;
        }

        if (header.Kind == SteamPeerMessageKind.Data)
        {
            return bytes.Length > HeaderSize && header.RecipientSession != 0;
        }

        return bytes.Length == HeaderSize && header.Mode == MultiplayerPeer.TransferModeEnum.Reliable &&
            header.Channel == 0 &&
            (header.Kind == SteamPeerMessageKind.Hello ? header.RecipientSession == 0 : header.RecipientSession != 0);
    }
}
