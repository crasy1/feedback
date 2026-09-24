using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace GameFeedback.Tests.Infrastructure;

/// <summary>
/// 把应用写出的日志行收集起来，供"密钥/票据绝不进日志"这类安全断言使用。
/// 密钥一旦进了日志就等于泄漏（日志会被采集、转发、长期保留），所以这条断言必须盯着真实日志管道，
/// 而不是盯着我们自己的几个 LogInformation 调用点。
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    /// <summary>已捕获的日志行（格式化后的完整文本，含消息与异常）。</summary>
    public IReadOnlyCollection<string> Lines => _lines;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _lines);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            lines.Enqueue($"[{logLevel}] {category}: {message}{(exception is null ? string.Empty : $" | {exception}")}");
        }
    }
}
