using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentFleet.Tests;

public sealed class RequestGuardTests
{
    private static readonly IReadOnlyCollection<string> None = [];

    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost:8000")]
    [InlineData("LOCALHOST:8000")]
    [InlineData("app.localhost:3000")]
    [InlineData("127.0.0.1:8000")]
    [InlineData("192.168.1.10:8000")]
    [InlineData("[::1]:8000")]
    [InlineData("[::1]")]
    public void Addresses_that_cannot_be_rebound_are_allowed(string host) =>
        Assert.True(RequestGuard.IsAllowedHost(host, None));

    [Theory]
    [InlineData("evil.example.com")]
    [InlineData("evil.example.com:8000")]
    [InlineData("localhost.evil.com")]
    [InlineData("127.0.0.1.evil.com")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_names_are_refused(string? host) =>
        Assert.False(RequestGuard.IsAllowedHost(host, None));

    [Fact]
    public void This_machines_own_name_is_allowed()
    {
        Assert.True(RequestGuard.IsAllowedHost(Environment.MachineName + ":8000", None));
        Assert.True(RequestGuard.IsAllowedHost(Environment.MachineName.ToLowerInvariant() + ".local", None));
    }

    [Fact]
    public void Names_listed_in_the_setting_are_allowed()
    {
        IReadOnlyList<string> names = RequestGuard.ParseAllowedNames("hub.home.arpa, fleet.lan;other");

        Assert.Equal(["hub.home.arpa", "fleet.lan", "other"], names);
        Assert.True(RequestGuard.IsAllowedHost("fleet.lan:8000", names));
        Assert.False(RequestGuard.IsAllowedHost("fleet.example", names));
    }

    [Theory]
    [InlineData("http://localhost:3000", true)]
    [InlineData("http://192.168.1.10:3000", true)]
    [InlineData("https://evil.example.com", false)]
    [InlineData("null", false)]
    [InlineData("file://", false)]
    public void An_origin_must_name_an_allowed_host(string origin, bool expected) =>
        Assert.Equal(expected, RequestGuard.IsAllowedOrigin(origin, None));

    private static async Task<HttpResponseMessage> Send(string host, string? origin)
    {
        using IHost host_ = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(_ => { })
                .Configure(app =>
                {
                    RequestGuard.Use(app, None);
                    app.Run(context => context.Response.WriteAsync("ok"));
                }))
            .StartAsync();

        HttpClient client = host_.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = host;
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return await client.SendAsync(request);
    }

    [Fact]
    public async Task The_middleware_lets_a_local_request_through()
    {
        HttpResponseMessage response = await Send("localhost:8000", null);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_middleware_refuses_a_rebound_name_with_an_explanation()
    {
        HttpResponseMessage response = await Send("evil.example.com:8000", null);

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("FLEET_ALLOWED_HOSTS", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_middleware_refuses_a_browser_request_from_another_site_even_to_localhost()
    {
        HttpResponseMessage response = await Send("localhost:8000", "https://evil.example.com");

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }
}
