using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Steamworks;
using Steamworks.Data;

namespace Godot;

/// <summary>Steam 传输上的 Godot MultiplayerPeer：统一握手、路由、队列和关闭语义。</summary>
public abstract partial class SteamPeer : MultiplayerPeerExtension
{
    public const int ServerPeerId = 1;
    public const int MaxPacketSize = 512 * 1024 - SteamPeerProtocol.HeaderSize;
    protected int TargetPeer { get; private set; }
    protected int PeerId { get; }
    protected SteamSocketType SteamSocketType { get; }
    protected SteamNetworkOptions Options { get; }
    protected bool IsActive { get; private set; }
    protected readonly Queue<SteamPacket> PacketQueue = new();
    protected readonly Dictionary<int, SteamId> ConnectedPeers = new();
    public ConnectionStatus ConnectionStatus { get; private set; } = ConnectionStatus.Disconnected;
    public SteamId ExpectedServer { get; private set; }
    public Func<SteamId, bool>? AdmissionPolicy { get; set; }
    public long SentBytes { get; private set; }
    public long ReceivedBytes { get; private set; }
    public long SentPackets { get; private set; }
    public long ReceivedPackets { get; private set; }
    public long SendFailures { get; private set; }
    public long RejectedPackets { get; private set; }
    public long DroppedUnreliablePackets { get; private set; }
    public int QueuedBytes { get; private set; }
    public int QueueHighWaterBytes { get; private set; }

    private readonly ulong _session;
    private readonly TimeProvider _clock;
    private readonly Dictionary<SteamId, Binding> _bindings = new();
    private readonly Dictionary<SteamId, PendingPeer> _pending = new();
    private readonly HashSet<SteamId> _blocked = new();
    private readonly HashSet<(SteamId, ulong)> _retired = new();
    private readonly Queue<(SteamId, ulong)> _retiredOrder = new();
    private readonly List<SteamId> _expired = new();
    private TransferModeEnum _transferMode = TransferModeEnum.Reliable;
    private int _transferChannel;
    private long _connectStarted;
    private bool _closed;

    private readonly record struct Binding(int PeerId, ulong Session);
    private readonly record struct PendingPeer(int PeerId, ulong Session, long Started);

    protected SteamPeer(int peerId, SteamSocketType socketType, SteamNetworkOptions? options = null,
        SteamId expectedServer = default, TimeProvider? clock = null)
    {
        PeerId = peerId == 0 ? checked((int)GenerateUniqueId()) : peerId;
        if (PeerId < 1) throw new ArgumentOutOfRangeException(nameof(peerId));
        Options = options ?? SteamConfig.NetworkOptions;
        Options.Validate();
        SteamSocketType = socketType;
        ExpectedServer = expectedServer;
        _clock = clock ?? TimeProvider.System;
        do { _session = BitConverter.ToUInt64(System.Security.Cryptography.RandomNumberGenerator.GetBytes(sizeof(ulong))); } while (_session == 0);
    }

    // 在派生类的传输对象创建成功后启动，避免基类构造器访问未初始化的派生状态。
    protected void Activate()
    {
        if (_closed || IsActive) return;
        OnCreate();
        IsActive = true;
        _connectStarted = _clock.GetTimestamp();
        ConnectionStatus = _IsServer() ? ConnectionStatus.Connected : ConnectionStatus.Connecting;
    }

    protected abstract void OnCreate();
    protected abstract void OnClose();
    protected abstract void Receive();
    protected abstract void DisconnectTransport(SteamId steamId, bool force);
    protected abstract bool SendMsg(SteamId steamId, byte[] data, Channel channel = Channel.Msg,
        SendType sendType = SendType.Reliable);

    internal void BindExpectedServer(SteamId steamId)
    {
        if (_IsServer()) return;
        if (ExpectedServer != 0 && ExpectedServer != steamId)
            throw new InvalidOperationException("Steam 连接目标与大厅房主不一致。");
        ExpectedServer = steamId;
    }

