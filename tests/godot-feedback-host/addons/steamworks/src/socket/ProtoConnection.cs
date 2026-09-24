using System;
using System.Runtime.InteropServices;
using System.Text;
using Steamworks;
using Steamworks.Data;

namespace Godot;

public class ProtoConnection : IConnectionManager
{
    private SteamSocket SteamSocket { set; get; }
    private string Name { set; get; }

    public Friend Friend { private set; get; }

    public ProtoConnection(SteamSocket steamSocket)
    {
        SteamSocket = steamSocket;
        Name = SteamSocket.Name;
    }

    public void OnConnecting(ConnectionInfo info)
    {
        if (GodotObject.IsInstanceValid(SteamSocket))
        {
            SteamSocket.SetProcess(false);
        }
    }

    public void OnConnected(ConnectionInfo info)
    {
        if (GodotObject.IsInstanceValid(SteamSocket))
        {
            SteamSocket.SetProcess(true);
            if (info.Identity.IsSteamId)
            {
                if (SFriends.Friends.TryGetValue(info.Identity.SteamId, out var friend))
                {
                    Friend = friend;
                    Log.Info($"{SteamSocket.SocketName} => 与 {friend.Id},{friend.Name} 连接成功");
                    SteamSocket.EmitSignal(SteamSocket.SignalName.Connected, info.Identity.SteamId.Value);
                }
            }
        }
    }

    public void OnDisconnected(ConnectionInfo info)
    {
        if (GodotObject.IsInstanceValid(SteamSocket))
        {
            SteamSocket.SetProcess(false);
            if (SFriends.Friends.TryGetValue(info.Identity.SteamId, out var friend))
            {
                Log.Info($"{SteamSocket.SocketName} => 与 {friend.Id},{friend.Name} 断开连接");
                SteamSocket.EmitSignal(SteamSocket.SignalName.Disconnected, info.Identity.SteamId.Value);
            }
        }
    }

    public void OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel)
    {
        if (GodotObject.IsInstanceValid(SteamSocket))
        {
            var protoBufMsg = ProtoBufMsg.From(data, size);
            Log.Debug($"{SteamSocket.SocketName} => 从 {Friend.Id},{Friend.Name} 收到信息 {protoBufMsg.Type}");
            SteamSocket.EmitSignal(SteamSocket.SignalName.ReceiveMessage, Friend.Id.Value, protoBufMsg);
        }
    }
}