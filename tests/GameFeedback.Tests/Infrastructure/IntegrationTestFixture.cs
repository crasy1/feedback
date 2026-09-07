using Testcontainers.PostgreSql;

namespace GameFeedback.Tests.Infrastructure;

/// <summary>
/// 集成测试共享夹具：一个真实 PostgreSQL 容器，按需创建应用工厂
/// （每个工厂可挂独立 Steam 传输桩，限流器也随之隔离）。
/// </summary>
public sealed class IntegrationTestFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();
    private readonly List<GameFeedbackApplicationFactory> _factories = [];

    public GameFeedbackApplicationFactory DefaultFactory { get; private set; } = null!;

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        DefaultFactory = CreateFactory();
    }

    /// <summary>创建一个应用工厂；传入 Steam 传输桩则覆盖名为 "Steam" 的 HttpClient 主处理器。</summary>
    public GameFeedbackApplicationFactory CreateFactory(HttpMessageHandler? steamHandler = null)
    {
        var factory = new GameFeedbackApplicationFactory(ConnectionString, steamHandler);
        _factories.Add(factory);
        return factory;
    }

    public async Task DisposeAsync()
    {
        foreach (var factory in _factories)
        {
            await factory.DisposeAsync();
        }
        await _postgres.DisposeAsync();
    }
}

[CollectionDefinition("Integration")]
public sealed class IntegrationTestCollection : ICollectionFixture<IntegrationTestFixture>;
