#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using Steamworks;
using Steamworks.Data;

namespace Godot;

/// <summary>注入正在运行的场景即可检查；传输完全在内存中，不建立 Steam 连接或发送消息。</summary>
public partial class MultiplayerRuntimeCheck : Node
{
    private int _checks;

    public override void _Ready()
    {
        try
        {
            CheckCodec();
            CheckBudgets();
            CheckRoutingAndMetadata();
            CheckBackendChannels();
            CheckConnectionBoundaries();
            CheckHandshakeBudgets();
            CheckQueueLimits();
            CheckSessionReplacement();
            CheckSceneMultiplayerRelay();
            CheckSubscriptionCleanup();
            GD.Print($"STEAM_MULTIPLAYER_PASS checks={_checks}");
        }
        catch (Exception exception)
        {
            GD.Print($"STEAM_MULTIPLAYER_FAIL checks={_checks}: {exception}");
            GD.PushError(exception.ToString());
        }
    }

    private void CheckCodec()
    {
        var expected = new SteamPeerHeader(SteamPeerMessageKind.Data, MultiplayerPeer.TransferModeEnum.Unreliable, 42, 3, 100, 200);
        var wire = SteamPeerProtocol.Encode(expected, [7, 8]);
        Check(wire.Length == 34 && wire[0] == (byte)'S' && wire[3] == (byte)'2', "版本头与长度");
        Check(SteamPeerProtocol.TryDecode(wire, 4, out var actual) && actual == expected, "协议保留来源、通道、模式和双方会话");
        wire[4] = 1;
        Check(!SteamPeerProtocol.TryDecode(wire, 4, out _), "拒绝旧协议");
        wire[4] = 2;
        Check(!SteamPeerProtocol.TryDecode(wire, 3, out _), "拒绝越界通道");
        Check(!SteamPeerProtocol.TryDecode(wire.AsSpan(0, 10), 4, out _), "拒绝截断消息头");
        Check(!SteamPeerProtocol.TryDecode(SteamPeerProtocol.Encode(expected with { Kind = SteamPeerMessageKind.Hello }), 4, out _),
            "控制包不能伪装为不可靠游戏数据");
    }

    private void CheckBudgets()
    {
        var clock = new MultiplayerTestClock();
        var options = new SteamNetworkOptions { ReceivePacketsPerFrame = 5, ReceiveBatchSize = 2 };
        var pump = new SteamReceivePump(options, clock);
        var order = new List<int>();
        int Read(int channel, int count) { order.Add(channel); return count; }
        Check(pump.Poll(new[] { 0, 1, 2, 3 }, Read) == 5 && order.SequenceEqual(new[] { 0, 1, 2 }), "突发收包不超过硬预算");
        order.Clear();
        Check(pump.Poll(new[] { 0, 1, 2, 3 }, Read) == 5 && order.SequenceEqual(new[] { 3, 0, 1 }), "跨帧轮转不饿死后续通道");
        var emptyReads = 0;
        Check(pump.Poll(new[] { 0, 1, 2 }, (_, _) => { emptyReads++; return 0; }) == 0 && emptyReads == 3, "空队列不忙等");
        Check(pump.Poll(new[] { 0, 1 }, (_, count) => { clock.Ticks += 3; return count; }) == 2, "每批前检查时间预算");
        var invalid = false;
        try { new SteamNetworkOptions { ReceiveBudgetMilliseconds = double.NaN }.Validate(); }
        catch (ArgumentOutOfRangeException) { invalid = true; }
        Check(invalid, "无效配置被拒绝");
    }

