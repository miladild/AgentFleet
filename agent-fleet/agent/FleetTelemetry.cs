using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace AgentFleet;

internal static class FleetTelemetry
{
    internal const string SourceName = "AgentFleet";
    internal static readonly ActivitySource Source = new(SourceName);
    private const string ModelSourceName = "AgentFleet.Model";
    private const string AgentSourceName = "AgentFleet.Agent";

    private static int _liveProviders;

    /// <summary>True only while a provider returned by Configure has not been disposed.</summary>
    public static bool Enabled => Volatile.Read(ref _liveProviders) > 0;

    /// <summary>
    /// Returns null (telemetry off) for an empty or "off" setting, and for any value it does not recognise: a typo must
    /// not stop the backend at startup, so it logs one warning and carries on without telemetry.
    /// </summary>
    public static IDisposable? Configure(string? setting, string logsFolder, ILogger? logger = null)
    {
        string mode = string.IsNullOrWhiteSpace(setting) ? "off" : setting.Trim().ToLowerInvariant();
        if (mode == "off") return null;
        if (mode is not ("file" or "otlp"))
        {
            logger?.LogWarning("FLEET_TELEMETRY \"{Value}\" is not recognised; use off, file or otlp. Telemetry stays off.", setting);
            return null;
        }

        // Registered before the exporter, so every exporter sees only the allow-listed tags.
        TracerProviderBuilder builder = Sdk.CreateTracerProviderBuilder()
            .AddSource(SourceName, ModelSourceName, AgentSourceName, "Microsoft.Extensions.AI", "Microsoft.Agents.AI")
            .AddProcessor(new AllowListActivityProcessor());
        if (mode == "file")
        {
            Directory.CreateDirectory(logsFolder);
            builder.AddProcessor(new SimpleActivityExportProcessor(new JsonLinesActivityExporter(logsFolder)));
        }
        else
        {
            builder.AddOtlpExporter();
        }
        TracerProvider traces = builder.Build();

        // Only the otlp mode has a reader for metrics; a meter provider in file mode would collect and drop everything.
        MeterProvider? meters = null;
        if (mode == "otlp")
        {
            meters = Sdk.CreateMeterProviderBuilder()
                .AddMeter("Microsoft.Extensions.AI", "Microsoft.Agents.AI")
                .AddOtlpExporter()
                .Build();
        }

        Interlocked.Increment(ref _liveProviders);
        return new TelemetryProviders(traces, meters);
    }

    /// <summary>
    /// Wraps a model client in span telemetry only while telemetry is on; with it off the same instance comes back, so
    /// the object graph is exactly what it was without the feature. Sensitive data stays off: no prompt or tool text.
    /// </summary>
    public static IChatClient Instrument(IChatClient client, ILoggerFactory loggerFactory) =>
        Enabled
            ? new ChatClientBuilder(client)
                .UseOpenTelemetry(loggerFactory, ModelSourceName, telemetry => telemetry.EnableSensitiveData = false)
                .Build()
            : client;

    /// <summary>The agent-level counterpart of the model wrapper: one span per agent run, same rules.</summary>
    public static AIAgent Instrument(AIAgent agent, IServiceProvider services) =>
        Enabled
            ? agent.AsBuilder()
                .UseOpenTelemetry(AgentSourceName, telemetry => telemetry.EnableSensitiveData = false)
                .Build(services)
            : agent;

    /// <summary>
    /// The tags a span may carry out of the process. This is an allow-list, not a block-list: the framework adds tags for
    /// tool schemas, messages and instructions, and a release that adds a content-bearing tag must not leak through it.
    /// Tool schemas alone are kilobytes per model call, so they are dropped for size as well as for privacy.
    /// </summary>
    internal static readonly string[] AllowedTagNames =
    [
        "gen_ai.operation.name",
        "gen_ai.provider.name",
        "gen_ai.request.model",
        "gen_ai.response.model",
        "gen_ai.response.id",
        "gen_ai.response.finish_reasons",
        "gen_ai.tool.name",
        "gen_ai.tool.call.id",
        "gen_ai.agent.id",
        "gen_ai.agent.name",
        "gen_ai.conversation.id",
        "error.type",
    ];

    private static readonly string[] AllowedTagPrefixes = ["fleet.", "gen_ai.usage."];