    public int SteamIdToPeerId(ulong steamId) => SteamIdToPeerId((SteamId)steamId);
    public int SteamIdToPeerId(SteamId steamId) => _bindings.TryGetValue(steamId, out var binding) ? binding.PeerId : 0;

    public bool CanAcceptConnection(SteamId steamId) => IsActive && steamId != 0 && !_blocked.Contains(steamId) &&
        (AdmissionPolicy?.Invoke(steamId) ?? true) &&
        (_IsServer() ? _bindings.ContainsKey(steamId) || (!RefuseNewConnections && _bindings.Count < Options.MaxConnections)
            : ExpectedServer == 0 || ExpectedServer == steamId);

    internal bool BeginTransportHandshake(SteamId steamId, bool replacingConnection = false)
    {
        if (!CanAcceptConnection(steamId)) return false;
        if (_IsServer() && replacingConnection)
        {
            if (!_pending.ContainsKey(steamId) && _pending.Count >= Options.MaxPendingConnections) return false;
            if (_bindings.TryGetValue(steamId, out var old)) RemovePeer(old.PeerId, false, false);
            if (!IsActive) return false;
            if (_pending.Remove(steamId, out var previous)) Retire(steamId, previous.Session);
        }
        if (!_IsServer() || _bindings.ContainsKey(steamId) || _pending.ContainsKey(steamId)) return true;
        if (_pending.Count >= Options.MaxPendingConnections) return false;
        _pending.Add(steamId, new PendingPeer(0, 0, _clock.GetTimestamp()));
        return true;
    }

    public void OnSocketConnected(ulong steamId)
    {
        if (!BeginTransportHandshake(steamId))
        {
            DisconnectTransport(steamId, true);
            return;
        }
        if (!_IsServer())
        {
            BindExpectedServer(steamId);
            if (!SendControl(steamId, SteamPeerMessageKind.Hello, 0)) _Close();
        }
    }

    public void OnSocketDisconnected(ulong steamId)
    {
        if (!IsActive) return;
        _pending.Remove(steamId);
        if (_bindings.TryGetValue(steamId, out var binding))
            RemovePeer(binding.PeerId, false, false);
        else if (!_IsServer() && ExpectedServer == steamId)
            _Close();
    }

    public void BanPeer(SteamId steamId)
    {
        _blocked.Add(steamId);
        _pending.Remove(steamId);
        if (_bindings.TryGetValue(steamId, out var binding)) _DisconnectPeer(binding.PeerId, false);
        else DisconnectTransport(steamId, true);
    }

    public void DisconnectSteamPeer(SteamId steamId, bool force = false)
    {
        if (_bindings.TryGetValue(steamId, out var binding)) RemovePeer(binding.PeerId, force, true);
        else
        {
            _pending.Remove(steamId);
            DisconnectTransport(steamId, force);
            if (!_IsServer() && steamId == ExpectedServer) _Close();
        }
    }

    public virtual void ReceiveData(ulong steamId, int channel, byte[] data)
    {
        if (!IsActive || _blocked.Contains(steamId) || (AdmissionPolicy is not null && !AdmissionPolicy(steamId)) ||
            (!_IsServer() && ExpectedServer != steamId) || data is null || data.Length > MaxPacketSize + SteamPeerProtocol.HeaderSize ||
            !SteamPeerProtocol.TryDecode(data, Options.ChannelCount, out var header))
        {
            RejectedPackets++;
            return;
        }

        if (SteamSocketType is SteamSocketType.P2P or SteamSocketType.P2PMessage &&
            channel != (header.Kind == SteamPeerMessageKind.Data
                ? SteamPeerProtocol.TransportChannel(header.Channel, header.Mode) : (int)Channel.Handshake))
        {
            RejectedPackets++;
            return;
        }

        if (header.Kind == SteamPeerMessageKind.Hello)
        {
            HandleHello(steamId, header);
            return;
        }
        if (header.RecipientSession != _session)
        {
            RejectedPackets++;
            return;
        }
        if (header.Kind == SteamPeerMessageKind.Welcome)
        {
            HandleWelcome(steamId, header);
            return;
        }

        // 数据 lane 可能先于 ACK 到达；回显双方会话标识也证明 WELCOME 已收到。
        if (_IsServer() && (header.Kind is SteamPeerMessageKind.Acknowledge or SteamPeerMessageKind.Data) &&
            _pending.TryGetValue(steamId, out var pending) && pending.PeerId == header.PeerId && pending.Session == header.SenderSession)
        {
            if (!Promote(steamId, new Binding(header.PeerId, header.SenderSession))) return;
        }

        if (!_bindings.TryGetValue(steamId, out var peer) || peer.PeerId != header.PeerId || peer.Session != header.SenderSession)
        {
            RejectedPackets++;
            return;
        }
        if (header.Kind == SteamPeerMessageKind.Disconnect)
            RemovePeer(peer.PeerId, false, true);
        else if (header.Kind == SteamPeerMessageKind.Data)
            QueueData(steamId, header, data);
    }

