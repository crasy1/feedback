using System;
using System.Collections.Generic;

namespace Godot;

/// <summary>按通道轮转读取，包数是硬上限，时间预算在每批调用前检查。</summary>
internal sealed class SteamReceivePump(SteamNetworkOptions options, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private int _nextChannel;

    public int Poll(IReadOnlyList<int> channels, Func<int, int, int> readBatch)
    {
        var started = _clock.GetTimestamp();
        var received = 0;
        var emptyChannels = 0;
        while (channels.Count > 0 && received < options.ReceivePacketsPerFrame && emptyChannels < channels.Count)
        {
            if (_clock.GetElapsedTime(started).TotalMilliseconds >= options.ReceiveBudgetMilliseconds)
            {
                break;
            }

            _nextChannel %= channels.Count;
            var channel = channels[_nextChannel];
            _nextChannel = (_nextChannel + 1) % channels.Count;
            var requested = Math.Min(options.ReceiveBatchSize, options.ReceivePacketsPerFrame - received);
            var count = readBatch(channel, requested);
            if (count < 0 || count > requested)
            {
                throw new InvalidOperationException("Steam 接收后端返回了无效的包数。");
            }

            received += count;
            emptyChannels = count == 0 ? emptyChannels + 1 : 0;
        }

        return received;
    }
}
