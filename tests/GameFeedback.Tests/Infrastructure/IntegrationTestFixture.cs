using Testcontainers.PostgreSql;

namespace GameFeedback.Tests.Infrastructure;

/// <summary>
/// 集成测试共享夹具：一个真实 PostgreSQL 容器 + 一个完整应用实例，
/// 整个测试集合复用，容器内数据库即测试数据库。
/// </summary>
public sealed class IntegrationTestFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();

    public GameFeedbackApplicationFactory Factory { get; private set; } = null!;

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        Factory = new GameFeedbackApplicationFactory(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

[CollectionDefinition("Integration")]
public sealed class IntegrationTestCollection : ICollectionFixture<IntegrationTestFixture>;