    private void CheckRoutingAndMetadata()
    {
        using var bus = new MultiplayerTestBus();
        var server = bus.Create(100, 1);
        var alice = bus.Create(200, 2, 100);
        var bob = bus.Create(300, 3, 100);
        Check(server.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected, "服务器立即就绪");
        Check(alice.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connecting, "客户端等待握手");
        Check(server.TransferMode == MultiplayerPeer.TransferModeEnum.Reliable && server.TransferChannel == 0, "默认传输语义");
        alice.Connect();
        bob.Connect();
        bus.Deliver();
        Check(alice.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected && server.SteamIdToPeerId(200UL) == 2, "握手登记双方 peer");
        Check(alice.SteamIdToPeerId(999UL) == 0, "未知来源不能映射成服务器");

        alice.TransferChannel = 3;
        alice.TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable;
        Check(alice.TransferChannel == 3 && alice.TransferMode == MultiplayerPeer.TransferModeEnum.Unreliable, "原生属性调用 getter/setter 往返");
        alice.SetTargetPeer(1);
        Check(alice.PutPacket([11, 12]) == Error.Ok, "非零通道发送");
        bus.Deliver();
        Check(server.GetPacketPeer() == 2 && server.GetPacketChannel() == 3 && server.GetPacketMode() == MultiplayerPeer.TransferModeEnum.Unreliable,
            "模拟 Facepunch 回调 channel=0 时仍恢复消息元数据");
        Check(server.GetPacket().SequenceEqual(new byte[] { 11, 12 }) && server.QueuedBytes == 0, "返回纯负载并释放队列预算");

        alice.TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered;
        Check(alice.PutPacket([13]) == Error.Ok, "顺序不可靠模式的兼容发送");
        bus.Deliver();
        Check(server.GetPacketMode() == MultiplayerPeer.TransferModeEnum.Reliable, "回退为 Reliable 时接收端如实报告");
        server.GetPacket();
        server.SetTargetPeer(0);
        Check(server.PutPacket([20]) == Error.Ok, "广播发送");
        bus.Deliver();
        Check(alice.GetPacket()[0] == 20 && bob.GetPacket()[0] == 20, "广播覆盖全部成员");
        server.SetTargetPeer(-2);
        server.PutPacket([21]);
        bus.Deliver();
        Check(alice.GetAvailablePacketCount() == 0 && bob.GetPacket()[0] == 21, "排除广播");
        server.SetTargetPeer(2);
        bus.FailedTargets.Add(200);
        Check(server.PutPacket([22]) == Error.Failed, "传递后端发送失败");
        server.SetTargetPeer(0);
        Check(server.PutPacket([23]) == Error.Failed, "广播汇总部分失败");
        bus.Deliver();
        Check(bob.GetPacket()[0] == 23, "部分失败不阻止其余目标发送");
        bus.FailedTargets.Clear();
        server.SetTargetPeer(999);
        Check(server.PutPacket([1]) == Error.InvalidParameter, "拒绝不存在的目标");
        server.SetTargetPeer(2);
        server.TransferChannel = 4;
        Check(server.PutPacket([1]) == Error.InvalidParameter, "拒绝未配置的通道");
        server.TransferChannel = 0;
        Check(server.PutPacket(new byte[SteamPeer.MaxPacketSize + 1]) == Error.InvalidParameter, "限制包长");
        var disconnected = 0;
        server.PeerDisconnected += _ => disconnected++;
        server.DisconnectPeer(2, true);
        Check(disconnected == 0 && server.SteamIdToPeerId(200UL) == 0, "force 不发本地断线信号");
        server.Close();
        server.Close();
        Check(disconnected == 0 && server.CloseCount == 1 && server.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Disconnected,
            "Close 幂等且不发本地断线信号");
        Check(server.PutPacket([1]) == Error.Unconfigured, "关闭后不能发送");
    }

