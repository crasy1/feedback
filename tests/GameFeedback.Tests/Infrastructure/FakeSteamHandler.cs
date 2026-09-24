using System.Text;
using System.Text.Json;

namespace GameFeedback.Tests.Infrastructure;

/// <summary>
/// Steam HTTP 传输桩：按请求路径（AuthenticateUserTicket / GetPlayerSummaries /
/// GetSingleGamePlaytime）返回罐头响应，供测试验证 fail-closed 各分支。
/// </summary>
public sealed class FakeSteamHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _ticketResponses = new();
    private Func<HttpResponseMessage>? _profileResponse;
    private Func<HttpResponseMessage>? _playtimeResponse;

    public List<string> RequestedPaths { get; } = [];

    /// <summary>
    /// 让 GetSingleGamePlaytime 延迟这么久再回应，用于覆盖"游玩时长查询超时"分支。
    /// 配合 <see cref="Services.SteamPlaytimeService.LookupBudget"/> 使用，取值要大于预算。
    /// </summary>
    public TimeSpan PlaytimeDelay { get; set; } = TimeSpan.Zero;

    public void EnqueueTicketResponse(Func<HttpResponseMessage> responseFactory) =>
        _ticketResponses.Enqueue(responseFactory);

    public void EnqueueTicketResponses(Func<HttpResponseMessage> responseFactory, int count)
    {
        for (var i = 0; i < count; i++)
        {
            _ticketResponses.Enqueue(responseFactory);
        }
    }

    public void SetProfileResponse(Func<HttpResponseMessage>? responseFactory) =>
        _profileResponse = responseFactory;

    /// <summary>
    /// 设置 GetSingleGamePlaytime 的响应。**不设置也能用**：会回一个合法的默认时长。
    /// 创建反馈总会顺带查一次游玩时长，若这里也像资料那样强制排队，
    /// 每个不关心该字段的用例都得先补一句无关的桩。
    /// </summary>
    public void SetPlaytimeResponse(Func<HttpResponseMessage>? responseFactory) =>
        _playtimeResponse = responseFactory;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.PathAndQuery;
        RequestedPaths.Add(path);

        if (path.Contains("AuthenticateUserTicket", StringComparison.Ordinal))
        {
            if (_ticketResponses.Count == 0)
            {
                throw new InvalidOperationException("测试未提供 AuthenticateUserTicket 罐头响应");
            }
            return _ticketResponses.Dequeue()();
        }

        if (path.Contains("GetPlayerSummaries", StringComparison.Ordinal))
        {
            if (_profileResponse is null)
            {
                throw new HttpRequestException("测试未提供 GetPlayerSummaries 罐头响应");
            }
            return _profileResponse();
        }

        if (path.Contains("GetSingleGamePlaytime", StringComparison.Ordinal))
        {
            if (PlaytimeDelay > TimeSpan.Zero)
            {
                await Task.Delay(PlaytimeDelay, cancellationToken);
            }
            return (_playtimeResponse ?? Playtime(2361))();
        }

        throw new InvalidOperationException($"测试未预期的 Steam 请求：{path}");
    }

    public static Func<HttpResponseMessage> TicketOk(string steamId) => () => JsonResponse(
        JsonSerializer.Serialize(new
        {
            response = new
            {
                @params = new
                {
                    result = "OK",
                    steamid = steamId,
                    ownersteamid = steamId,
                    vacbanned = false,
                    publisherbanned = false,
                },
            },
        }));

    public static Func<HttpResponseMessage> TicketRejected() => () => JsonResponse(
        JsonSerializer.Serialize(new
        {
            response = new
            {
                @params = new
                {
                    result = "Fail",
                    error = new { ErrorCode = 3, ErrorDesc = "Ticket invalid" },
                },
            },
        }));

    public static Func<HttpResponseMessage> TicketMissingSteamId() => () => JsonResponse(
        JsonSerializer.Serialize(new { response = new { @params = new { result = "OK" } } }));

    public static Func<HttpResponseMessage> SteamServerError() => () =>
        new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError);

    public static Func<HttpResponseMessage> MalformedJson() => () => JsonResponse("{not-json");

    public static Func<HttpResponseMessage> Payload(string json) => () => JsonResponse(json);

    /// <summary>Steam 的 avatarfull 在资料不可见时确实可能是 null，所以这里允许留空。</summary>
    public static Func<HttpResponseMessage> Profile(string steamName, string? avatarUrl) => () => JsonResponse(
        JsonSerializer.Serialize(new
        {
            response = new
            {
                players = new[]
                {
                    new { steamid = "76561198000000001", personaname = steamName, avatarfull = avatarUrl },
                },
            },
        }));

    /// <summary>
    /// GetSingleGamePlaytime 的响应，形状照抄 2026 年的真实观测（issue 01）：
    /// 扁平对象、**没有 games[] 包装**、**不回显 appid**，playtime_forever 单位分钟。
    /// </summary>
    public static Func<HttpResponseMessage> Playtime(int minutes) => () => JsonResponse(
        JsonSerializer.Serialize(new
        {
            response = new
            {
                playtime_2weeks = 81,
                playtime_forever = minutes,
            },
        }));

    /// <summary>没有 playtime_forever 字段（资料私密 / 未拥有该游戏时的退化形状）。</summary>
    public static Func<HttpResponseMessage> PlaytimeMissingField() => () => JsonResponse(
        JsonSerializer.Serialize(new { response = new { playtime_2weeks = 81 } }));

    private static HttpResponseMessage JsonResponse(string json) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}
