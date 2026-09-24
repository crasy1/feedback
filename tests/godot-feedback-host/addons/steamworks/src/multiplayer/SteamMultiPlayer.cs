using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using Steamworks;
using Steamworks.Data;

namespace Godot;

/// <summary>
/// 扩展steam多人游戏
/// 服务端用法，创建一个SteamMultiPlayer对象，调用CreateLobbyAsync方法创建一个大厅，
/// 创建 MultiplayerPeer server 赋值给 SteamMultiPlayer，正常连接就能使用了，要退出的话调用 LeaveLobby 离开大厅并关闭 MultiplayerPeer
/// 客户端用法，创建一个SteamMultiPlayer对象，然后调用JoinLobbyAsync方法加入一个大厅，
/// 创建 MultiplayerPeer client 赋值给 SteamMultiPlayer，正常连接就能使用了，要退出的话调用 LeaveLobby 离开大厅并关闭 MultiplayerPeer
/// </summary>
public partial class SteamMultiPlayer : MultiplayerApiExtension
{
    private SceneMultiplayer SceneMultiplayer = new();
    private OfflineMultiplayerPeer OfflinePeer = new();
    private Lobby Lobby { set; get; }
    private readonly HashSet<SteamId> _members = new();
    private readonly SMatchmaking _matchmaking;
    private SteamId _host;
    private bool _disposed;
    private bool _leaving;
    private bool _lobbyOperation;
    private int _generation;

    public SteamMultiPlayer()
    {
        _matchmaking = SMatchmaking.Instance;
        SMatchmaking.Instance.LobbyCreated += OnLobbyCreated;
        SMatchmaking.Instance.LobbyEntered += OnLobbyEntered;
        SMatchmaking.Instance.LobbyLeaved += OnLobbyLeaved;
        SMatchmaking.Instance.LobbyInvite += OnLobbyInvite;
        SMatchmaking.Instance.LobbyMemberJoined += OnLobbyMemberJoined;
        SMatchmaking.Instance.LobbyMemberLeave += OnLobbyMemberLeave;
        SMatchmaking.Instance.LobbyMemberDisconnected += OnLobbyMemberDisconnected;
        SMatchmaking.Instance.LobbyMemberDataChanged += OnLobbyMemberDataChanged;
        SMatchmaking.Instance.LobbyDataChanged += OnLobbyDataChanged;
        SMatchmaking.Instance.LobbyChatMessage += OnLobbyChatMessage;
        SMatchmaking.Instance.LobbyMemberKick += OnLobbyMemberKick;

        SceneMultiplayer.PeerConnected += OnPeerConnected;
        SceneMultiplayer.PeerDisconnected += OnPeerDisconnected;
        SceneMultiplayer.ServerDisconnected += OnServerDisconnected;
        SceneMultiplayer.ConnectedToServer += OnConnectedToServer;
        SceneMultiplayer.ConnectionFailed += OnConnectionFailed;
        SceneMultiplayer.MultiplayerPeer = OfflinePeer;
    }

    private void OnConnectionFailed()
    {
        LeaveLobby();
        EmitSignalConnectionFailed();
    }

    private void OnConnectedToServer()
    {
        EmitSignalConnectedToServer();
    }

    private void OnServerDisconnected()
    {
        LeaveLobby();
        EmitSignalServerDisconnected();
    }

    private void OnPeerDisconnected(long id)
    {
        EmitSignalPeerDisconnected(id);
    }

    private void OnPeerConnected(long id)
    {
        EmitSignalPeerConnected(id);
    }

    private void OnLobbyMemberKick(ulong lobbyId, ulong steamId)
    {
        if (Lobby.Id != lobbyId) return;
        _members.Remove(steamId);
        if (_host == SteamClient.SteamId && MultiplayerPeer is SteamPeer peer) peer.BanPeer(steamId);
        if (steamId == SteamClient.SteamId) LeaveLobby();
    }

    private void OnLobbyChatMessage(ulong lobbyId, ulong steamId, string message)
    {
    }

    private void OnLobbyDataChanged(ulong lobbyId)
    {
    }

    private void OnLobbyMemberDataChanged(ulong lobbyId, ulong steamId)
    {
    }

    private void OnLobbyMemberDisconnected(ulong lobbyId, ulong steamId)
    {
        OnLobbyMemberLeave(lobbyId, steamId);
    }

    private void OnLobbyMemberLeave(ulong lobbyId, ulong steamId)
    {
        if (Lobby.Id != lobbyId) return;
        _members.Remove(steamId);
        if (MultiplayerPeer is SteamPeer peer) peer.DisconnectSteamPeer(steamId);
        if (steamId == _host && _host != SteamClient.SteamId) LeaveLobby();
    }

    private void OnLobbyMemberJoined(ulong lobbyId, ulong steamId)
    {
        if (Lobby.Id == lobbyId) _members.Add(steamId);
    }

    private void OnLobbyInvite(ulong lobbyId, ulong steamId)
    {
    }