    private void CheckBackendChannels()
    {
        foreach (var type in new[] { SteamSocketType.P2P, SteamSocketType.P2PMessage, SteamSocketType.Normal, SteamSocketType.Relay })
        {
            using var bus = new MultiplayerTestBus();
            var host = bus.Create(100, 1, type: type);
            var client = bus.Create(200, 2, 100, type: type);
            client.Connect();
            bus.Deliver();
            client.TransferChannel = 1;
            client.TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable;
            Check(client.PutPacket([61]) == Error.Ok, $"{type} 模拟后端发送");
            var packet = bus.Pending.Peek();
            Check(packet.Channel == 6, $"{type} 游戏通道不会使用语音通道 1");
            bus.Deliver();
            Check(host.GetPacketMode() == MultiplayerPeer.TransferModeEnum.Unreliable && host.GetPacketChannel() == 1 && host.GetPacket()[0] == 61,
                $"{type} 模拟后端保留传输语义");
            if (type is SteamSocketType.P2P or SteamSocketType.P2PMessage)
            {
                host.ReceiveData(packet.From, 1, packet.Bytes);
                Check(host.GetAvailablePacketCount() == 0, $"{type} 拒绝放入保留通道的数据");
            }
        }
    }

    private void CheckConnectionBoundaries()
    {
        using var bus = new MultiplayerTestBus();
        var server = bus.Create(100, 1);
        var client = bus.Create(200, 2, 100);
        server.AdmissionPolicy = id => id == 200;
        server.ReceiveData(999, 0, SteamPeerProtocol.Encode(new(SteamPeerMessageKind.Hello, MultiplayerPeer.TransferModeEnum.Reliable, 9, 0, 11, 0)));
        Check(server.RejectedPackets == 1 && server.SteamIdToPeerId(999UL) == 0, "拒绝非大厅来源");
        server.RefuseNewConnections = true;
        client.Connect();
        bus.Deliver();
        Check(client.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connecting, "拒绝新连接时不完成握手");
        bus.Clock.Ticks = 10001;
        client.Poll();
        Check(client.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Disconnected && client.TransportCloses > 0, "握手超时清理传输");
        var replacement = bus.Create(200, 2, 100);
        server.RefuseNewConnections = false;
        replacement.Connect();
        // 跨 lane 到达顺序：数据包可以先于 ACK，但必须回显有效的服务器会话。
        bus.DeliverOne();
        bus.DeliverOne();
        var ack = bus.Pending.Dequeue();
        replacement.PutPacket([31]);
        bus.Deliver();
        Check(server.SteamIdToPeerId(200UL) == 2 && server.GetPacket()[0] == 31, "会话回显数据可完成 ACK，可靠包不因跨 lane 排序丢失");
        server.ReceiveData(ack.From, 0, ack.Bytes);
        server.BanPeer(200);
        var banned = bus.Create(200, 2, 100);
        banned.Connect();
        bus.Deliver();
        Check(server.SteamIdToPeerId(200UL) == 0 && banned.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Connected, "踢人后禁止重新握手");
    }

    private void CheckHandshakeBudgets()
    {
        using var bus = new MultiplayerTestBus();
        var host = bus.Create(100, 1, options: new() { MaxPendingConnections = 1, MaxConnections = 4 });
        Check(host.BeginTransportHandshake(200), "native 接入即占用握手预算");
        Check(!host.BeginTransportHandshake(300), "未发送 Hello 也受待握手上限限制");
        bus.Clock.Ticks = 10001;
        host.Poll();
        Check(host.TransportCloses == 1 && host.BeginTransportHandshake(300), "没有 Hello 的连接超时释放额度");
        host.OnSocketDisconnected(300);
        var alice = bus.Create(200, 2, 100);
        alice.Connect();
        var hello = bus.Pending.Peek();
        bus.DeliverOne();
        bus.Pending.Clear(); // 模拟对端不回复 Welcome。
        bus.Clock.Ticks += 9000;
        host.ReceiveData(hello.From, 0, hello.Bytes);
        bus.Pending.Clear();
        bus.Clock.Ticks += 1001;
        host.Poll();
        Check(host.TransportCloses == 2, "重复 Hello 不延长截止时间");
        var fresh = bus.Create(200, 2, 100);
        fresh.Connect();
        bus.Deliver();
        var bob = bus.Create(300, 3, 100);
        bob.Connect();
        bus.Deliver();
        Check(host.SteamIdToPeerId(200UL) == 2 && host.SteamIdToPeerId(300UL) == 3,
            "已完成连接不占待握手额度");
        Check(host.BeginTransportHandshake(200, replacingConnection: true) && host.SteamIdToPeerId(200UL) == 0,
            "新的 native 连接替换旧绑定后必须重新握手");
        bus.Clock.Ticks += 10001;
        host.Poll();
        Check(host.TransportCloses == 3 && host.BeginTransportHandshake(400), "替换 native 连接不发送 Hello 也会超时");
    }