    private void HandleHello(SteamId steamId, SteamPeerHeader header)
    {
        if (!_IsServer() || header.PeerId == ServerPeerId || !CanAcceptConnection(steamId) ||
            _retired.Contains((steamId, header.SenderSession)) ||
            (ConnectedPeers.TryGetValue(header.PeerId, out var owner) && owner != steamId))
        {
            RejectedPackets++;
            return;
        }
        if (_bindings.TryGetValue(steamId, out var current))
        {
            if (current.Session == header.SenderSession && current.PeerId == header.PeerId)
            {
                SendControl(steamId, SteamPeerMessageKind.Welcome, header.SenderSession);
                return;
            }
            RemovePeer(current.PeerId, false, false);
            if (!IsActive) return;
        }
        if (!_pending.ContainsKey(steamId) && _pending.Count >= Options.MaxPendingConnections)
        {
            RejectedPackets++;
            return;
        }
        var started = _clock.GetTimestamp();
        if (_pending.TryGetValue(steamId, out var previous))
        {
            started = previous.Started;
            if (previous.Session == header.SenderSession && previous.PeerId != header.PeerId)
            {
                RejectedPackets++;
                return;
            }
            if (previous.Session != header.SenderSession) Retire(steamId, previous.Session);
        }
        _pending[steamId] = new PendingPeer(header.PeerId, header.SenderSession, started);
        if (!SendControl(steamId, SteamPeerMessageKind.Welcome, header.SenderSession))
        {
            _pending.Remove(steamId);
            DisconnectTransport(steamId, true);
        }
    }

    private void HandleWelcome(SteamId steamId, SteamPeerHeader header)
    {
        if (_IsServer() || header.PeerId != ServerPeerId || steamId != ExpectedServer)
        {
            RejectedPackets++;
            return;
        }
        if (_bindings.TryGetValue(steamId, out var existing))
        {
            if (existing.Session == header.SenderSession) SendControl(steamId, SteamPeerMessageKind.Acknowledge, existing.Session);
            else RejectedPackets++;
            return;
        }
        _bindings.Add(steamId, new Binding(ServerPeerId, header.SenderSession));
        ConnectedPeers.Add(ServerPeerId, steamId);
        ConnectionStatus = ConnectionStatus.Connected;
        if (!SendControl(steamId, SteamPeerMessageKind.Acknowledge, header.SenderSession))
        {
            _Close();
            return;
        }
        if (IsActive) EmitSignalPeerConnected(ServerPeerId);
    }

    private bool Promote(SteamId steamId, Binding binding)
    {
        if (!CanAcceptConnection(steamId) || (ConnectedPeers.TryGetValue(binding.PeerId, out var existing) && existing != steamId))
        {
            RejectedPackets++;
            return false;
        }
        _pending.Remove(steamId);
        _bindings[steamId] = binding;
        ConnectedPeers[binding.PeerId] = steamId;
        EmitSignalPeerConnected(binding.PeerId);
        return IsActive && _bindings.TryGetValue(steamId, out var current) && current == binding;
    }

