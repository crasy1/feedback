using System.Text;
using ProtoBuf.Meta;
using Steamworks;
using Steamworks.Data;

namespace Godot;

[SceneTree]
public partial class SteamTest : Node2D
{
    private SteamUserInfo SteamUserInfo { set; get; }
    private NormalServer NormalServer { set; get; }
    private NormalClient NormalClient { set; get; }
    private RelayServer RelayServer { set; get; }
    private RelayClient RelayClient { set; get; }

    public override void _Ready()
    {
        base._Ready();
        SNetworking.Instance.ReceiveData += (steamId, channel, data) =>
        {
            Log.Info($"从 {steamId} 收到P2P消息 {data}");
            P2PReceiveText.AppendText(Encoding.UTF8.GetString(data));
        };

        ShowFriend.Pressed += () => { ShowFriends(); };
        SendP2P.Pressed += () =>
        {
            if (SteamUserInfo == null)
            {
                return;
            }

            SNetworking.SendP2P(SteamUserInfo.Friend.Id, P2PText.Text, Channel.Msg);
        };
        DisconnectP2P.Pressed += () =>
        {
            if (SteamUserInfo == null)
            {
                return;
            }

            var disconnect = SNetworking.Instance.Disconnect(SteamUserInfo.Friend.Id);
            Log.Info(disconnect);
        };
        // NormalServerIp,NormalServerPort,NormalServerText,NormalServerReceiveText
        CreateNormalServer.Pressed += () =>
        {
            NormalServer?.Close();
            NormalServer = SNetworkingSockets.CreateNormal((ushort)NormalServerPort.Value);
            NormalServer.Create();
            NormalServerPort.Value = NormalServer.NetAddress.Port;
            NormalServer.ReceiveMessage += (id, msg) =>
            {
                var protoBufMsg = msg as ProtoBufMsg;
                var deserialize = protoBufMsg.Deserialize();
                NormalServerReceiveText.AddText($"{id}:{protoBufMsg.Type} {deserialize} \r\n");
            };
            NormalServer.Connected += (id) =>
            {
                NormalServerReceiveText.AddText($"已连接：{id} \r\n");
                NormalServerConnections.AddChild(SteamUserInfo.Instantiate(SFriends.Friends[id]));
            };
            NormalServer.Disconnected += (id) =>
            {
                NormalServerReceiveText.AddText($"已断开连接：{id} \r\n");
                foreach (var steamUserInfo in NormalServerConnections.GetChildren<SteamUserInfo>())
                {
                    if (steamUserInfo.Friend.Id == id)
                    {
                        steamUserInfo.QueueFree();
                        NormalServerConnections.RemoveChild(steamUserInfo);
                    }
                }
            };
        };
        CloseNormalServer.Pressed += () =>
        {
            NormalServer?.Close();
            NormalServer = null;
        };
        SendToClientNormal.Pressed += () =>
        {
            if (SteamUserInfo == null)
            {
                Log.Info("向所有发送");
                NormalServer?.Send(ProtoBufMsg.From(NormalServerText.Text));
            }
            else
            {
                Log.Info("向单个发送");
                NormalServer?.Send(SteamUserInfo.Friend.Id, ProtoBufMsg.From(NormalServerText.Text));
            }
        };
        // NormalIp,NormalPort,NormalClientText,NormalClientReceiveText
        ConnectNormalClient.Pressed += () =>
        {
            NormalClient?.Close();
            var host = NormalIp.Text;
            NormalClient = SNetworkingSockets.ConnectNormal(host, (ushort)NormalPort.Value);
            NormalClient.ReceiveMessage += (id, msg) =>
            {
                var protoBufMsg = msg as ProtoBufMsg;
                var deserialize = protoBufMsg.Deserialize();
                NormalClientReceiveText.AddText($"{id}:{protoBufMsg.Type} {deserialize}\r\n");
            };
            NormalClient.Connected += (id) =>
            {
                NormalClientReceiveText.AddText($"已连接：{id} \r\n");
                NormalClientConnections.AddChild(SteamUserInfo.Instantiate(SFriends.Friends[id]));
            };
            NormalClient.Disconnected += (id) =>
            {
                NormalClient = null;
                NormalClientReceiveText.AddText($"已断开连接：{id} \r\n");
                foreach (var steamUserInfo in NormalClientConnections.GetChildren<SteamUserInfo>())
                {
                    if (steamUserInfo.Friend.Id == id)
                    {
                        steamUserInfo.QueueFree();
                        NormalClientConnections.RemoveChild(steamUserInfo);
                    }
                }
            };
            NormalClient.Connect();
        };
        DisconnectNormalClient.Pressed += () =>
        {
            NormalClient?.Close();
            NormalClient = null;
        };
        SendToNormalServer.Pressed += () => { NormalClient?.Send(ProtoBufMsg.From(NormalClientText.Text)); };

        // RelayServerIp,RelayServerPort,RelayServerText,RelayServerReceiveText
        CreateRelayServer.Pressed += () =>
        {
            RelayServer?.Close();
            RelayServer = SNetworkingSockets.CreateRelay((int)RelayServerPort.Value);
            RelayServer.ReceiveMessage += (id, msg) =>
            {
                var protoBufMsg = msg as ProtoBufMsg;
                var deserialize = protoBufMsg.Deserialize();
                RelayServerReceiveText.AddText($"{id}:{protoBufMsg.Type} {deserialize} \r\n");
            };
            RelayServer.Connected += (id) =>
            {
                RelayServerReceiveText.AddText($"已连接：{id} \r\n");
                RelayServerConnections.AddChild(SteamUserInfo.Instantiate(SFriends.Friends[id]));
            };
            RelayServer.Disconnected += (id) =>
            {
                RelayServerReceiveText.AddText($"已断开连接：{id} \r\n");
                foreach (var steamUserInfo in RelayServerConnections.GetChildren<SteamUserInfo>())
                {
                    if (steamUserInfo.Friend.Id == id)
                    {
                        steamUserInfo.QueueFree();
                        RelayServerConnections.RemoveChild(steamUserInfo);
                    }
                }
            };
            RelayServer.Create();
        };
        CloseRelayServer.Pressed += () =>
        {
            RelayServer?.Close();
            RelayServer = null;
        };
        SendToClientRelay.Pressed += () =>
        {
            if (SteamUserInfo == null)
            {
                Log.Info("向所有发送");
                RelayServer?.Send(ProtoBufMsg.From(RelayServerText.Text));
            }
            else
            {
                Log.Info("向单个发送");
                RelayServer?.Send(SteamUserInfo.Friend.Id, ProtoBufMsg.From(RelayServerText.Text));
            }
        };
        // RelayIp,RelayPort,RelayClientText,RelayClientReceiveText
        ConnectRelayClient.Pressed += () =>
        {
            RelayClient?.Close();
            var port = (ushort)RelayPort.Value;
            RelayClient = SNetworkingSockets.ConnectRelay(SteamUserInfo.Friend.Id, port);
            RelayClient.ReceiveMessage += (id, msg) =>
            {
                var protoBufMsg = msg as ProtoBufMsg;
                RelayClientReceiveText.AddText($"{id}:{protoBufMsg.Type} {protoBufMsg.Deserialize()} \r\n");
            };
            RelayClient.Connected += (id) =>
            {
                RelayClientReceiveText.AddText($"已连接：{id} \r\n");
                RelayClientConnections.AddChild(SteamUserInfo.Instantiate(SFriends.Friends[id]));
            };
            RelayClient.Disconnected += (id) =>
            {
                RelayClient = null;
                RelayClientReceiveText.AddText($"已断开连接：{id} \r\n");
                foreach (var steamUserInfo in RelayClientConnections.GetChildren<SteamUserInfo>())
                {
                    if (steamUserInfo.Friend.Id == id)
                    {
                        steamUserInfo.QueueFree();
                        RelayClientConnections.RemoveChild(steamUserInfo);
                    }
                }
            };
            RelayClient.Connect();
        };
        DisconnectRelayClient.Pressed += () =>
        {
            RelayClient?.Close();
            RelayClient = null;
        };
        SendToRelayServer.Pressed += () => { RelayClient?.Send(ProtoBufMsg.From(RelayClientText.Text)); };
    }

    public void ShowFriends()
    {
        Friends.ClearAndFreeChildren();
        var userInfo = SteamUserInfo.Instantiate(SFriends.Me);
        Friends.AddChild(userInfo);
        userInfo.GuiInput += (@event) =>
        {
            if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            {
                SteamUserInfo = userInfo;
                Log.Info($"选中 {userInfo.Friend.Id} {userInfo.Friend.Name}");
            }
        };
        foreach (var friend in SteamFriends.GetFriends())
        {
            if (friend.IsOnline)
            {
                var steamUserInfo = SteamUserInfo.Instantiate(friend);
                Friends.AddChild(steamUserInfo);
                steamUserInfo.GuiInput += (@event) =>
                {
                    if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
                    {
                        SteamUserInfo = steamUserInfo;
                        Log.Info($"选中 {steamUserInfo.Friend.Id} {steamUserInfo.Friend.Name}");
                    }
                };
            }
        }
    }
}