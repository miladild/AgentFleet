using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentFleet.Tests;

public sealed class OllamaUpdateEndpointTests
{
    private const string Worker = "http://192.0.2.10:11434/";

    private static OllamaUpdateFixture Fixture()
    {
        var fixture = new OllamaUpdateFixture();
        fixture.UseNodes(OllamaUpdateFixture.Node("worker", "http://192.0.2.10:11434/v1", OllamaUpdateFixture.Ssh()));
        fixture.Versions[Worker] = "0.12.0";
        fixture.Remote.Respond = command =>
        {
            if (command.StartsWith("printf", StringComparison.Ordinal))
            {
                return new RemoteResult(0, "/home/fleet-updater", string.Empty);
            }

            if (command == "sudo -n systemctl start ollama")
            {
                fixture.Versions[Worker] = OllamaUpdateFixture.Latest;
            }

            return new RemoteResult(0, string.Empty, string.Empty);
        };
        return fixture;
    }

    // The same guard that stands in front of the whole app is put in front of these calls.
    private static async Task<IHost> StartAsync(OllamaUpdateService service) =>
        await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services => services.AddRouting())
                .Configure(app =>
                {
                    RequestGuard.Use(app, []);
                    app.UseRouting();
                    app.UseEndpoints(endpoints => OllamaUpdateEndpoints.Map(endpoints, service));
                }))
            .StartAsync();

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task The_versions_call_lists_machines_and_never_asks_GitHub()
    {
        OllamaUpdateFixture fixture = Fixture();
        using IHost host = await StartAsync(fixture.Service);

        HttpResponseMessage response = await host.GetTestClient().GetAsync("/api/setup/ollama-versions");
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("0.12.0", body.GetProperty("machines")[0].GetProperty("version").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("latest").ValueKind);
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task The_check_call_asks_GitHub_and_the_versions_call_then_offers_the_update()
    {
        OllamaUpdateFixture fixture = Fixture();
        using IHost host = await StartAsync(fixture.Service);
        HttpClient client = host.GetTestClient();

        HttpResponseMessage check = await client.PostAsync("/api/setup/ollama-versions/check", null);
        JsonElement versions = await (await client.GetAsync("/api/setup/ollama-versions")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        Assert.Equal("0.13.0", (await check.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetString());
        Assert.True(versions.GetProperty("machines")[0].GetProperty("canUpdate").GetBoolean());
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task A_check_that_GitHub_cannot_answer_is_a_502_with_the_reason()
    {
        OllamaUpdateFixture fixture = Fixture();
        fixture.ReleaseStatus = HttpStatusCode.ServiceUnavailable;
        using IHost host = await StartAsync(fixture.Service);

        HttpResponseMessage response = await host.GetTestClient().PostAsync("/api/setup/ollama-versions/check", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("HTTP 503", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_update_starts_in_the_background_and_the_jobs_call_shows_its_result()
    {
        OllamaUpdateFixture fixture = Fixture();
        using IHost host = await StartAsync(fixture.Service);
        HttpClient client = host.GetTestClient();
        await client.PostAsync("/api/setup/ollama-versions/check", null);

        HttpResponseMessage started = await client.PostAsync("/api/setup/ollama-update", Json($$"""{"url":"{{Worker}}"}"""));

        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        JsonElement job = default;
        for (int attempt = 0; attempt < 300; attempt++)
        {
            JsonElement list = await (await client.GetAsync("/api/setup/ollama-updates")).Content.ReadFromJsonAsync<JsonElement>();
            job = list[0];
            if (job.GetProperty("state").GetString() == OllamaUpdateService.Done)
            {
                break;
            }

            await Task.Delay(10);
        }

        Assert.Equal(OllamaUpdateService.Done, job.GetProperty("state").GetString());
        Assert.Equal("0.13.0", job.GetProperty("finalVersion").GetString());
        Assert.Equal("0.12.0", job.GetProperty("fromVersion").GetString());
    }

    [Fact]
    public async Task Update_all_needs_no_address()
    {
        OllamaUpdateFixture fixture = Fixture();
        using IHost host = await StartAsync(fixture.Service);
        HttpClient client = host.GetTestClient();
        await client.PostAsync("/api/setup/ollama-versions/check", null);

        HttpResponseMessage started = await client.PostAsync("/api/setup/ollama-update", Json("""{"all":true}"""));

        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        Assert.Single((await started.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
    }

    [Fact]
    public async Task A_running_plan_gives_a_409_with_a_clear_message()
    {
        OllamaUpdateFixture fixture = Fixture();
        fixture.PlanRunning = true;
        using IHost host = await StartAsync(fixture.Service);
        HttpClient client = host.GetTestClient();
        await client.PostAsync("/api/setup/ollama-versions/check", null);

        HttpResponseMessage response = await client.PostAsync("/api/setup/ollama-update", Json($$"""{"url":"{{Worker}}"}"""));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("plan is running", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, fixture.Remote.Opened);
    }

    [Fact]
    public async Task An_update_before_the_check_is_a_409()
    {
        OllamaUpdateFixture fixture = Fixture();
        using IHost host = await StartAsync(fixture.Service);

        HttpResponseMessage response = await host.GetTestClient().PostAsync("/api/setup/ollama-update", Json($$"""{"url":"{{Worker}}"}"""));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Check for updates first", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_request_that_names_no_machine_is_a_400_and_a_download_address_in_the_body_is_ignored()
    {
        OllamaUpdateFixture fixture = Fixture();
        using IHost host = await StartAsync(fixture.Service);
        HttpClient client = host.GetTestClient();
        await client.PostAsync("/api/setup/ollama-versions/check", null);

        HttpResponseMessage nothing = await client.PostAsync("/api/setup/ollama-update", Json("{}"));
        HttpResponseMessage sneaky = await client.PostAsync("/api/setup/ollama-update",
            Json($$"""{"url":"{{Worker}}","downloadUrl":"https://example.invalid/evil","asset":"evil.exe","version":"1.0.0;id"}"""));
        for (int attempt = 0; attempt < 300 && fixture.Service.IsUpdating; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(HttpStatusCode.BadRequest, nothing.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, sneaky.StatusCode);
        Assert.Contains(fixture.Remote.Commands, command => command.Contains("/releases/download/v0.13.0/ollama-linux-amd64.tar.zst"));
        Assert.DoesNotContain(fixture.Remote.Commands, command => command.Contains("evil") || command.Contains("example.invalid"));
    }

    [Theory]
    [InlineData("POST", "/api/setup/ollama-versions/check")]
    [InlineData("POST", "/api/setup/ollama-update")]
    [InlineData("GET", "/api/setup/ollama-versions")]
    public async Task The_calls_are_behind_the_same_host_and_origin_guard_as_the_other_setup_calls(string method, string path)
    {
        OllamaUpdateFixture fixture = Fixture();
        using IHost host = await StartAsync(fixture.Service);
        HttpClient client = host.GetTestClient();

        var otherSite = new HttpRequestMessage(new HttpMethod(method), path) { Content = method == "POST" ? Json("""{"all":true}""") : null };
        otherSite.Headers.Add("Origin", "https://evil.example.com");
        var rebound = new HttpRequestMessage(new HttpMethod(method), path) { Content = method == "POST" ? Json("""{"all":true}""") : null };
        rebound.Headers.Host = "evil.example.com:8000";

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(otherSite)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(rebound)).StatusCode);
        Assert.Empty(fixture.Requests);
        Assert.Equal(0, fixture.Remote.Opened);
    }
}
