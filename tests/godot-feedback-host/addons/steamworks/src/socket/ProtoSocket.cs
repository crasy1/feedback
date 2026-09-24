using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Steamworks;
using Steamworks.Data;

namespace Godot;

public class ProtoSocket : ISocketManager
{
    private SteamSocket SteamSocket { set; get; }
    private string Name { set; get; }

    public readonly Dictionary<Connection, Friend> Connections = new();

    public ProtoSocket(SteamSocket steamSocket)
    {
        SteamSocket = steamSocket;
        Name = SteamSocket.Name;
    }

    public void OnConnecting(Connection connection, ConnectionInfo info)
    {
        connection.Accept();
    }

    public void OnConnected(Connection connection, ConnectionInfo info)
    {
        if (info.Identity.IsSteamId)
        {
            if (SFriends.Friends.TryGetValue(info.Identity.SteamId, out var friend))
            {
                Connections.TryAdd(connection, friend);
                Log.Info($"{SteamSocket.SocketName} => 与 {friend.Id},{friend.Name} 连接成功");
                if (GodotObject.IsInstanceValid(SteamSocket))
                {
                    SteamSocket.EmitSignal(SteamSocket.SignalName.Connected, info.Identity.SteamId.Value);
                }
            }
        }
    }

    public void OnDisconnected(Connection connection, ConnectionInfo info)
    {
        if (info.Identity.IsSteamId)
        {
            if (SFriends.Friends.TryGetValue(info.Identity.SteamId, out var friend))
            {
                Connections.Remove(connection);
                Log.Info($"{SteamSocket.SocketName} => 与 {friend.Id},{friend.Name} 断开连接");

                if (GodotObject.IsInstanceValid(SteamSocket))
                {
                    SteamSocket.EmitSignal(SteamSocket.SignalName.Disconnected, info.Identity.SteamId.Value);
                }
            }
        }
    }

    public void OnMessage(Connection connection, NetIdentity identity, IntPtr data, int size, long messageNum,
        long recvTime,
        int channel)
    {
        if (GodotObject.IsInstanceValid(SteamSocket))
        {
            if (identity.IsSteamId)
            {
                if (SFriends.Friends.TryGetValue(identity.SteamId, out var friend))
                {
                    var protoBufMsg = ProtoBufMsg.From(data, size);
                    Log.Debug($"{SteamSocket.SocketName} => 从 {friend.Id},{friend.Name} 收到信息 {protoBufMsg.Type}");
                    SteamSocket.EmitSignal(SteamSocket.SignalName.ReceiveMessage, identity.SteamId.Value, protoBufMsg);
                }
            }
        }
    }
}