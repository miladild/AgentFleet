using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentFleet.Tests;

public sealed class WebFetchTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fleet-webfetch-tests-" + Guid.NewGuid().ToString("N"));

    public WebFetchTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static FleetConfig Config(
        IReadOnlyList<string>? allowedHosts = null,
        string nodeUrl = "http://192.0.2.80:11434/v1") =>
        new(
            "triage",
            "conservative",
            false,
            [new FleetNodeConfig("hub", nodeUrl, "model", "local", FleetTiers.Heavy, Fallback: true)],
            new Dictionary<string, FleetToolConfig>(StringComparer.Ordinal)
            {
                ["webFetch"] = new(true, allowedHosts)
            });

    private static Task<string> Fetch(
        string url,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        Func<Uri, IReadOnlyList<IPAddress>, HttpMessageHandler> handlerFactory,
        IReadOnlyList<string>? allowedHosts = null,
        string nodeUrl = "http://192.0.2.80:11434/v1") =>
        WebTools.FetchUrlAsync(url, Config(allowedHosts, nodeUrl), NullLogger.Instance, CancellationToken.None, resolve, handlerFactory);

    private sealed class ReplyHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<Uri?> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri);
            return Task.FromResult(reply(request));
        }
    }

    private sealed class CountingStream : Stream
    {
        private readonly int _length;
        private int _remaining;

        public CountingStream(int length)
        {
            _length = length;
            _remaining = length;
        }

        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = Math.Min(count, _remaining);
            Array.Fill(buffer, (byte)'a', offset, read);
            _remaining -= read;
            BytesRead += read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = Math.Min(buffer.Length, _remaining);
            buffer.Span[..read].Fill((byte)'a');
            _remaining -= read;
            BytesRead += read;
            return ValueTask.FromResult(read);
        }
    }

    private static HttpResponseMessage Ok(string body = "readable page") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body)
    };

    private static string Refusal(string host) =>
        $"web_fetch only reads public internet addresses; {host} is on this computer or the local network.";

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.2.3.4")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.4.5")]
    [InlineData("169.254.1.2")]
    [InlineData("100.64.1.2")]
    [InlineData("100.127.255.254")]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("[::]")]
    [InlineData("[::1]")]
    [InlineData("[fe80::1]")]
    [InlineData("[fd12::1]")]
    [InlineData("[ff02::1]")]
    [InlineData("[::ffff:127.0.0.1]")]
    public async Task An_internal_or_non_routable_literal_is_refused_before_the_handler(string host)
    {
        bool handlerCalled = false;
        string result = await Fetch(
            $"http://{host}/page",
            WebFetchPolicy.ResolveAsync,
            (_, _) =>
            {
                handlerCalled = true;
                return new ReplyHandler(_ => Ok());
            });

        Assert.Equal(Refusal(new Uri($"http://{host}/page").Host.Trim('[', ']')), result);
        Assert.False(handlerCalled);
    }

    [Theory]
    [InlineData("http://printer/page")]
    [InlineData("http://docs.local/page")]
    [InlineData("http://docs.lan/page")]
    [InlineData("http://docs.internal/page")]
    [InlineData("http://docs.localhost/page")]
    public async Task A_local_name_is_refused_without_resolving_it(string url)
    {
        bool resolved = false;
        bool handlerCalled = false;
        Uri uri = new(url);

        string result = await Fetch(
            url,
            (host, _) =>
            {
                resolved = true;
                return Task.FromResult(new[] { IPAddress.Parse("192.0.2.90") });
            },
            (_, _) =>
            {
                handlerCalled = true;
                return new ReplyHandler(_ => Ok());
            });

        Assert.Equal(Refusal(uri.Host), result);
        Assert.False(resolved);
        Assert.False(handlerCalled);
    }

    [Fact]
    public async Task A_public_literal_is_allowed_without_real_network_access()
    {
        var handler = new ReplyHandler(_ => Ok());
        IReadOnlyList<IPAddress>? connectedAddresses = null;

        string result = await Fetch(
            "https://192.0.2.90/page",
            WebFetchPolicy.ResolveAsync,
            (_, addresses) =>
            {
                connectedAddresses = addresses;
                return handler;
            });

        Assert.Equal("readable page", result);
        Assert.Equal("https://192.0.2.90/page", Assert.Single(handler.Requests)?.ToString());
        Assert.Equal(IPAddress.Parse("192.0.2.90"), Assert.Single(connectedAddresses!));
    }

    [Fact]
    public async Task A_public_name_with_any_private_dns_answer_is_refused()
    {
        int resolverCalls = 0;
        bool handlerCalled = false;

        string result = await Fetch(
            "https://public.example.test/page",
            (_, _) =>
            {
                resolverCalls++;
                return Task.FromResult(new[] { IPAddress.Parse("192.0.2.90"), IPAddress.Parse("10.0.0.9") });
            },
            (_, _) =>
            {
                handlerCalled = true;
                return new ReplyHandler(_ => Ok());
            });

        Assert.Equal(Refusal("public.example.test"), result);
        Assert.Equal(1, resolverCalls);
        Assert.False(handlerCalled);
    }

    [Fact]
    public async Task A_redirect_to_loopback_is_refused_at_the_redirect_hop()
    {
        int resolverCalls = 0;
        var firstHandler = new ReplyHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("http://127.0.0.1/private") }
        });
        int handlersCreated = 0;

        string result = await Fetch(
            "https://public.example.test/start",
            (host, _) =>
            {
                resolverCalls++;
                return Task.FromResult(new[]
                {
                    IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? literal)
                        ? literal
                        : IPAddress.Parse("192.0.2.90")
                });
            },
            (_, _) =>
            {
                handlersCreated++;
                return firstHandler;
            });

        Assert.Equal(Refusal("127.0.0.1"), result);
        Assert.Equal(2, resolverCalls);
        Assert.Equal(1, handlersCreated);
        Assert.Single(firstHandler.Requests);
    }

    [Fact]
    public async Task An_explicit_internal_host_can_be_fetched()
    {
        var handler = new ReplyHandler(_ => Ok());
        IReadOnlyList<IPAddress>? connectedAddresses = null;

        string result = await Fetch(
            "http://docs.example.test/guide",
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.168.1.50") }),
            (_, addresses) =>
            {
                connectedAddresses = addresses;
                return handler;
            },
            ["docs.example.test"]);

        Assert.Equal("readable page", result);
        Assert.Single(handler.Requests);
        Assert.Equal(IPAddress.Parse("192.168.1.50"), Assert.Single(connectedAddresses!));
    }

    [Fact]
    public async Task An_explicit_internal_address_can_be_fetched_through_a_name()
    {
        var handler = new ReplyHandler(_ => Ok());
        string result = await Fetch(
            "http://docs.example.test/guide",
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.168.1.50") }),
            (_, _) => handler,
            ["192.168.1.50"]);

        Assert.Equal("readable page", result);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_configured_fleet_node_host_is_always_refused()
    {
        bool handlerCalled = false;
        string result = await Fetch(
            "http://node.example.test/api/fleet-config",
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.0.2.90") }),
            (_, _) =>
            {
                handlerCalled = true;
                return new ReplyHandler(_ => Ok());
            },
            ["node.example.test"],
            "http://node.example.test:11434/v1");

        Assert.Equal(Refusal("node.example.test"), result);
        Assert.False(handlerCalled);
    }

    [Fact]
    public async Task A_configured_fleet_node_address_is_refused_even_through_an_alias()
    {
        bool handlerCalled = false;
        string result = await Fetch(
            "http://node-alias.example.test/page",
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.0.2.80") }),
            (_, _) =>
            {
                handlerCalled = true;
                return new ReplyHandler(_ => Ok());
            });

        Assert.Equal(Refusal("node-alias.example.test"), result);
        Assert.False(handlerCalled);
    }

    [Fact]
    public async Task An_allowed_name_that_resolves_to_a_fleet_node_named_by_host_is_refused()
    {
        bool handlerCalled = false;
        string result = await Fetch(
            "http://docs.example.test/guide",
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.168.1.50") }),
            (_, _) =>
            {
                handlerCalled = true;
                return new ReplyHandler(_ => Ok());
            },
            ["docs.example.test"],
            "http://hub.example.test:11434/v1");

        Assert.Equal(Refusal("docs.example.test"), result);
        Assert.False(handlerCalled);
    }

    [Fact]
    public async Task A_public_name_that_resolves_to_a_fleet_node_named_by_host_is_refused()
    {
        bool handlerCalled = false;
        string result = await Fetch(
            "http://public.example.test/page",
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.0.2.80") }),
            (_, _) =>
            {
                handlerCalled = true;
                return new ReplyHandler(_ => Ok());
            },
            nodeUrl: "http://hub.example.test:11434/v1");

        Assert.Equal(Refusal("public.example.test"), result);
        Assert.False(handlerCalled);
    }

    [Fact]
    public async Task An_unresolvable_fleet_node_name_does_not_stop_an_ordinary_public_fetch()
    {
        var resolvedHosts = new List<string>();
        var handler = new ReplyHandler(_ => Ok());
        string result = await Fetch(
            "https://public.example.test/page",
            (host, _) =>
            {
                resolvedHosts.Add(host);
                if (host == "hub.example.test")
                {
                    throw new SocketException((int)SocketError.HostNotFound);
                }

                return Task.FromResult(new[] { IPAddress.Parse("192.0.2.90") });
            },
            (_, _) => handler,
            nodeUrl: "http://hub.example.test:11434/v1");

        Assert.Equal("readable page", result);
        Assert.Contains("hub.example.test", resolvedHosts);
        Assert.Equal("https://public.example.test/page", Assert.Single(handler.Requests)?.ToString());
    }

    [Theory]
    [InlineData("[::7f00:1]")]
    [InlineData("[::127.0.0.1]")]
    [InlineData("[2002:7f00:1::]")]
    [InlineData("[2002:0a00:0009::]")]
    [InlineData("[64:ff9b::7f00:1]")]
    public async Task An_ipv6_form_that_embeds_an_internal_ipv4_address_is_refused_before_the_handler(string host)
    {
        bool handlerCalled = false;
        string result = await Fetch(
            $"http://{host}/page",
            WebFetchPolicy.ResolveAsync,
            (_, _) =>
            {
                handlerCalled = true;
                return new ReplyHandler(_ => Ok());
            });

        Assert.Equal(Refusal(new Uri($"http://{host}/page").Host.Trim('[', ']')), result);
        Assert.False(handlerCalled);
    }

    [Fact]
    public async Task A_6to4_form_of_a_documentation_ipv4_address_is_not_refused_by_the_embedded_rule()
    {
        var handler = new ReplyHandler(_ => Ok());
        string result = await Fetch(
            "http://[2002:c000:0204::]/page",
            WebFetchPolicy.ResolveAsync,
            (_, _) => handler);

        Assert.Equal("readable page", result);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task The_connection_uses_only_the_address_that_was_checked()
    {
        IPAddress checkedAddress = IPAddress.Parse("192.0.2.90");
        string? connectedAddress = null;
        await using Stream _ = await WebFetchPolicy.ConnectToCheckedAddressesAsync(
            new DnsEndPoint("public.example.test", 443),
            [checkedAddress],
            (address, _, _) =>
            {
                connectedAddress = address;
                return ValueTask.FromResult<Stream>(new MemoryStream());
            },
            CancellationToken.None);

        Assert.Equal(checkedAddress.ToString(), connectedAddress);
    }

    [Fact]
    public void The_fetch_handler_disables_proxy_and_automatic_redirects()
    {
        using SocketsHttpHandler handler = WebFetchPolicy.CreateHandler(
            new Uri("https://public.example.test/page"),
            [IPAddress.Parse("192.0.2.90")]);

        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public async Task The_body_reader_stops_at_two_megabytes_and_keeps_the_text_truncation()
    {
        var content = new CountingStream(3 * 1024 * 1024);
        var handler = new ReplyHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(content)
        });

        string result = await Fetch(
            "https://192.0.2.90/page",
            WebFetchPolicy.ResolveAsync,
            (_, _) => handler);

        Assert.Equal(2 * 1024 * 1024, content.BytesRead);
        Assert.Contains("[truncated:", result);
        Assert.StartsWith(new string('a', 12_000), result);
    }

    [Fact]
    public void Allowed_internal_hosts_are_normalized_and_bad_entries_are_rejected()
    {
        Assert.Equal(
            ["192.168.1.10", "docs.example.test"],
            WebFetchPolicy.NormalizeAllowedInternalHosts([" Docs.Example.Test. ", "192.168.1.10", "docs.example.test"]));
        Assert.Throws<InvalidOperationException>(() => WebFetchPolicy.NormalizeAllowedInternalHosts(["https://docs.example.test/path"]));
    }

    [Fact]
    public void The_config_loads_normalizes_and_saves_the_web_fetch_exception_list()
    {
        string path = Path.Combine(_directory, "fleet.config.json");
        File.WriteAllText(path,
            "{\"triageModel\":\"triage\",\"mode\":\"conservative\",\"planModeEnabled\":false," +
            "\"nodes\":[{\"name\":\"hub\",\"url\":\"http://192.0.2.80:11434/v1\",\"model\":\"m\",\"purpose\":\"local\",\"fallback\":true}]," +
            "\"tools\":{\"webFetch\":{\"allowedInternalHosts\":[\"Docs.Example.Test.\",\"192.168.1.10\"]}}}");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FLEET_CONFIG_PATH"] = path })
            .Build();

        FleetConfigStore store = new(configuration);
        FleetConfig config = store.Current;

        Assert.Equal(["192.168.1.10", "docs.example.test"], config.WebFetchAllowedInternalHosts);
        store.Save(config);
        Assert.Contains("\"webFetch\"", File.ReadAllText(path));
        Assert.Contains("\"docs.example.test\"", File.ReadAllText(path));
    }
}
