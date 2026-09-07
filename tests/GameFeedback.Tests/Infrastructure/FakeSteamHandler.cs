using System.Text;
using System.Text.Json;

namespace GameFeedback.Tests.Infrastructure;

/// <summary>
/// Steam HTTP 传输桩：按请求路径（AuthenticateUserTicket / GetPlayerSummaries）
/// 返回罐头响应，供测试验证 fail-closed 各分支。
/// </summary>
public sealed class FakeSteamHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _ticketResponses = new();
    private Func<HttpResponseMessage>? _profileResponse;

    public List<string> RequestedPaths { get; } = [];

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

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.PathAndQuery;
        RequestedPaths.Add(path);

        if (path.Contains("AuthenticateUserTicket", StringComparison.Ordinal))
        {
            if (_ticketResponses.Count == 0)
            {
                throw new InvalidOperationException("测试未提供 AuthenticateUserTicket 罐头响应");
            }
            return Task.FromResult(_ticketResponses.Dequeue()());
        }

        if (path.Contains("GetPlayerSummaries", StringComparison.Ordinal))
        {
            if (_profileResponse is null)
            {
                throw new HttpRequestException("测试未提供 GetPlayerSummaries 罐头响应");
            }
            return Task.FromResult(_profileResponse());
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

    public static Func<HttpResponseMessage> Profile(string steamName, string avatarUrl) => () => JsonResponse(
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

    private static HttpResponseMessage JsonResponse(string json) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}
