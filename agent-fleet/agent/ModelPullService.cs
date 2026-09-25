using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AgentFleet;

/// <summary>Where an Ollama machine's native API lives, from any of the forms people and the config use.</summary>
internal static class OllamaAddress
{
    /// <summary>"http://host:11434/v1", "http://host:11434" or "host" become the machine's API root.</summary>
    public static bool TryGetRoot(string? address, out Uri root)
    {
        root = null!;
        string value = (address ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return false;
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "http://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        // Same rule as the web UI: plain http with no port means Ollama's own port.
        int port = uri.IsDefaultPort ? (uri.Scheme == "http" ? 11434 : -1) : uri.Port;
        root = new UriBuilder(uri.Scheme, uri.Host, port, "/").Uri;
        return true;
    }

    public static Uri Endpoint(Uri root, string path) => new(root, path);

    /// <summary>True for this machine's own addresses: the ones a hub's local Ollama answers on.</summary>
    public static bool IsThisMachine(Uri root) =>
        root.IsLoopback ||
        string.Equals(root.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
        HubNetwork.LocalAddresses().Contains(root.Host, StringComparer.OrdinalIgnoreCase);
}

/// <summary>A model download in progress (or just finished) on one machine.</summary>
internal sealed record ModelPullStatus(
    string Id,
    string Url,
    string Model,
    string State,
    string Status,
    long Completed,
    long Total,
    string? Error,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc);

/// <summary>
/// Downloads models onto fleet machines through Ollama's own API (POST /api/pull), in the background,
/// so the web UI can offer a Download button with a progress bar instead of "open a terminal and run
/// ollama pull". A download keeps going if the browser is closed; the UI polls <see cref="List"/>.
/// </summary>
internal sealed partial class ModelPullService
{
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    private static readonly TimeSpan KeepFinished = TimeSpan.FromMinutes(15);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);

    public ModelPullService(IHttpClientFactory httpClientFactory, ILogger<ModelPullService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Raised when a download finishes, successfully or not, so cached health can be refreshed.</summary>
    public event Action<ModelPullStatus>? Finished;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/:-]{0,199}$")]
    private static partial Regex ModelNamePattern();

    public static bool IsValidModelName(string? model) => model is not null && ModelNamePattern().IsMatch(model.Trim());

    /// <summary>Starts downloading, or returns the download of the same model to the same machine already under way.</summary>
    public ModelPullStatus Start(Uri root, string model)
    {
        model = model.Trim();
        if (!IsValidModelName(model))
        {
            throw new ArgumentException($"'{model}' is not a model name Ollama accepts, for example qwen2.5-coder:7b.", nameof(model));
        }

        Prune();
        string id = IdOf(root, model);
        Job job = _jobs.AddOrUpdate(
            id,
            _ => Job.Begin(id, root, model),
            (_, existing) => existing.State == Running ? existing : Job.Begin(id, root, model));

        if (job.TryClaimStart())
        {
            _ = Task.Run(() => RunAsync(job));
        }

        return job.Snapshot();
    }

    public IReadOnlyList<ModelPullStatus> List()
    {
        Prune();
        return _jobs.Values.Select(job => job.Snapshot()).OrderBy(status => status.StartedAtUtc).ToList();
    }

    public bool Cancel(string id)
    {
        if (_jobs.TryGetValue(id, out Job? job) && job.State == Running)
        {
            job.Cancellation.Cancel();
            return true;
        }

        return false;
    }

    private static string IdOf(Uri root, string model) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{root.AbsoluteUri}|{model.ToLowerInvariant()}")))[..16].ToLowerInvariant();

    private void Prune()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - KeepFinished;
        foreach ((string id, Job job) in _jobs)
        {
            if (job.State != Running && job.FinishedAtUtc < cutoff)
            {
                _jobs.TryRemove(id, out _);
            }
        }
    }

    private async Task RunAsync(Job job)
    {
        _logger.LogInformation("Downloading {Model} onto {Machine}.", job.Model, job.Root);
        try
        {
            // The health client has no overall timeout: a big model can take an hour on a slow line.
            HttpClient client = _httpClientFactory.CreateClient(FleetHealthMonitor.HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, OllamaAddress.Endpoint(job.Root, "api/pull"))
            {
                Content = new StringContent(JsonSerializer.Serialize(new { model = job.Model, stream = true }), Encoding.UTF8, "application/json")
            };
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, job.Cancellation.Token);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(job.Cancellation.Token);
                job.Fail(ExplainError(ReadError(body) ?? $"HTTP {(int)response.StatusCode}", job.Model));
                return;
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(job.Cancellation.Token);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = await reader.ReadLineAsync(job.Cancellation.Token)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (!job.Apply(line))
                {
                    break;
                }
            }