    private void QueueData(SteamId steamId, SteamPeerHeader header, byte[] wireData)
    {
        var size = wireData.Length - SteamPeerProtocol.HeaderSize;
        if (PacketQueue.Count >= Options.MaxQueuedPackets || size > Options.MaxQueuedBytes - QueuedBytes)
        {
            if (header.Mode == TransferModeEnum.Unreliable) DroppedUnreliablePackets++;
            else
            {
                Log.Error($"[steam] peer {header.PeerId} 的可靠消息队列超过预算，关闭该连接。");
                _DisconnectPeer(header.PeerId, false);
            }
            return;
        }
        PacketQueue.Enqueue(new SteamPacket
        {
            SteamId = steamId, PeerId = header.PeerId, TransferChannel = header.Channel,
            TransferMode = header.Mode, Data = wireData.AsSpan(SteamPeerProtocol.HeaderSize).ToArray()
        });
        QueuedBytes += size;
        QueueHighWaterBytes = Math.Max(QueueHighWaterBytes, QueuedBytes);
        ReceivedBytes += wireData.Length;
        ReceivedPackets++;
    }

    private bool SendControl(SteamId steamId, SteamPeerMessageKind kind, ulong recipientSession)
    {
        var bytes = SteamPeerProtocol.Encode(new(kind, TransferModeEnum.Reliable, PeerId, 0, _session, recipientSession));
        try { return SendMsg(steamId, bytes, Channel.Handshake, SendType.Reliable); }
        catch (Exception exception) { Log.Error("[steam] 控制消息发送失败", exception); return false; }
    }

    public override void _Close()
    {
        if (_closed) return;
        _closed = true;
        IsActive = false;
        ConnectionStatus = ConnectionStatus.Disconnected;
        var connections = _bindings.Keys.Concat(_pending.Keys).Append(ExpectedServer).Where(id => id != 0).Distinct().ToArray();
        foreach (var (steamId, binding) in _bindings) SendControl(steamId, SteamPeerMessageKind.Disconnect, binding.Session);
        _bindings.Clear();
        _pending.Clear();
        ConnectedPeers.Clear();
        PacketQueue.Clear();
        QueuedBytes = 0;
        foreach (var steamId in connections)
        {
            try { DisconnectTransport(steamId, true); }
            catch (Exception exception) { Log.Error("[steam] 关闭连接失败", exception); }
        }
        try { OnClose(); }
        catch (Exception exception) { Log.Error("[steam] 关闭传输失败", exception); }
    }

    public override void _DisconnectPeer(int pPeer, bool pForce) => RemovePeer(pPeer, pForce, true);

    private void RemovePeer(int peerId, bool force, bool closeTransport)
    {
        if (!ConnectedPeers.Remove(peerId, out var steamId) || !_bindings.Remove(steamId, out var binding)) return;
        Retire(steamId, binding.Session);
        _pending.Remove(steamId);
        var count = PacketQueue.Count;
        for (var i = 0; i < count; i++)
        {
            var packet = PacketQueue.Dequeue();
            if (packet.PeerId == peerId) QueuedBytes -= packet.Data.Length;
            else PacketQueue.Enqueue(packet);
        }
        if (!_IsServer()) ConnectionStatus = ConnectionStatus.Disconnected;
        if (closeTransport)
        {
            SendControl(steamId, SteamPeerMessageKind.Disconnect, binding.Session);
            try { DisconnectTransport(steamId, force); }
            catch (Exception exception) { Log.Error("[steam] 断开连接失败", exception); }
        }
        if (!force && IsActive) EmitSignalPeerDisconnected(peerId);
        if (!_IsServer()) _Close();
    }

    private void Retire(SteamId steamId, ulong session)
    {
        if (session == 0) return;
        if (!_retired.Add((steamId, session))) return;
        _retiredOrder.Enqueue((steamId, session));
        while (_retiredOrder.Count > 256) _retired.Remove(_retiredOrder.Dequeue());
    }