    private void CheckQueueLimits()
    {
        using var bus = new MultiplayerTestBus();
        var server = bus.Create(100, 1, options: new() { MaxQueuedPackets = 1, MaxQueuedBytes = 2 });
        var client = bus.Create(200, 2, 100);
        client.Connect();
        bus.Deliver();
        client.TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable;
        client.PutPacket([1, 2]);
        client.PutPacket([3, 4]);
        bus.Deliver();
        Check(server.QueuedBytes == 2 && server.DroppedUnreliablePackets == 1 && server.GetAvailablePacketCount() == 1, "不可靠队列受包数和字节上限约束");
        client.TransferMode = MultiplayerPeer.TransferModeEnum.Reliable;
        client.PutPacket([5]);
        bus.Deliver();
        Check(server.SteamIdToPeerId(200UL) == 0 && server.QueuedBytes == 0, "可靠队列溢出显式断开并清理旧包");
    }

    private void CheckSessionReplacement()
    {
        using var bus = new MultiplayerTestBus();
        var server = bus.Create(100, 1);
        var old = bus.Create(200, 2, 100);
        old.Connect();
        var oldHello = bus.Pending.Peek();
        bus.Deliver();
        old.PutPacket([90]);
        var oldData = bus.Pending.Dequeue();
        var fresh = bus.Create(200, 2, 100);
        fresh.Connect();
        bus.Deliver();
        var rejected = server.RejectedPackets;
        server.ReceiveData(oldHello.From, 0, oldHello.Bytes);
        server.ReceiveData(oldData.From, 0, oldData.Bytes);
        Check(server.RejectedPackets == rejected + 2 && server.GetAvailablePacketCount() == 0, "新会话拒绝旧 HELLO 和旧数据");
        fresh.PutPacket([91]);
        bus.Deliver();
        Check(server.GetPacket()[0] == 91, "快速重连后的新会话正常工作");
    }

    private void CheckSceneMultiplayerRelay()
    {
        using var bus = new MultiplayerTestBus();
        var host = bus.Create(100, 1);
        var alice = bus.Create(200, 2, 100);
        var bob = bus.Create(300, 3, 100);
        using var hostApi = new SceneMultiplayer { RootPath = GetPath(), MultiplayerPeer = host };
        using var aliceApi = new SceneMultiplayer { RootPath = GetPath(), MultiplayerPeer = alice };
        using var bobApi = new SceneMultiplayer { RootPath = GetPath(), MultiplayerPeer = bob };
        var received = new List<(long, byte[])>();
        bobApi.PeerPacket += (sender, bytes) => received.Add((sender, bytes));
        alice.Connect();
        bob.Connect();
        void PollAll()
        {
            for (var i = 0; i < 8; i++)
            {
                bus.Deliver();
                hostApi.Poll();
                aliceApi.Poll();
                bobApi.Poll();
            }
        }
        PollAll();
        Check(aliceApi.GetPeers().Contains(3) && bobApi.GetPeers().Contains(2), "真实 SceneMultiplayer 完成三人发现");
        Check(aliceApi.SendBytes([41, 42], 3, MultiplayerPeer.TransferModeEnum.Unreliable, 2) == Error.Ok, "SceneMultiplayer 发起跨客户端消息");
        PollAll();
        Check(received.Count == 1 && received[0].Item1 == 2 && received[0].Item2.SequenceEqual(new byte[] { 41, 42 }),
            "A 经服务器转发到 B，保留原发送者和负载");
    }

