using Npgsql;
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

    /// <summary>
    /// 夹具级共享的默认游戏（带可用凭据与 AppID）。多数用例只需要"一个能登录的游戏"，
    /// 而同一个库里的玩家隔离类用例（两个玩家、同一游戏）也必须落在同一个游戏上。
    /// 需要第二个游戏的用例请用 <see cref="SeedGameAsync"/> 另种一个。
    /// </summary>
    public TestGame DefaultGame { get; private set; } = null!;

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        DefaultFactory = CreateFactory();
        DefaultGame = await TestGames.SeedAsync(DefaultFactory);
    }

    /// <summary>创建一个应用工厂；传入 Steam 传输桩则覆盖名为 "Steam" 的 HttpClient 主处理器。</summary>
    public GameFeedbackApplicationFactory CreateFactory(
        HttpMessageHandler? steamHandler = null,
        Action<Dictionary<string, string>>? extraSettings = null,
        string? connectionString = null,
        CapturingLoggerProvider? logCapture = null)
    {
        var settings = new Dictionary<string, string>();
        extraSettings?.Invoke(settings);
        var factory = new GameFeedbackApplicationFactory(
            connectionString ?? ConnectionString, steamHandler, settings.Count > 0 ? settings : null, logCapture);
        _factories.Add(factory);
        return factory;
    }

    /// <summary>在同一个容器里另种一个 Game（默认带凭据与 AppID）。</summary>
    public Task<TestGame> SeedGameAsync(
        string? name = null,
        string? steamAppId = null,
        bool withAppId = true,
        bool withCredential = true,
        bool isActive = true,
        string identity = TestGames.DefaultIdentity,
        string apiKey = TestGames.DefaultApiKey,
        CancellationToken cancellationToken = default) =>
        TestGames.SeedAsync(
            DefaultFactory,
            name,
            steamAppId,
            withAppId,
            withCredential,
            isActive,
            identity,
            apiKey,
            cancellationToken: cancellationToken);

    /// <summary>
    /// 一个"库里一个游戏都没有"的干净数据库上的工厂：用于覆盖首次启动状态
    /// （管理端据此把管理员强制送到「添加游戏」页）。共享库里有夹具的默认游戏，
    /// 所以这个状态只能在另一个新建的空库上观察。
    /// </summary>
    public async Task<GameFeedbackApplicationFactory> CreateFactoryOnEmptyDatabaseAsync(
        HttpMessageHandler? steamHandler = null)
    {
        var databaseName = $"empty_{Guid.NewGuid():N}"[..18];
        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Database = databaseName,
        }.ConnectionString;

        return CreateFactory(steamHandler, connectionString: connectionString);
    }

    public async Task DisposeAsync()
    {
        foreach (var factory in _factories)
        {
            await factory.DisposeAsync();
        }
        await _postgres.DisposeAsync();

        // 清掉本次运行写的 Data Protection key ring（里面是测试专有密钥，不该留在临时目录里）。
        try
        {
            if (Directory.Exists(GameFeedbackApplicationFactory.DataProtectionKeysPath))
            {
                Directory.Delete(GameFeedbackApplicationFactory.DataProtectionKeysPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不影响测试结论。
        }
    }
}

[CollectionDefinition("Integration")]
public sealed class IntegrationTestCollection : ICollectionFixture<IntegrationTestFixture>;