    public override int _GetAvailablePacketCount() => PacketQueue.Count;
    public override ConnectionStatus _GetConnectionStatus() => ConnectionStatus;
    public override int _GetMaxPacketSize() => MaxPacketSize;
    public override int _GetPacketChannel() => PacketQueue.TryPeek(out var packet) ? packet.TransferChannel : 0;
    public override TransferModeEnum _GetPacketMode() => PacketQueue.TryPeek(out var packet) ? packet.TransferMode : TransferModeEnum.Reliable;
    public override int _GetPacketPeer() => PacketQueue.TryPeek(out var packet) ? packet.PeerId : 0;
    public override int _GetUniqueId() => PeerId;
    public override bool _IsServer() => PeerId == ServerPeerId;
    public override bool _IsServerRelaySupported() => true;
    public override void _SetTargetPeer(int pPeer) => TargetPeer = pPeer;
    public override void _SetTransferChannel(int pChannel) => _transferChannel = pChannel;
    public override int _GetTransferChannel() => _transferChannel;
    public override void _SetTransferMode(TransferModeEnum pMode) => _transferMode = pMode;
    public override TransferModeEnum _GetTransferMode() => _transferMode;

    public override void _Poll()
    {
        if (!IsActive) return;
        Receive();
        if (!IsActive) return;
        if (!_IsServer() && ConnectionStatus == ConnectionStatus.Connecting &&
            _clock.GetElapsedTime(_connectStarted).TotalSeconds >= Options.HandshakeTimeoutSeconds)
        {
            Log.Error("[steam] 多人握手超时；两端须使用 SMP2 协议版本 2。");
            _Close();
            return;
        }
        _expired.Clear();
        foreach (var (steamId, pending) in _pending)
            if (_clock.GetElapsedTime(pending.Started).TotalSeconds >= Options.HandshakeTimeoutSeconds) _expired.Add(steamId);
        foreach (var steamId in _expired)
        {
            if (!_pending.Remove(steamId, out var pending)) continue;
            Retire(steamId, pending.Session);
            DisconnectTransport(steamId, true);
        }
    }

    public override byte[] _GetPacketScript()
    {
        if (!PacketQueue.TryDequeue(out var packet)) return [];
        QueuedBytes -= packet.Data.Length;
        return packet.Data;
    }

    public override Error _PutPacketScript(byte[] pBuffer)
    {
        if (!IsActive || ConnectionStatus != ConnectionStatus.Connected) return SendError(Error.Unconfigured);
        if (pBuffer is null || pBuffer.Length == 0 || pBuffer.Length > MaxPacketSize ||
            _transferChannel < 0 || _transferChannel >= Options.ChannelCount ||
            _transferMode is < TransferModeEnum.Unreliable or > TransferModeEnum.Reliable || TargetPeer == int.MinValue)
            return SendError(Error.InvalidParameter);
        if (TargetPeer != 0 && !ConnectedPeers.ContainsKey(Math.Abs(TargetPeer))) return SendError(Error.InvalidParameter);
        var mode = SteamPeerProtocol.EffectiveMode(_transferMode);
        if (TargetPeer > 0)
            return SendPacket(ConnectedPeers[TargetPeer], pBuffer, mode) ? Error.Ok : SendError(Error.Failed);
        var success = true;
        foreach (var (peerId, steamId) in ConnectedPeers)
        {
            if (TargetPeer < 0 && peerId == -TargetPeer) continue;
            success &= SendPacket(steamId, pBuffer, mode);
        }
        return success ? Error.Ok : SendError(Error.Failed);
    }

    private bool SendPacket(SteamId steamId, byte[] payload, TransferModeEnum mode)
    {
        if (!_bindings.TryGetValue(steamId, out var binding)) return false;
        var bytes = SteamPeerProtocol.Encode(new(SteamPeerMessageKind.Data, mode, PeerId, _transferChannel, _session, binding.Session), payload);
        try
        {
            if (!SendMsg(steamId, bytes, (Channel)SteamPeerProtocol.TransportChannel(_transferChannel, mode),
                    mode == TransferModeEnum.Unreliable ? SendType.Unreliable : SendType.Reliable)) return false;
            SentBytes += bytes.Length;
            SentPackets++;
            return true;
        }
        catch (Exception exception) { Log.Error("[steam] 发送数据包失败", exception); return false; }
    }

    private Error SendError(Error error) { SendFailures++; return error; }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _Close();
        base.Dispose(disposing);
    }
}
