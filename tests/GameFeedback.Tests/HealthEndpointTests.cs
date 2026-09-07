using System.Net;
using GameFeedback.Tests.Infrastructure;

namespace GameFeedback.Tests;

[Collection("Integration")]
public sealed class HealthEndpointTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task Health_returns_ok_without_authentication()
    {
        var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
