using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameFeedback.Data;
using GameFeedback.Services;
using GameFeedback.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameFeedback.Tests;

/// <summary>
/// Steam 凭据的管理端服务。安全约束是这里的重点：密钥只进不出——
/// 明文绝不进任何返回值、DTO、日志，密文也只在库里。
/// </summary>
[Collection("Integration")]
public sealed class SteamCredentialServiceTests(IntegrationTestFixture fixture)
{
    private async Task<T> GetServiceAsync<T>(GameFeedbackApplicationFactory? factory = null)
        where T : notnull
    {
        var target = factory ?? fixture.DefaultFactory;
        _ = target.CreateClient();
        return target.Services.CreateScope().ServiceProvider.GetRequiredService<T>();
    }

    // ------------------------------------------------------- 增删改

    [Fact]
    public async Task Create_stores_the_key_encrypted_and_reports_no_key_material()
    {
        var apiKey = $"plain-key-{Guid.NewGuid():N}";
        var service = await GetServiceAsync<SteamCredentialService>();

        var result = await service.CreateAsync("发行商密钥", apiKey, CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        var view = result.Credential!;
        Assert.True(view.Id > 0);
        Assert.Equal("发行商密钥", view.Name);
        Assert.Equal(0, view.GameCount);

        // 明文绝不回显，密文也不带出服务层。
        var json = JsonSerializer.Serialize(view);
        Assert.DoesNotContain(apiKey, json);
        Assert.DoesNotContain("EncryptedApiKey", json);
    }

    [Fact]
    public async Task List_and_Get_return_no_key_material()
    {
        var apiKey = $"plain-key-{Guid.NewGuid():N}";
        var created = await TestCredentials.CreateAsync(fixture, apiKey: apiKey);
        var service = await GetServiceAsync<SteamCredentialService>();

        var list = await service.ListAsync(CancellationToken.None);
        var single = await service.GetAsync(created.Id, CancellationToken.None);

        Assert.Contains(list, c => c.Id == created.Id);
        Assert.NotNull(single);
        Assert.DoesNotContain(apiKey, JsonSerializer.Serialize(list));
        Assert.DoesNotContain(apiKey, JsonSerializer.Serialize(single));
        Assert.Null(await service.GetAsync(999_999, CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_rejects_a_missing_name(string? name)
    {
        var service = await GetServiceAsync<SteamCredentialService>();

        var result = await service.CreateAsync(name, "some-key", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.Credential);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_rejects_a_missing_key(string? apiKey)
    {
        var service = await GetServiceAsync<SteamCredentialService>();

        var result = await service.CreateAsync("没有密钥", apiKey, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.Credential);
    }

    /// <summary>粘贴时带进换行的密钥看起来和"Steam 挂了"一模一样，必须在入口就拦掉。</summary>
    [Fact]
    public async Task Create_rejects_a_key_with_whitespace()
    {
        var service = await GetServiceAsync<SteamCredentialService>();

        var result = await service.CreateAsync("换行密钥", "abc\ndef", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.Credential);
    }

    [Fact]
    public async Task Create_rejects_an_over_long_key()
    {
        var service = await GetServiceAsync<SteamCredentialService>();

        var result = await service.CreateAsync("太长", new string('k', 129), CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    /// <summary>apiKey 留空表示只改名字，必须保留原密钥（否则编辑名字等于把密钥擦掉）。</summary>
    [Fact]
    public async Task Update_with_empty_key_keeps_the_existing_key()
    {
        var apiKey = $"plain-key-{Guid.NewGuid():N}";
        var created = await TestCredentials.CreateAsync(fixture, apiKey: apiKey);
        var service = await GetServiceAsync<SteamCredentialService>();

        var result = await service.UpdateAsync(created.Id, "改名了", apiKey: "", CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("改名了", result.Credential!.Name);
        Assert.Equal(apiKey, await ReadDecryptedKeyAsync(created.Id));
    }

    [Fact]
    public async Task Update_with_a_new_key_replaces_it()
    {
        var created = await TestCredentials.CreateAsync(fixture);
        var rotated = $"rotated-{Guid.NewGuid():N}";
        var service = await GetServiceAsync<SteamCredentialService>();

        var result = await service.UpdateAsync(created.Id, created.Name, rotated, CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(rotated, await ReadDecryptedKeyAsync(created.Id));
    }

    [Fact]
    public async Task Update_of_an_unknown_credential_reports_an_error()
    {
        var service = await GetServiceAsync<SteamCredentialService>();

        var result = await service.UpdateAsync(999_999, "不存在", "key", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.Credential);
    }

    [Fact]
    public async Task Delete_of_an_unreferenced_credential_succeeds()
    {
        var created = await TestCredentials.CreateAsync(fixture);
        var service = await GetServiceAsync<SteamCredentialService>();

        Assert.Equal(CredentialDeleteOutcome.Deleted, await service.DeleteAsync(created.Id, CancellationToken.None));
        Assert.Null(await service.GetAsync(created.Id, CancellationToken.None));
    }

    /// <summary>被任何游戏引用的凭据一律拒绝删除（外键也是 Restrict）：轮换密钥不该顺手弄坏游戏。</summary>
    [Fact]
    public async Task Delete_of_a_referenced_credential_returns_in_use()
    {
        var apiKey = $"plain-key-{Guid.NewGuid():N}";
        var created = await TestCredentials.CreateAsync(fixture, apiKey: apiKey);
        var game = await fixture.SeedGameAsync();
        await AttachCredentialAsync(game, created.Id);
        var service = await GetServiceAsync<SteamCredentialService>();

        Assert.Equal(CredentialDeleteOutcome.InUse, await service.DeleteAsync(created.Id, CancellationToken.None));

        // 凭据与游戏都还在。
        Assert.NotNull(await service.GetAsync(created.Id, CancellationToken.None));
        using var scope = fixture.DefaultFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(created.Id, (await db.Games.SingleAsync(g => g.Id == game.Id)).CredentialId);
    }

    [Fact]
    public async Task Delete_of_an_unknown_credential_returns_not_found()
    {
        var service = await GetServiceAsync<SteamCredentialService>();

        Assert.Equal(CredentialDeleteOutcome.NotFound, await service.DeleteAsync(999_999, CancellationToken.None));
    }

    [Fact]
    public async Task Game_count_reflects_how_many_games_reference_the_credential()
    {
        var created = await TestCredentials.CreateAsync(fixture);
        var gameA = await fixture.SeedGameAsync();
        var gameB = await fixture.SeedGameAsync();
        await AttachCredentialAsync(gameA, created.Id);
        await AttachCredentialAsync(gameB, created.Id);
        var service = await GetServiceAsync<SteamCredentialService>();

        var view = await service.GetAsync(created.Id, CancellationToken.None);

        Assert.Equal(2, view!.GameCount);
    }

    // ------------------------------------------------------- 探测

    [Fact]
    public async Task Verify_reports_ok_when_steam_answers_with_a_players_array()
    {
        var steam = new FakeSteamHandler();
        steam.SetProfileResponse(FakeSteamHandler.Profile("probe", null));
        var factory = fixture.CreateFactory(steam);
        var created = await TestCredentials.CreateAsync(fixture, factory: factory);
        var service = await GetServiceAsync<SteamCredentialService>(factory);

        var result = await service.VerifyAsync(created.Id, CancellationToken.None);

        Assert.Equal(CredentialProbeStatus.Ok, result.Status);
        Assert.Contains("key=", Assert.Single(steam.RequestedPaths));
    }

    [Fact]
    public async Task Verify_reports_invalid_when_steam_rejects_the_key()
    {
        var steam = new FakeSteamHandler();
        steam.SetProfileResponse(() => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var factory = fixture.CreateFactory(steam);
        var created = await TestCredentials.CreateAsync(fixture, factory: factory);
        var service = await GetServiceAsync<SteamCredentialService>(factory);

        var result = await service.VerifyAsync(created.Id, CancellationToken.None);

        Assert.Equal(CredentialProbeStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Verify_reports_unreachable_when_steam_fails()
    {
        var steam = new FakeSteamHandler();
        steam.SetProfileResponse(FakeSteamHandler.SteamServerError());
        var factory = fixture.CreateFactory(steam);
        var created = await TestCredentials.CreateAsync(fixture, factory: factory);
        var service = await GetServiceAsync<SteamCredentialService>(factory);

        var result = await service.VerifyAsync(created.Id, CancellationToken.None);

        Assert.Equal(CredentialProbeStatus.Unreachable, result.Status);
    }

    [Fact]
    public async Task Verify_reports_unreachable_for_an_undecryptable_credential()
    {
        var created = await TestCredentials.CreateAsync(fixture);
        await CorruptAsync(created.Id);
        var service = await GetServiceAsync<SteamCredentialService>();

        var result = await service.VerifyAsync(created.Id, CancellationToken.None);

        Assert.Equal(CredentialProbeStatus.Unreachable, result.Status);
        Assert.Contains("解密", result.Message);
    }

    [Fact]
    public async Task Verify_reports_not_found_for_an_unknown_credential()
    {
        var service = await GetServiceAsync<SteamCredentialService>();

        var result = await service.VerifyAsync(999_999, CancellationToken.None);

        Assert.Equal(CredentialProbeStatus.NotFound, result.Status);
    }

    // ------------------------------------------------------- 密钥不泄漏

    /// <summary>
    /// 保存失败时管理端会带着错误消息重渲染表单，所以错误消息里也绝不能出现密钥——
    /// 那是最容易被忽略的一条回显路径。
    /// </summary>
    [Fact]
    public async Task A_failed_save_never_echoes_the_key_in_its_error_message()
    {
        var service = await GetServiceAsync<SteamCredentialService>();
        // 密钥里的空白（粘贴时带进的换行）会被拒绝；首尾空白会被 Trim 掉，所以用中间夹一个换行。
        var secret = $"secret-{Guid.NewGuid():N}";
        var keyWithNewline = $"{secret}\nsecond-line";
        var overLongKey = new string('k', 129);

        var whitespace = await service.CreateAsync("带换行的密钥", keyWithNewline, CancellationToken.None);
        Assert.False(whitespace.Succeeded);
        Assert.NotNull(whitespace.Error);
        Assert.DoesNotContain(secret, whitespace.Error!);

        var tooLong = await service.CreateAsync("太长", overLongKey, CancellationToken.None);
        Assert.False(tooLong.Succeeded);
        Assert.DoesNotContain(overLongKey, tooLong.Error!);

        var updated = await service.UpdateAsync(999_999, "不存在", keyWithNewline, CancellationToken.None);
        Assert.False(updated.Succeeded);
        Assert.DoesNotContain(secret, updated.Error!);
    }

    /// <summary>
    /// 端到端：从管理端建出来的凭据，换一个全新的请求作用域（新的 DbContext + 新的解析器）读回来
    /// 必须仍然能解密并真的被用去打 Steam——加密往返如果只测 Protect/TryUnprotect，是测不到这条路径的。
    /// </summary>
    [Fact]
    public async Task A_key_created_through_the_admin_path_is_usable_for_a_steam_call()
    {
        var apiKey = $"plain-key-{Guid.NewGuid():N}";
        var steam = new FakeSteamHandler();
        steam.EnqueueTicketResponse(FakeSteamHandler.TicketOk("76561198000000066"));
        steam.SetProfileResponse(FakeSteamHandler.Profile("Encrypted", null));
        var factory = await fixture.CreateFactoryOnEmptyDatabaseAsync(steam);
        var appId = TestGames.UniqueAppId();
        var credential = await TestCredentials.CreateAsync(fixture, apiKey: apiKey, factory: factory);
        var game = await TestGames.SeedAsync(factory, steamAppId: appId, withCredential: false);
        await AttachCredentialAsync(game, credential.Id, factory);

        var response = await factory.CreateClient().PostAsJsonAsync(game.AuthPath, new { ticket = "ticket" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ticketCall = Assert.Single(
            steam.RequestedPaths, p => p.Contains("AuthenticateUserTicket", StringComparison.Ordinal));
        Assert.Contains($"key={apiKey}", ticketCall);
        Assert.Contains($"appid={appId}", ticketCall);
    }

    /// <summary>
    /// 本次升级新增的凭据存储最怕的就是"某个视图顺手把密钥带出去了"。
    /// 这里把两个管理端服务返回的<b>全部</b>视图（含游戏列表/详情）序列化后一起比对。
    /// </summary>
    [Fact]
    public async Task Plaintext_key_never_appears_in_any_admin_view()
    {
        var apiKey = $"plain-key-{Guid.NewGuid():N}";
        var created = await TestCredentials.CreateAsync(fixture, apiKey: apiKey);
        var game = await fixture.SeedGameAsync();
        await AttachCredentialAsync(game, created.Id);

        var credentials = await GetServiceAsync<SteamCredentialService>();
        var games = await GetServiceAsync<GameAdminService>();

        var exposed = new List<string>
        {
            JsonSerializer.Serialize(created),
            JsonSerializer.Serialize(await credentials.GetAsync(created.Id, CancellationToken.None)),
            JsonSerializer.Serialize(await credentials.ListAsync(CancellationToken.None)),
            JsonSerializer.Serialize(await games.GetAsync(game.Id, CancellationToken.None)),
            JsonSerializer.Serialize(await games.ListAsync(CancellationToken.None)),
            (await games.GetAsync(game.Id, CancellationToken.None))!.ToString(),
        };

        Assert.All(exposed, text => Assert.DoesNotContain(apiKey, text));
    }

    /// <summary>
    /// 解析出来的游戏对象里确实带着可用的明文密钥（服务端要用它去调 Steam），
    /// 但它的 ToString 必须不含密钥——否则任何一次"把游戏对象打进日志"都会泄漏发行商密钥。
    /// </summary>
    [Fact]
    public async Task Resolved_game_exposes_the_key_to_code_but_not_to_ToString()
    {
        var apiKey = $"plain-key-{Guid.NewGuid():N}";
        var created = await TestCredentials.CreateAsync(fixture, apiKey: apiKey);
        var game = await fixture.SeedGameAsync();
        await AttachCredentialAsync(game, created.Id);
        var resolver = await GetServiceAsync<GameResolver>();

        var resolved = await resolver.ResolveAsync(game.AppId, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(apiKey, resolved!.ApiKey);
        Assert.True(resolved.IsFullyConfigured);
        Assert.DoesNotContain(apiKey, resolved.ToString());
        // 但该有的诊断信息要在：否则日志里看不出是哪个游戏。
        Assert.Contains(game.AppId!, resolved.ToString());
        Assert.Contains("Configured=True", resolved.ToString());
    }

    [Fact]
    public async Task Resolved_game_marks_a_credential_it_cannot_decrypt()
    {
        var created = await TestCredentials.CreateAsync(fixture);
        var game = await fixture.SeedGameAsync();
        await AttachCredentialAsync(game, created.Id);
        await CorruptAsync(created.Id);
        var resolver = await GetServiceAsync<GameResolver>();

        var resolved = await resolver.ResolveAsync(game.AppId, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.True(resolved!.CredentialUnreadable);
        Assert.Null(resolved.ApiKey);
        Assert.False(resolved.IsFullyConfigured);
    }

    /// <summary>
    /// 解析按 AppID 精确匹配：首尾空白会被规范化掉（URL 段可能被手工输入），
    /// 但非数字段、未知 AppID 与 null 一律解析不到——绝不会有"猜一个游戏"的兜底。
    /// </summary>
    [Fact]
    public async Task Resolve_requires_a_matching_numeric_app_id()
    {
        var game = await fixture.SeedGameAsync();
        var resolver = await GetServiceAsync<GameResolver>();

        var resolved = await resolver.ResolveAsync($"  {game.AppId}  ", CancellationToken.None);
        Assert.NotNull(resolved);
        Assert.Equal(game.AppId, resolved!.AppId);
        Assert.Equal(game.Id, resolved.Id);

        Assert.Null(await resolver.ResolveAsync(TestGames.UniqueAppId(), CancellationToken.None));
        // 非数字段连查库都不该查：它们永远匹配不到 AppID。
        Assert.Null(await resolver.ResolveAsync("not-a-number", CancellationToken.None));
        Assert.Null(await resolver.ResolveAsync($"{game.AppId}x", CancellationToken.None));
        Assert.Null(await resolver.ResolveAsync("", CancellationToken.None));
        Assert.Null(await resolver.ResolveAsync(null, CancellationToken.None));
    }

    /// <summary>升级迁移建的那行占位游戏（SteamAppId 为 NULL）解析不到：NULL 不是可寻址的 AppID。</summary>
    [Fact]
    public async Task Resolve_never_returns_a_game_without_an_app_id()
    {
        var placeholder = await fixture.SeedGameAsync(withAppId: false);
        var resolver = await GetServiceAsync<GameResolver>();

        Assert.Null(placeholder.AppId);
        // 用它的数据库 Id 当路径段也解析不到——Id 从来不是寻址标识。
        Assert.Null(await resolver.ResolveAsync(placeholder.Id.ToString(), CancellationToken.None));
    }

    // ------------------------------------------------------- 工具

    /// <summary>直接从库里把密文解出来——只有测试才这么做，服务层任何出口都不带密钥。</summary>
    private async Task<string?> ReadDecryptedKeyAsync(int credentialId)
    {
        _ = fixture.DefaultFactory.CreateClient();
        var scope = fixture.DefaultFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ApiKeyProtector>();
        var stored = await db.SteamCredentials.AsNoTracking().SingleAsync(c => c.Id == credentialId);
        return protector.TryUnprotect(stored.EncryptedApiKey, stored.Id);
    }

    /// <summary>断言库里存的是密文而不是明文。</summary>
    [Fact]
    public async Task Stored_ciphertext_differs_from_the_plaintext_key()
    {
        var apiKey = $"plain-key-{Guid.NewGuid():N}";
        var created = await TestCredentials.CreateAsync(fixture, apiKey: apiKey);

        _ = fixture.DefaultFactory.CreateClient();
        var scope = fixture.DefaultFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.SteamCredentials.AsNoTracking().SingleAsync(c => c.Id == created.Id);

        Assert.NotEqual(apiKey, stored.EncryptedApiKey);
        Assert.DoesNotContain(apiKey, stored.EncryptedApiKey);
        Assert.Equal(apiKey, await ReadDecryptedKeyAsync(created.Id));
    }

    private async Task AttachCredentialAsync(
        TestGame game, int credentialId, GameFeedbackApplicationFactory? factory = null)
    {
        var target = factory ?? fixture.DefaultFactory;
        _ = target.CreateClient();
        var scope = target.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Games.SingleAsync(g => g.Id == game.Id);
        stored.CredentialId = credentialId;
        await db.SaveChangesAsync();
    }

    private async Task CorruptAsync(int credentialId, GameFeedbackApplicationFactory? factory = null)
    {
        var target = factory ?? fixture.DefaultFactory;
        _ = target.CreateClient();
        var scope = target.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.SteamCredentials.SingleAsync(c => c.Id == credentialId);
        stored.EncryptedApiKey = "CfDJ8-not-a-real-protected-payload";
        await db.SaveChangesAsync();
    }
}