    private void OnLobbyEntered(ulong lobbyId)
    {
        // 只接管本实例发起的大厅操作，避免旧对象响应其他会话。
        if (_lobbyOperation) Lobby = new Lobby(lobbyId);
    }

    private void OnLobbyLeaved(ulong lobbyId)
    {
        if (Lobby.Id == lobbyId && !_leaving) LeaveLobby();
    }

    private void OnLobbyCreated(int result, ulong lobbyId)
    {
        if (_lobbyOperation && result == (int)Result.OK)
        {
            Lobby = new Lobby(lobbyId);
        }
    }

    /// <summary>
    /// 退出大厅，并关闭MultiplayerPeer
    /// </summary>
    public void LeaveLobby()
    {
        if (_disposed || _leaving) return;
        _leaving = true;
        _generation++;
        try
        {
            ClosePeer();
            if (Lobby.IsValid && SteamClient.IsValid)
            {
                if (SMatchmaking.Lobby.Id == Lobby.Id) SMatchmaking.LeaveLobby();
                else Lobby.Leave();
            }
            Lobby = default;
            _host = default;
            _members.Clear();
        }
        finally { _leaving = false; }
    }

    private void ClosePeer()
    {
        var peer = SceneMultiplayer.MultiplayerPeer;
        SceneMultiplayer.MultiplayerPeer = OfflinePeer;
        if (peer is not null && peer is not OfflineMultiplayerPeer)
        {
            peer.Close();
            if (peer is SteamPeer steamPeer) steamPeer.AdmissionPolicy = null;
        }
    }

    /// <summary>
    /// 创建一个大厅，如果失败则不允许用多人游戏
    /// </summary>
    /// <param name="maxUser"></param>
    /// <exception cref="Exception"></exception>
    public async Task CreateLobbyAsync(int maxUser)
    {
        var generation = BeginLobbyOperation();
        try
        {
            var lobby = await SMatchmaking.CreateLobbyAsync(maxUser);
            if (!lobby.HasValue) throw new InvalidOperationException("Steam 创建大厅失败。");
            if (!AcceptLobbyResult(lobby.Value, generation)) throw new OperationCanceledException("大厅操作已取消。");
            if (!lobby.Value.SetData(SteamPeerProtocol.LobbyVersionKey, SteamPeerProtocol.LobbyVersion))
            {
                LeaveLobby();
                throw new InvalidOperationException("无法写入多人协议版本。");
            }
        }
        finally { _lobbyOperation = false; }
    }

    /// <summary>
    /// 加入好友大厅
    /// </summary>
    /// <param name="friend"></param>
    /// <exception cref="Exception"></exception>
    public async Task JoinLobbyAsync(Friend friend)
    {
        var lobby = friend.GameInfo?.Lobby;
        if (!lobby.HasValue)
        {
            throw new Exception($"{nameof(SteamMultiPlayer)} 未找到大厅");
        }

        await JoinLobbyAsync(lobby.Value);
    }

    /// <summary>
    /// 加入指定大厅
    /// </summary>
    /// <param name="lobby"></param>
    /// <exception cref="Exception"></exception>
    public async Task JoinLobbyAsync(Lobby lobby)
    {
        var generation = BeginLobbyOperation();
        try
        {
            var result = await SMatchmaking.JoinLobbyAsync(lobby);
            if (result != RoomEnter.Success) throw new InvalidOperationException($"加入大厅失败：{result}");
            if (!AcceptLobbyResult(lobby, generation)) throw new OperationCanceledException("大厅操作已取消。");
            if (lobby.GetData(SteamPeerProtocol.LobbyVersionKey) != SteamPeerProtocol.LobbyVersion)
            {
                LeaveLobby();
                throw new InvalidOperationException("Steam 多人协议不兼容，请让两端一起更新插件（SMP2 v2）。");
            }
        }
        finally { _lobbyOperation = false; }
    }