    private void CheckSubscriptionCleanup()
    {
        var matchmaking = SMatchmaking.Instance;
        // 自定义 C# 信号保存在生成的委托字段中，不出现在原生 GetSignalConnectionList 里。
        var subscribers = typeof(SMatchmaking).GetField("backing_LobbyMemberKick",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Delegate[] Subscribers() => ((Delegate?)subscribers.GetValue(matchmaking))?.GetInvocationList() ?? [];
        var before = Subscribers().Length;
        for (var i = 0; i < 5; i++)
        {
            using var session = new SteamMultiPlayer();
            var current = Subscribers();
            Check(current.Length == before + 1 && current.Any(callback => ReferenceEquals(callback.Target, session)), "注册单个会话订阅");
            Check(session.ObjectConfigurationAdd(null!, GetPath()) == Error.Ok, "正确转发 SceneTree 根路径配置");
        }
        Check(Subscribers().Length == before, "反复创建释放后没有残留订阅");
    }

    private void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        _checks++;
        GD.Print($"STEAM_MULTIPLAYER_CHECK {_checks}: {description}");
    }
}

internal sealed class MultiplayerTestClock : TimeProvider
{
    public long Ticks;
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => Ticks;
}

internal readonly record struct MultiplayerTestDatagram(ulong From, ulong To, byte[] Bytes, int Channel);

internal sealed class MultiplayerTestBus : IDisposable
{
    public readonly MultiplayerTestClock Clock = new();
    public readonly Queue<MultiplayerTestDatagram> Pending = new();
    public readonly HashSet<ulong> FailedTargets = new();
    private readonly Dictionary<ulong, MultiplayerTestPeer> _peers = new();
    private readonly List<MultiplayerTestPeer> _owned = new();

    public MultiplayerTestPeer Create(ulong steamId, int peerId, ulong server = 0, SteamNetworkOptions? options = null,
        SteamSocketType type = SteamSocketType.Normal)
    {
        var peer = new MultiplayerTestPeer(this, steamId, peerId, server, options ?? new(), type);
        _peers[steamId] = peer;
        _owned.Add(peer);
        return peer;
    }

    public bool Send(MultiplayerTestDatagram packet)
    {
        if (FailedTargets.Contains(packet.To)) return false;
        Pending.Enqueue(packet);
        return true;
    }

    public void DeliverOne()
    {
        var packet = Pending.Dequeue();
        if (_peers.TryGetValue(packet.To, out var peer))
            peer.ReceiveData(packet.From, peer.BackendType is SteamSocketType.P2P or SteamSocketType.P2PMessage ? packet.Channel : 0, packet.Bytes);
    }

    public void Deliver()
    {
        var count = 0;
        while (Pending.Count > 0)
        {
            if (++count > 4096) throw new InvalidOperationException("控制消息循环");
            DeliverOne();
        }
    }

    public void Dispose()
    {
        foreach (var peer in _owned) peer.Dispose();
        Pending.Clear();
    }
}

internal partial class MultiplayerTestPeer : SteamPeer
{
    private readonly MultiplayerTestBus _bus;
    private readonly ulong _steamId;
    public int CloseCount { get; private set; }
    public int TransportCloses { get; private set; }
    public SteamSocketType BackendType => SteamSocketType;

    public MultiplayerTestPeer(MultiplayerTestBus bus, ulong steamId, int peerId, ulong server, SteamNetworkOptions options,
        SteamSocketType type)
        : base(peerId, type, options, server, bus.Clock)
    {
        _bus = bus;
        _steamId = steamId;
        Activate();
    }

    public void Connect() => OnSocketConnected(ExpectedServer);
    protected override void OnCreate() { }
    protected override void OnClose() => CloseCount++;
    protected override void Receive() { }
    protected override void DisconnectTransport(SteamId steamId, bool force) => TransportCloses++;
    protected override bool SendMsg(SteamId steamId, byte[] data, Channel channel = Channel.Msg, SendType sendType = SendType.Reliable) =>
        _bus.Send(new MultiplayerTestDatagram(_steamId, steamId, data, (int)channel));
}
#endif
