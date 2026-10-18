using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

public sealed class WebFetchAllowListTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fleet-fetch-hosts-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_directory, "fleet.config.json");
    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public WebFetchAllowListTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static FleetConfig Config(IReadOnlyList<string>? allowedHosts, IReadOnlyList<string>? internalHosts = null,
        string nodeUrl = "http://192.0.2.80/v1") =>
        new("triage", "conservative", false,
            [new FleetNodeConfig("hub", nodeUrl, "model", "local", FleetTiers.Heavy, Fallback: true)],
            new Dictionary<string, FleetToolConfig> { ["webFetch"] = new(true, internalHosts, allowedHosts) });

    private sealed class FetchProbe
    {
        public List<string> Resolved { get; } = [];
        public List<Uri> Requests { get; } = [];
        public int Handlers { get; private set; }
        public string Address { get; init; } = "192.0.2.90";
        public string? Redirect { get; init; }

        public Task<string> Fetch(string url, FleetConfig config) => WebTools.FetchUrlAsync(url, config,
            NullLogger.Instance, CancellationToken.None,
            (host, _) =>
            {
                Resolved.Add(host);
                return Task.FromResult(new[] { IPAddress.Parse(Address) });
            },
            (_, _) =>
            {
                Handlers++;
                return new ReplyHandler(request =>
                {
                    Requests.Add(request.RequestUri!);
                    return Redirect is not null && Requests.Count == 1
                        ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri(Redirect, UriKind.RelativeOrAbsolute) } }
                        : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("readable page") };
                });
            });
    }

    private sealed class ReplyHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(reply(request));
    }

    private static string Refusal(string host) =>
        $"web_fetch only reads the hosts listed in tools.webFetch.allowedHosts; {host} is not on the list.";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_missing_or_empty_list_keeps_public_fetches(bool empty)
    {
        var probe = new FetchProbe();
        Assert.Equal("readable page", await probe.Fetch("https://public.example.test/page", Config(empty ? [] : null)));
        Assert.Single(probe.Resolved);
        Assert.Single(probe.Requests);
    }

    [Theory]
    [InlineData("docs.example.com", true)]
    [InlineData("other.example.com", false)]
    [InlineData("Docs.Example.COM.", true)]
    public async Task Exact_hosts_ignore_case_and_a_trailing_dot(string host, bool allowed)
    {
        var probe = new FetchProbe();
        string result = await probe.Fetch($"https://{host}/page", Config(["docs.example.com"]));
        Assert.Equal(allowed ? "readable page" : Refusal(new Uri($"https://{host}/page").IdnHost), result);
        Assert.Equal(allowed ? 1 : 0, probe.Resolved.Count);
        Assert.Equal(allowed ? 1 : 0, probe.Handlers);
    }

    [Theory]
    [InlineData("a.example.com", true)]
    [InlineData("a.b.example.com", true)]
    [InlineData("example.com", false)]
    [InlineData("badexample.com", false)]
    [InlineData("example.com.attacker.example", false)]
    public async Task Wildcards_match_only_subdomains(string host, bool allowed)
    {
        var probe = new FetchProbe();
        string result = await probe.Fetch($"https://{host}/page", Config(["*.example.com"]));
        Assert.Equal(allowed ? "readable page" : Refusal(host), result);
        Assert.Equal(allowed ? 1 : 0, probe.Resolved.Count);
        Assert.Equal(allowed ? 1 : 0, probe.Handlers);
    }

    [Fact]
    public async Task A_refused_host_does_not_even_resolve_fleet_nodes()
    {
        var probe = new FetchProbe();
        Assert.Equal(Refusal("other.example.test"), await probe.Fetch("https://other.example.test/page",
            Config(["docs.example.test"], nodeUrl: "http://node.example.test/v1")));
        Assert.Empty(probe.Resolved);
        Assert.Equal(0, probe.Handlers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Each_redirect_checks_the_next_host_before_resolving(bool allowed)
    {
        var probe = new FetchProbe { Redirect = "https://next.example.test/page" };
        string result = await probe.Fetch("https://docs.example.test/start",
            Config(allowed ? ["docs.example.test", "next.example.test"] : ["docs.example.test"]));
        Assert.Equal(allowed ? "readable page" : Refusal("next.example.test"), result);
        Assert.Equal(allowed ? 2 : 1, probe.Resolved.Count);
        Assert.Equal(allowed ? 2 : 1, probe.Handlers);
        Assert.Equal(allowed ? 2 : 1, probe.Requests.Count);
    }

    [Theory]
    [InlineData("192.0.2.90", true)]
    [InlineData("192.0.2.91", false)]
    [InlineData("[2001:db8::90]", true)]
    [InlineData("[2001:db8::91]", false)]
    public async Task Ip_literals_match_only_the_listed_address(string host, bool allowed)
    {
        var probe = new FetchProbe();
        string result = await probe.Fetch($"https://{host}/page", Config(["192.0.2.90", "2001:db8::90"]));
        Assert.Equal(allowed ? "readable page" : Refusal(host.Trim('[', ']')), result);
        Assert.Equal(allowed ? 1 : 0, probe.Resolved.Count);
        Assert.Equal(allowed ? 1 : 0, probe.Handlers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Internal_exceptions_must_also_match_the_host_list(bool restricted)
    {
        var probe = new FetchProbe { Address = "127.0.0.1" };
        string result = await probe.Fetch("http://docs.example.test/page",
            Config(restricted ? ["other.example.test"] : [], ["docs.example.test"]));
        Assert.Equal(restricted ? Refusal("docs.example.test") : "readable page", result);
        Assert.Equal(restricted ? 0 : 1, probe.Resolved.Count);
        Assert.Equal(restricted ? 0 : 1, probe.Handlers);
    }

    [Fact]
    public async Task An_internal_host_matching_both_lists_can_still_be_fetched()
    {
        var probe = new FetchProbe { Address = "127.0.0.1" };
        Assert.Equal("readable page", await probe.Fetch("http://docs.example.test/page",
            Config(["docs.example.test"], ["docs.example.test"])));
        Assert.Single(probe.Resolved);
        Assert.Equal(1, probe.Handlers);
    }

    [Fact]
    public async Task Matching_both_lists_does_not_bypass_the_fleet_node_guard()
    {
        var probe = new FetchProbe();
        string result = await probe.Fetch("http://node.example.test/page",
            Config(["node.example.test"], ["node.example.test"], "http://node.example.test/v1"));
        Assert.Equal("web_fetch only reads public internet addresses; node.example.test is on this computer or the local network.", result);
        Assert.Equal(0, probe.Handlers);
    }

    [Fact]
    public async Task A_listed_public_name_still_cannot_resolve_to_a_private_address()
    {
        var probe = new FetchProbe { Address = "127.0.0.1" };
        Assert.StartsWith("web_fetch only reads public internet addresses;",
            await probe.Fetch("https://docs.example.test/page", Config(["docs.example.test"])));
        Assert.Equal(0, probe.Handlers);
    }

    [Fact]
    public void Entries_are_trimmed_canonicalized_deduplicated_and_sorted()
    {
        Assert.Equal(["*.example.com", "192.0.2.90", "2001:db8::90", "docs.example.com"],
            WebFetchPolicy.NormalizeAllowedHosts([" Docs.Example.COM. ", "*.Example.COM.", "docs.example.com", "192.0.2.90", "[2001:db8::90]"]));
        Assert.Empty(WebFetchPolicy.NormalizeAllowedHosts(null));
        Assert.Empty(WebFetchPolicy.NormalizeAllowedHosts([]));
    }

    [Theory]
    [InlineData("https://docs.example.com")]
    [InlineData("docs.example.com/path")]
    [InlineData("docs.example.com:443")]
    [InlineData("docs example.com")]
    [InlineData("*")]
    [InlineData("*.")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    [InlineData("*.192.0.2.90")]
    [InlineData("foo.*.example.com")]
    [InlineData("[docs.example.com]")]
    [InlineData("docs.example.com?query=value")]
    [InlineData("docs.example.com#fragment")]
    [InlineData("docs\u0000.example.com")]
    [InlineData("docs.example.com\u0007")]
    [InlineData("doc\ts.example.com")]
    [InlineData("example..com")]
    [InlineData(".example.com")]
    public void Invalid_entries_are_rejected(string? entry)
    {
        var error = Assert.Throws<InvalidOperationException>(() => WebFetchPolicy.NormalizeAllowedHosts([entry!]));
        Assert.Contains("tools.webFetch.allowedHosts", error.Message);
    }

    private FleetConfigStore Open(Action<string, string>? replace = null) => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["FLEET_CONFIG_PATH"] = ConfigPath }).Build(), replace);

    [Fact]
    public void Loading_saving_and_reloading_keeps_and_normalizes_the_list()
    {
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Config([" Docs.Example.COM. ", "*.Example.COM", "docs.example.com"]),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        FleetConfigStore store = Open();
        Assert.Equal(["*.example.com", "docs.example.com"], store.Current.WebFetchAllowedHosts);
        store.Save(store.Current with { Tools = new Dictionary<string, FleetToolConfig> { ["webFetch"] = new(true, AllowedHosts: [" NEXT.Example.COM. "]) } });
        Assert.Equal(["next.example.com"], Open().Current.WebFetchAllowedHosts);
    }

    [Fact]
    public void A_null_list_is_omitted_from_json()
    {
        string json = JsonSerializer.Serialize(new FleetToolConfig(true),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.DoesNotContain("allowedHosts", json);
    }

    [Fact]
    public void Invalid_lists_are_refused_on_load_and_save_without_replacing_the_config()
    {
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Config(["https://docs.example.com"]),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Assert.Throws<InvalidOperationException>(() => Open());
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Config([]),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        FleetConfigStore store = Open();
        byte[] before = File.ReadAllBytes(ConfigPath);
        Assert.Throws<InvalidOperationException>(() => store.Save(Config(["*"])));
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
    }

    [Theory]
    [InlineData("https://docs.example.com@other.example/page", "other.example", false)]
    [InlineData("https://other.example@docs.example.com/page", "docs.example.com", true)]
    [InlineData("https://docs.example.com:8443/page", "docs.example.com", true)]
    [InlineData("https://docs.example.com/page#https://other.example/", "docs.example.com", true)]
    public async Task Userinfo_port_and_fragment_do_not_change_the_checked_host(string url, string host, bool allowed)
    {
        var probe = new FetchProbe();
        string result = await probe.Fetch(url, Config(["docs.example.com"]));
        Assert.Equal(allowed ? "readable page" : Refusal(host), result);
        Assert.Equal(allowed ? 1 : 0, probe.Handlers);
    }

    [Fact]
    public async Task A_backslash_before_the_userinfo_is_not_read_as_a_host()
    {
        var probe = new FetchProbe();
        Assert.Equal("Error: url must be an absolute http(s) URL.",
            await probe.Fetch("https://docs.example.com\\@other.example/page", Config(["docs.example.com"])));
        Assert.Equal(0, probe.Handlers);
    }

    [Theory]
    [InlineData("[2001:db8:0:0:0:0:0:90]", true)]
    [InlineData("[2001:DB8::90]", true)]
    [InlineData("[::ffff:192.0.2.90]", true)]
    [InlineData("[2001:db8::91]", false)]
    public async Task Ipv6_spellings_match_the_listed_address(string host, bool allowed)
    {
        var probe = new FetchProbe();
        string result = await probe.Fetch($"https://{host}/page", Config(WebFetchPolicy.NormalizeAllowedHosts(["2001:db8::90", "192.0.2.90"])));
        Assert.Equal(allowed ? "readable page" : Refusal(host.Trim('[', ']')), result);
        Assert.Equal(allowed ? 1 : 0, probe.Handlers);
    }

    [Theory]
    [InlineData("https://3221226074/page")]
    [InlineData("https://0xC0.0.2.90/page")]
    public async Task Alternate_ipv4_spellings_match_the_listed_address(string url)
    {
        var probe = new FetchProbe();
        Assert.Equal("readable page", await probe.Fetch(url, Config(["192.0.2.90"])));
    }

    [Fact]
    public async Task An_ip_entry_does_not_match_a_name_that_resolves_to_it()
    {
        var probe = new FetchProbe();
        Assert.Equal(Refusal("alias.example.test"), await probe.Fetch("https://alias.example.test/page", Config(["192.0.2.90"])));
        Assert.Empty(probe.Resolved);
        Assert.Equal(0, probe.Handlers);
    }

    [Theory]
    [InlineData("/next", "docs.example.com", true)]
    [InlineData("https://DOCS.example.com./next", "docs.example.com", true)]
    [InlineData("http://docs.example.com/next", "docs.example.com", true)]
    [InlineData("//other.example/next", "other.example", false)]
    public async Task Redirect_locations_are_checked_by_the_host_they_resolve_to(string location, string host, bool allowed)
    {
        var probe = new FetchProbe { Redirect = location };
        string result = await probe.Fetch("https://docs.example.com/start", Config(["docs.example.com"]));
        Assert.Equal(allowed ? "readable page" : Refusal(host), result);
        Assert.Equal(allowed ? 2 : 1, probe.Requests.Count);
    }

    [Theory]
    [InlineData("node.example.test", "http://node.example.test/page", "http://node.example.test/v1")]
    [InlineData("192.0.2.80", "http://192.0.2.80/page", "http://192.0.2.80/v1")]
    public async Task A_fleet_node_on_the_list_is_still_refused(string node, string url, string nodeUrl)
    {
        var probe = new FetchProbe();
        Assert.Equal($"web_fetch only reads public internet addresses; {node} is on this computer or the local network.",
            await probe.Fetch(url, Config(WebFetchPolicy.NormalizeAllowedHosts([node]), nodeUrl: nodeUrl)));
        Assert.Equal(0, probe.Handlers);
    }

    [Theory]
    [InlineData("bücher.example.test", "https://xn--bcher-kva.example.test/page")]
    [InlineData("xn--bcher-kva.example.test", "https://bücher.example.test/page")]
    public async Task Idn_entries_and_urls_match_in_their_punycode_form(string entry, string url)
    {
        var probe = new FetchProbe();
        Assert.Equal("readable page", await probe.Fetch(url, Config(WebFetchPolicy.NormalizeAllowedHosts([entry]))));
        Assert.Equal(1, probe.Handlers);
    }

    [Fact]
    public void Idn_entries_are_stored_in_their_punycode_form()
    {
        Assert.Equal(["xn--bcher-kva.example.test"], WebFetchPolicy.NormalizeAllowedHosts(["Bücher.example.test"]));
        Assert.Equal(["*.xn--bcher-kva.example.test"], WebFetchPolicy.NormalizeAllowedHosts(["*.Bücher.example.test"]));
    }

    [Fact]
    public void Ip_entries_written_two_ways_are_one_entry()
    {
        Assert.Equal(["2001:db8::90"], WebFetchPolicy.NormalizeAllowedHosts(["2001:db8:0::90", "[2001:DB8::90]"]));
        Assert.Equal(["192.0.2.90"], WebFetchPolicy.NormalizeAllowedHosts(["::ffff:192.0.2.90", "192.0.2.90"]));
        Assert.Equal(["192.0.2.90"], WebFetchPolicy.NormalizeAllowedHosts(["3221226074", "0xC0.0.2.90"]));
        Assert.Equal(["docs.example.com"], WebFetchPolicy.NormalizeAllowedHosts(["docs.exa​mple.com"]));
    }

    [Fact]
    public void Property_names_spelled_in_another_case_are_read_and_both_lists_apply()
    {
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Config(["docs.example.com"], ["internal.example.test"]), CamelCase)
            .Replace("\"allowedHosts\"", "\"AllowedHosts\"")
            .Replace("\"allowedInternalHosts\"", "\"AllowedInternalHosts\""));
        FleetConfigStore store = Open();
        Assert.Equal(["docs.example.com"], store.Current.WebFetchAllowedHosts);
        Assert.Equal(["internal.example.test"], store.Current.WebFetchAllowedInternalHosts);
    }

    [Fact]
    public void Property_names_in_all_capitals_are_read_and_the_list_applies()
    {
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Config(["docs.example.com"]), CamelCase)
            .Replace("\"allowedHosts\"", "\"ALLOWEDHOSTS\""));
        Assert.Equal(["docs.example.com"], Open().Current.WebFetchAllowedHosts);
    }

    [Fact]
    public void A_tools_key_spelled_in_another_case_carries_its_list_under_the_exact_key()
    {
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Config(["docs.example.com"]), CamelCase)
            .Replace("\"webFetch\"", "\"WebFetch\""));
        FleetConfigStore store = Open();
        Assert.Equal(["docs.example.com"], store.Current.WebFetchAllowedHosts);
        Assert.False(store.Current.Tools.ContainsKey("WebFetch"));
        Assert.True(store.Current.Tools.ContainsKey("webFetch"));
    }

    [Fact]
    public void When_both_tools_key_spellings_exist_the_exact_key_wins()
    {
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Config(["exact.example.com"]), CamelCase)
            .Replace("\"tools\":{", "\"tools\":{\"WebFetch\":{\"enabled\":true,\"allowedHosts\":[\"other.example.com\"]},"));
        Assert.Equal(["exact.example.com"], Open().Current.WebFetchAllowedHosts);
    }

    [Fact]
    public void The_exact_tools_key_wins_when_another_spelling_comes_after_it()
    {
        var document = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(Config(["exact.example.com"]), CamelCase))!.AsObject();
        document["tools"]!.AsObject()["WebFetch"] = System.Text.Json.Nodes.JsonNode.Parse("{\"enabled\":true,\"allowedHosts\":[\"other.example.com\"]}");
        File.WriteAllText(ConfigPath, document.ToJsonString());
        Assert.Equal(["exact.example.com"], Open().Current.WebFetchAllowedHosts);
    }

    [Fact]
    public void Saving_and_reloading_writes_the_camel_case_names()
    {
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Config(["docs.example.com"], ["internal.example.test"]), CamelCase)
            .Replace("\"allowedHosts\"", "\"AllowedHosts\"")
            .Replace("\"webFetch\"", "\"WEBFETCH\""));
        FleetConfigStore store = Open();
        store.Save(store.Current);
        string saved = File.ReadAllText(ConfigPath);
        Assert.Contains("\"allowedHosts\"", saved);
        Assert.Contains("\"allowedInternalHosts\"", saved);
        Assert.Contains("\"webFetch\"", saved);
        Assert.DoesNotContain("AllowedHosts", saved);
        Assert.Equal(["docs.example.com"], Open().Current.WebFetchAllowedHosts);
    }

    [Fact]
    public void A_null_entry_in_the_list_file_is_refused_on_load()
    {
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Config(["docs.example.com"]), CamelCase)
            .Replace("[\"docs.example.com\"]", "[null]"));
        Assert.Throws<InvalidOperationException>(() => Open());
    }

    [Fact]
    public void A_list_saved_with_web_fetch_switched_off_is_kept()
    {
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Config(["docs.example.com"]), CamelCase)
            .Replace("\"enabled\":true", "\"enabled\":false"));
        FleetConfigStore store = Open();
        Assert.Equal(["docs.example.com"], store.Current.WebFetchAllowedHosts);
        store.Save(store.Current);
        Assert.Equal(["docs.example.com"], Open().Current.WebFetchAllowedHosts);
    }

    [Fact]
    public void Merging_tool_switches_keeps_the_web_fetch_entry_whole()
    {
        var webFetch = new FleetToolConfig(true, ["internal.example.test"], ["docs.example.com"]);
        Dictionary<string, FleetToolConfig>? merged = FleetConfigStore.MergeToolSwitches(
            new Dictionary<string, FleetToolConfig> { ["webFetch"] = webFetch, ["read_file"] = new(true) },
            new Dictionary<string, bool> { ["web_fetch"] = false, ["read_file"] = false, ["webFetch"] = false });

        Assert.NotNull(merged);
        Assert.Same(webFetch, merged["webFetch"]);
        Assert.Equal(["docs.example.com"], merged["webFetch"].AllowedHosts);
        Assert.Equal(["internal.example.test"], merged["webFetch"].AllowedInternalHosts);
        Assert.Equal(new FleetToolConfig(false), merged["web_fetch"]);
        Assert.Equal(new FleetToolConfig(false), merged["read_file"]);
    }

    [Fact]
    public void Merging_tool_switches_creates_tools_missing_from_the_current_config()
    {
        Dictionary<string, FleetToolConfig>? merged = FleetConfigStore.MergeToolSwitches(
            new Dictionary<string, FleetToolConfig>(),
            new Dictionary<string, bool> { ["web_search"] = true, ["webFetch"] = false });

        Assert.NotNull(merged);
        Assert.Equal(new FleetToolConfig(true), merged["web_search"]);
        Assert.Equal(new FleetToolConfig(false), merged["webFetch"]);
    }

    [Fact]
    public void Merging_tool_switches_without_a_request_changes_nothing()
    {
        var current = new Dictionary<string, FleetToolConfig> { ["webFetch"] = new(true, AllowedHosts: ["docs.example.com"]) };
        Assert.Null(FleetConfigStore.MergeToolSwitches(current, null));
        Assert.Null(FleetConfigStore.MergeToolSwitches(current, new Dictionary<string, bool>()));
    }

    [Fact]
    public void Saving_merged_tool_switches_keeps_both_lists()
    {
        Open().Save(Config(["docs.example.com"], ["internal.example.test"]));
        FleetConfigStore store = Open();
        FleetConfig current = store.Current;
        Dictionary<string, FleetToolConfig>? merged = FleetConfigStore.MergeToolSwitches(current.Tools,
            new Dictionary<string, bool> { ["web_fetch"] = false });
        store.Save(current with { Tools = merged! });

        FleetConfig reloaded = Open().Current;
        Assert.Equal(["docs.example.com"], reloaded.WebFetchAllowedHosts);
        Assert.Equal(["internal.example.test"], reloaded.WebFetchAllowedInternalHosts);
        Assert.Equal(["docs.example.com"], reloaded.Tools["webFetch"].AllowedHosts);
    }
}