    /// <summary>Longest string tag value written; a longer value is cut and marked, as a second guard for any tag we allow.</summary>
    internal const int MaxTagTextLength = 256;
    internal const string TruncationMarker = "...";

    internal static bool IsAllowedTag(string key) =>
        AllowedTagNames.Contains(key, StringComparer.Ordinal) ||
        AllowedTagPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal));

    internal static object? LimitTagValue(object? value) =>
        value is string text && text.Length > MaxTagTextLength ? text[..MaxTagTextLength] + TruncationMarker : value;

    /// <summary>Removes every tag that is not allowed and caps the rest, before any exporter sees the span.</summary>
    internal sealed class AllowListActivityProcessor : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data)
        {
            foreach (KeyValuePair<string, object?> tag in data.TagObjects.ToArray())
            {
                if (!IsAllowedTag(tag.Key))
                {
                    data.SetTag(tag.Key, null);
                    continue;
                }
                object? limited = LimitTagValue(tag.Value);
                if (!ReferenceEquals(limited, tag.Value)) data.SetTag(tag.Key, limited);
            }
        }
    }

    private sealed class TelemetryProviders(TracerProvider traces, MeterProvider? meters) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Interlocked.Decrement(ref _liveProviders);
            meters?.Dispose();
            traces.Dispose();
        }
    }

    public static Activity? StartPlanRound(string planId, int stepId, int attempt, int round, int rung,
        string? workspaceNode, string? modelNode) => StartPlan("plan.round", planId, stepId, attempt, round, rung, workspaceNode, modelNode);

    public static Activity? StartPlanCheck(string planId, int stepId, int attempt, int round, int rung,
        string? workspaceNode, string? modelNode) => StartPlan("plan.check", planId, stepId, attempt, round, rung, workspaceNode, modelNode);

    private static Activity? StartPlan(string name, string planId, int stepId, int attempt, int round, int rung,
        string? workspaceNode, string? modelNode)
    {
        Activity? activity = Source.StartActivity(name, ActivityKind.Internal);
        activity?.SetTag("fleet.plan_id", planId);
        activity?.SetTag("fleet.step_id", stepId);
        activity?.SetTag("fleet.attempt", attempt);
        activity?.SetTag("fleet.round", round);
        activity?.SetTag("fleet.rung", rung);
        activity?.SetTag("fleet.workspace_node", workspaceNode);
        activity?.SetTag("fleet.model_node", modelNode);
        return activity;
    }

    private sealed class JsonLinesActivityExporter(string folder) : BaseExporter<Activity>
    {
        private readonly object _gate = new();

        public override ExportResult Export(in Batch<Activity> batch)
        {
            try
            {
                lock (_gate)
                {
                    foreach (Activity activity in batch)
                    {
                        DateTime spanDate = activity.StartTimeUtc.ToLocalTime().Date;
                        string path = Path.Combine(folder, $"telemetry-{spanDate:yyyyMMdd}.jsonl");
                        var item = new Dictionary<string, object?>
                        {
                            ["name"] = activity.DisplayName,
                            ["trace_id"] = activity.TraceId.ToHexString(),
                            ["span_id"] = activity.SpanId.ToHexString(),
                            ["parent_span_id"] = activity.ParentSpanId == default ? null : activity.ParentSpanId.ToHexString(),
                            ["kind"] = activity.Kind.ToString(),
                            ["start_time_utc"] = activity.StartTimeUtc,
                            ["duration_ms"] = activity.Duration.TotalMilliseconds,
                            ["status"] = activity.Status.ToString(),
                            ["tags"] = activity.TagObjects.Where(tag => tag.Value is not null)
                                .ToDictionary(tag => tag.Key, tag => LimitTagValue(tag.Value))
                        };
                        File.AppendAllText(path, JsonSerializer.Serialize(item) + Environment.NewLine);
                    }

                    DateTime today = DateTime.Now.Date;
                    foreach (string oldFile in Directory.EnumerateFiles(folder, "telemetry-????????.jsonl"))
                    {
                        if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(oldFile).AsSpan("telemetry-".Length),
                            "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out DateTime date) && date.Date < today.AddDays(-13))
                            File.Delete(oldFile);
                    }
                }
                return ExportResult.Success;
            }
            catch
            {
                return ExportResult.Failure;
            }
        }
    }
}
