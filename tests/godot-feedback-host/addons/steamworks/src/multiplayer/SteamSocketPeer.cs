using System;
using System.Linq;
using System.Threading.Tasks;
using Steamworks;
using Steamworks.Data;

namespace Godot;

/// <summary>Steam Networking Sockets 的 IP/Relay 传输；Facepunch 的通道回调不用于协议解析。</summary>
public partial class SteamSocketPeer : SteamPeer
{
    public const int DefaultPort = 60937;
    private PeerSocketManager? _peerSocketManager;
    private SocketManager? _socketManager;
    private ConnectionManager? _connectionManager;
    private readonly int[] _lanePriorities;
    private readonly ushort[] _laneWeights;
    private readonly SteamReceivePump _receivePump;
    private readonly Func<int, int, int> _readBatch;
    private static readonly int[] ReceiveQueue = [0];

    private SteamSocketPeer(int peerId, SteamSocketType type, SteamNetworkOptions? options = null,
        SteamId expectedServer = default) : base(peerId, type, options, expectedServer)
    {
        _lanePriorities = new int[SteamPeerProtocol.LaneCount(Options.ChannelCount)];
        _laneWeights = Enumerable.Repeat((ushort)1, _lanePriorities.Length).ToArray();
        _receivePump = new SteamReceivePump(Options);
        _readBatch = (_, count) => !IsActive || !SteamClient.IsValid ? 0 :
            _IsServer() ? _socketManager?.Receive(count, receiveToEnd: false) ?? 0 :
                _connectionManager?.Receive(count, receiveToEnd: false) ?? 0;
    }

    public static Task<SteamSocketPeer> CreateRelayServer(int port, SteamNetworkOptions? options = null)
    {
        var peer = new SteamSocketPeer(ServerPeerId, SteamSocketType.Relay, options);
        try
        {
            peer._peerSocketManager = new PeerSocketManager(peer);
            peer._socketManager = SteamNetworkingSockets.CreateRelaySocket(port, peer._peerSocketManager);
            peer.Activate();
            return Task.FromResult(peer);
        }
        catch { peer._Close(); throw; }
    }

    public static SteamSocketPeer CreateNormalServer(ushort port, SteamNetworkOptions? options = null)
    {
        var peer = new SteamSocketPeer(ServerPeerId, SteamSocketType.Normal, options);
        try
        {
            peer._peerSocketManager = new PeerSocketManager(peer);
            peer._socketManager = SteamNetworkingSockets.CreateNormalSocket(NetAddress.AnyIp(port), peer._peerSocketManager);
            peer.Activate();
            return peer;
        }
        catch { peer._Close(); throw; }
    }

    public static SteamSocketPeer CreateRelayClient(SteamId steamId, int port, SteamNetworkOptions? options = null)
    {
        if (steamId == 0) throw new ArgumentOutOfRangeException(nameof(steamId));
        var peer = new SteamSocketPeer(0, SteamSocketType.Relay, options, steamId);
        try
        {
            peer._connectionManager = SteamNetworkingSockets.ConnectRelay(steamId, port, new PeerConnectionManager(peer));
            peer.Activate();
            return peer;
        }
        catch { peer._Close(); throw; }
    }

    public static SteamSocketPeer CreateNormalClient(string host, ushort port, SteamNetworkOptions? options = null)
    {
        var peer = new SteamSocketPeer(0, SteamSocketType.Normal, options);
        try
        {
            peer._connectionManager = SteamNetworkingSockets.ConnectNormal(NetAddress.From(host, port), new PeerConnectionManager(peer));
            peer.Activate();
            return peer;
        }
        catch { peer._Close(); throw; }
    }

    internal bool ConfigureConnection(Connection connection) =>
        connection.ConfigureConnectionLanes(_lanePriorities, _laneWeights) == Result.OK;

    internal void ClientConnected(SteamId steamId)
    {
        if (_connectionManager is null || !CanAcceptConnection(steamId) || !ConfigureConnection(_connectionManager.Connection))
        {
            _Close();
            return;
        }
        OnSocketConnected(steamId);
    }

    protected override void OnCreate() { }

    protected override void OnClose()
    {
        if (SteamClient.IsValid)
        {
            if (_peerSocketManager is not null)
                foreach (var connection in _peerSocketManager.ConnectionDict.Keys.ToArray()) connection.Close();
            _connectionManager?.Close();
            _socketManager?.Close();
        }
        _peerSocketManager?.Clear();
        _connectionManager = null;
        _socketManager = null;
    }

    protected override bool SendMsg(SteamId steamId, byte[] data, Channel channel = Channel.Msg,
        SendType sendType = SendType.Reliable)
    {
        if (!SteamClient.IsValid) return false;
        if (_IsServer())
        {
            return _peerSocketManager is not null && _peerSocketManager.SteamIdDict.TryGetValue(steamId, out var connection) &&
                connection.SendMessage(data, sendType, (ushort)channel) == Result.OK;
        }
        return _connectionManager is not null && steamId == ExpectedServer &&
            _connectionManager.Connection.SendMessage(data, sendType, (ushort)channel) == Result.OK;
    }

    protected override void DisconnectTransport(SteamId steamId, bool force)
    {
        if (!SteamClient.IsValid) return;
        if (_IsServer())
        {
            if (_peerSocketManager is not null && _peerSocketManager.Remove(steamId, out var connection))
                connection.Close(linger: !force);
        }
        else _connectionManager?.Connection.Close(linger: !force);
    }

    protected override void Receive()
    {
        try { _receivePump.Poll(ReceiveQueue, _readBatch); }
        catch (Exception exception)
        {
            Log.Error("[steam] Socket 接收失败", exception);
            _Close();
        }
    }
}