            job.Complete();
        }
        catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
        {
            job.Cancel();
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            job.Fail($"Could not reach Ollama at {job.Root}: {exception.Message}");
        }
        finally
        {
            ModelPullStatus final = job.Snapshot();
            _logger.LogInformation("Download of {Model} onto {Machine}: {State}{Error}.",
                final.Model, final.Url, final.State, final.Error is null ? string.Empty : $" ({final.Error})");
            Finished?.Invoke(final);
        }
    }

    private static string? ReadError(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out JsonElement error) ? error.GetString() : null;
        }
        catch (JsonException)
        {
            return string.IsNullOrWhiteSpace(body) ? null : body.Trim();
        }
    }

    internal static string ExplainError(string error, string model) =>
        error.Contains("file does not exist", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("manifest unknown", StringComparison.OrdinalIgnoreCase)
            ? $"Ollama has no model called {model}. Check the name on https://ollama.com/library."
            : error.Contains("no space left", StringComparison.OrdinalIgnoreCase)
                ? $"That machine ran out of disk space while downloading {model}."
                : error;

    private sealed class Job
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, (long Completed, long Total)> _layers = new(StringComparer.Ordinal);
        private int _started;

        private Job(string id, Uri root, string model)
        {
            Id = id;
            Root = root;
            Model = model;
        }

        public string Id { get; }

        public Uri Root { get; }

        public string Model { get; }

        public CancellationTokenSource Cancellation { get; } = new();

        public string State { get; private set; } = Running;

        public string Status { get; private set; } = "starting";

        public string? Error { get; private set; }

        public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;

        public DateTimeOffset? FinishedAtUtc { get; private set; }

        public static Job Begin(string id, Uri root, string model) => new(id, root, model);

        public bool TryClaimStart() => Interlocked.Exchange(ref _started, 1) == 0;

        /// <summary>Reads one line of Ollama's progress stream. False when it reported an error.</summary>
        public bool Apply(string line)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            lock (_gate)
            {
                if (root.TryGetProperty("error", out JsonElement error))
                {
                    Error = ExplainError(error.GetString() ?? "Ollama reported an error.", Model);
                    State = Failed;
                    FinishedAtUtc = DateTimeOffset.UtcNow;
                    return false;
                }

                if (root.TryGetProperty("status", out JsonElement status))
                {
                    Status = status.GetString() ?? Status;
                }

                if (root.TryGetProperty("digest", out JsonElement digest) && digest.GetString() is { } layer &&
                    root.TryGetProperty("total", out JsonElement total) && total.TryGetInt64(out long totalBytes))
                {
                    long completed = root.TryGetProperty("completed", out JsonElement done) && done.TryGetInt64(out long doneBytes) ? doneBytes : 0;
                    _layers[layer] = (Math.Min(completed, totalBytes), totalBytes);
                }

                return true;
            }
        }

        public void Complete()
        {
            lock (_gate)
            {
                if (State != Running)
                {
                    return;
                }

                // Ollama ends a good download with "success"; a stream that stops short of it did not finish.
                if (string.Equals(Status, "success", StringComparison.OrdinalIgnoreCase))
                {
                    State = Done;
                }
                else
                {
                    State = Failed;
                    Error = "The download stopped before it finished. Try again; Ollama resumes where it left off.";
                }

                FinishedAtUtc = DateTimeOffset.UtcNow;
            }
        }

        public void Fail(string error)
        {
            lock (_gate)
            {
                State = Failed;
                Error = error;
                FinishedAtUtc = DateTimeOffset.UtcNow;
            }
        }

        public void Cancel()
        {
            lock (_gate)
            {
                State = Cancelled;
                Status = "cancelled";
                FinishedAtUtc = DateTimeOffset.UtcNow;
            }
        }

        public ModelPullStatus Snapshot()
        {
            lock (_gate)
            {
                long completed = _layers.Values.Sum(layer => layer.Completed);
                long total = _layers.Values.Sum(layer => layer.Total);
                return new ModelPullStatus(Id, Root.AbsoluteUri, Model, State, Status, completed, total, Error, StartedAtUtc, FinishedAtUtc);
            }
        }
    }
}
