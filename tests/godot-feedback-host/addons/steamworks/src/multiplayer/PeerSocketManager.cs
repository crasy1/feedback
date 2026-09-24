using System;
using System.Collections.Generic;
using Steamworks;
using Steamworks.Data;

namespace Godot;

public class PeerSocketManager(SteamSocketPeer steamPeer) : ISocketManager
{
    public readonly Dictionary<Connection, SteamId> ConnectionDict = new();
    public readonly Dictionary<SteamId, Connection> SteamIdDict = new();

    public void OnConnecting(Connection connection, ConnectionInfo info)
    {
        var replacing = SteamIdDict.TryGetValue(info.Identity.SteamId, out var old) && old != connection;
        if (steamPeer.BeginTransportHandshake(info.Identity.SteamId, replacing) && steamPeer.ConfigureConnection(connection))
        {
            TrackConnection(connection, info.Identity.SteamId);
            if (connection.Accept() != Result.OK)
            {
                OnDisconnected(connection, info);
                connection.Close();
            }
        }
        else
        {
            connection.Close();
        }
    }

    public void OnConnected(Connection connection, ConnectionInfo info)
    {
        var steamId = info.Identity.SteamId;
        var replacing = SteamIdDict.TryGetValue(steamId, out var old) && old != connection;
        if (!steamPeer.BeginTransportHandshake(steamId, replacing) || !steamPeer.ConfigureConnection(connection))
        {
            connection.Close();
            return;
        }
        TrackConnection(connection, steamId);
        steamPeer.OnSocketConnected(steamId);
    }

    private void TrackConnection(Connection connection, SteamId steamId)
    {
        if (SteamIdDict.TryGetValue(steamId, out var previous) && previous != connection)
        {
            ConnectionDict.Remove(previous);
            previous.Close();
        }
        ConnectionDict.TryAdd(connection, steamId);
        SteamIdDict[steamId] = connection;
    }

    public void OnDisconnected(Connection connection, ConnectionInfo info)
    {
        var steamId = info.Identity.SteamId;
        ConnectionDict.Remove(connection);
        // 已替换的旧连接回调不能断开同 SteamId 的新连接。
        if (SteamIdDict.TryGetValue(steamId, out var current) && current == connection)
        {
            SteamIdDict.Remove(steamId);
            steamPeer.OnSocketDisconnected(steamId);
        }
    }

    public unsafe void OnMessage(Connection connection, NetIdentity identity, IntPtr data, int size, long messageNum,
        long recvTime,
        int channel)
    {
        if (size >= SteamPeerProtocol.HeaderSize && size <= SteamPeer.MaxPacketSize + SteamPeerProtocol.HeaderSize &&
            ConnectionDict.TryGetValue(connection, out var steamId))
        {
            var span = new Span<byte>((byte*)data.ToPointer(), size);
            steamPeer.ReceiveData(steamId, channel, span.ToArray());
        }
    }

    public bool Remove(SteamId steamId, out Connection connection)
    {
        if (!SteamIdDict.Remove(steamId, out connection)) return false;
        ConnectionDict.Remove(connection);
        return true;
    }

    public void Clear()
    {
        ConnectionDict.Clear();
        SteamIdDict.Clear();
    }
}
