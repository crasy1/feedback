using Steamworks;

namespace Godot;

/// <summary>
/// steam 消息包
/// </summary>
public struct SteamPacket
{
    public int TransferChannel { init; get; }
    public byte[] Data { init; get; }
    public SteamId SteamId { init; get; }
    public int PeerId { init; get; }
    public MultiplayerPeer.TransferModeEnum TransferMode { init; get; }
}
