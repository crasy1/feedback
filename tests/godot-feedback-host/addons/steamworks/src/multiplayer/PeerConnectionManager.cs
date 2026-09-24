using System;
using System.Collections.Generic;
using System.Text;
using Steamworks;
using Steamworks.Data;

namespace Godot;

public class PeerConnectionManager(SteamSocketPeer steamPeer) : IConnectionManager
{
    private SteamId SteamId { set; get; }

    public void OnConnecting(ConnectionInfo info)
    {
        // 连接状态由统一握手驱动，native Connected 不等于 Godot 会话就绪。
    }

    public void OnConnected(ConnectionInfo info)
    {
        SteamId = info.Identity.SteamId;
        steamPeer.ClientConnected(SteamId);
    }

    public void OnDisconnected(ConnectionInfo info)
    {
        steamPeer.OnSocketDisconnected(info.Identity.SteamId);
    }

    public unsafe void OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel)
    {
        if (size < SteamPeerProtocol.HeaderSize || size > SteamPeer.MaxPacketSize + SteamPeerProtocol.HeaderSize) return;
        // channel/messageNum/recvTime 在当前 Facepunch 版本中不可靠；数据元信息由协议头恢复。
        var span = new ReadOnlySpan<byte>((byte*)data.ToPointer(), size);
        steamPeer.ReceiveData(SteamId, channel, span.ToArray());
    }
}