    private int BeginLobbyOperation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_lobbyOperation || Lobby.IsValid) throw new InvalidOperationException("请先完成当前大厅操作并退出已有大厅。");
        _lobbyOperation = true;
        return ++_generation;
    }

    private bool AcceptLobbyResult(Lobby lobby, int generation)
    {
        if (_disposed || generation != _generation)
        {
            if (SteamClient.IsValid)
            {
                if (SMatchmaking.Lobby.Id == lobby.Id) SMatchmaking.LeaveLobby();
                else lobby.Leave();
            }
            return false;
        }
        Lobby = lobby;
        _host = lobby.Owner.Id;
        _members.Clear();
        foreach (var member in lobby.Members) _members.Add(member.Id);
        return true;
    }

    public override void _SetMultiplayerPeer(MultiplayerPeer multiplayerPeer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (multiplayerPeer is null || multiplayerPeer is OfflineMultiplayerPeer)
        {
            ClosePeer();
            return;
        }

        var isServer = multiplayerPeer.GetUniqueId() == SteamPeer.ServerPeerId;
        try
        {
            if (!Lobby.IsValid)
            {
                if (isServer)
                {
                    throw new Exception("请先创建一个大厅");
                }

                throw new Exception("请先加入一个大厅");
            }
            else
            {
                if (isServer && !Lobby.IsOwnedBy(SteamClient.SteamId))
                {
                    throw new Exception("大厅成员不能作为服务端");
                }

                if (!isServer && Lobby.IsOwnedBy(SteamClient.SteamId))
                {
                    throw new Exception("大厅拥有者不能作为客户端");
                }

                if (SceneMultiplayer.MultiplayerPeer == multiplayerPeer) return;
                ClosePeer();
                if (multiplayerPeer is SteamPeer peer)
                {
                    if (!isServer) peer.BindExpectedServer(_host);
                    peer.AdmissionPolicy = _members.Contains;
                }
                SceneMultiplayer.MultiplayerPeer = multiplayerPeer;
            }
        }
        catch (Exception e)
        {
            multiplayerPeer.Close();
            LeaveLobby();
            throw;
        }
    }

    public override MultiplayerPeer _GetMultiplayerPeer()
    {
        return SceneMultiplayer.MultiplayerPeer;
    }

    public override int[] _GetPeerIds()
        => SceneMultiplayer.GetPeers();

    public override int _GetRemoteSenderId()
        => SceneMultiplayer.GetRemoteSenderId();

    public override int _GetUniqueId()
        => SceneMultiplayer.GetUniqueId();

    /// <summary>
    /// 记录配置添加。例如，根路径（nullptr、NodePath），复制（Node、Spawner|Synchronizer），自定义
    /// </summary>
    public override Error _ObjectConfigurationAdd(GodotObject obj, Variant configuration)
    {
        if (configuration.VariantType == Variant.Type.Object && configuration.Obj is MultiplayerSynchronizer synchronizer)
        {
            Log.Debug($"添加用于 {obj} 的同步配置。同步器：{configuration}");
        }
        else if (configuration.VariantType == Variant.Type.Object && configuration.Obj is MultiplayerSpawner spawner)
        {
            Log.Debug($"将节点 {obj} 添加到出生列表。出生器：{configuration}");
        }

        return SceneMultiplayer.ObjectConfigurationAdd(obj, configuration);
    }

    /// <summary>
    /// 记录配置移除。例如，根路径（nullptr、NodePath），复制（Node、Spawner|Synchronizer），自定义。
    /// </summary>
    public override Error _ObjectConfigurationRemove(GodotObject obj, Variant configuration)
    {
        if (configuration.VariantType == Variant.Type.Object && configuration.Obj is MultiplayerSynchronizer)
        {
            Log.Debug($"移除用于 {obj} 的同步配置。同步器：{configuration}");
        }
        else if (configuration.VariantType == Variant.Type.Object && configuration.Obj is MultiplayerSpawner)
        {
            Log.Debug($"将节点 {obj} 移除到出生列表。出生器：{configuration}");
        }

        return SceneMultiplayer.ObjectConfigurationRemove(obj, configuration);
    }

    public override Error _Poll()
    {
        return SceneMultiplayer.Poll();
    }

    /// <summary>
    /// 记录正在进行的 RPC 并将其转发到默认的多人游戏。
    /// </summary>
    public override Error _Rpc(int peer, GodotObject obj, StringName method, Collections.Array args)
        => SceneMultiplayer.Rpc(peer, obj, method, args);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            LeaveLobby();
            _disposed = true;
            if (IsInstanceValid(_matchmaking))
            {
                _matchmaking.LobbyCreated -= OnLobbyCreated;
                _matchmaking.LobbyEntered -= OnLobbyEntered;
                _matchmaking.LobbyLeaved -= OnLobbyLeaved;
                _matchmaking.LobbyInvite -= OnLobbyInvite;
                _matchmaking.LobbyMemberJoined -= OnLobbyMemberJoined;
                _matchmaking.LobbyMemberLeave -= OnLobbyMemberLeave;
                _matchmaking.LobbyMemberDisconnected -= OnLobbyMemberDisconnected;
                _matchmaking.LobbyMemberDataChanged -= OnLobbyMemberDataChanged;
                _matchmaking.LobbyDataChanged -= OnLobbyDataChanged;
                _matchmaking.LobbyChatMessage -= OnLobbyChatMessage;
                _matchmaking.LobbyMemberKick -= OnLobbyMemberKick;
            }
            SceneMultiplayer.PeerConnected -= OnPeerConnected;
            SceneMultiplayer.PeerDisconnected -= OnPeerDisconnected;
            SceneMultiplayer.ServerDisconnected -= OnServerDisconnected;
            SceneMultiplayer.ConnectedToServer -= OnConnectedToServer;
            SceneMultiplayer.ConnectionFailed -= OnConnectionFailed;
            SceneMultiplayer.Dispose();
            OfflinePeer.Dispose();
        }
        base.Dispose(disposing);
    }
}
