using Steamworks;
using Steamworks.Data;

namespace Godot;

public abstract partial class SteamSocket : SteamComponent
{
    /// <summary>
    /// 与 steamId 连接
    /// </summary>
    [Signal]
    public delegate void ConnectedEventHandler(ulong steamId);

    /// <summary>
    /// 与 steamId 断开
    /// </summary>
    [Signal]
    public delegate void DisconnectedEventHandler(ulong steamId);

    /// <summary>
    /// 收到来自 steamId的消息
    /// </summary>
    /// <param name="steamId"></param>
    /// <param name="msg">ProtoBufMsg</param>
    [Signal]
    public delegate void ReceiveMessageEventHandler(ulong steamId, GodotObject msg);

    /// <summary>
    /// steam socket名字
    /// </summary>
    public string SocketName { protected set; get; }

    public abstract void Send(ProtoBufMsg msg, SendType sendType = SendType.Reliable);
    public abstract void Close();
}