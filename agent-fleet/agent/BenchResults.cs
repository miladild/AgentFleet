using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace AgentFleet;

// Results of bench/Invoke-Bench.ps1 batches, imported by hand from results.jsonl and run.json. The counts say how
// many runs each machine and model did and how they ended. They never rank models and never measure speed.

/// <summary>One stored run. Stored as one JSON line; the import never changes a stored line.</summary>
internal sealed record BenchResultRow(
    string ImportId,
    DateTimeOffset ImportedAtUtc,
    string? Label,
    string PlanId,
    string Task,
    int? Run,
    string Status,
    bool HiddenPass,
    bool FalseAccept,
    bool Completed,
    bool TimedOut,
    int StepsDone,
    int StepsParked,
    string ParkCauses,
    IReadOnlyList<string> Nodes,
    string Model,
    DateTimeOffset? StartedAtUtc,
    bool Errored);

internal sealed record BenchModelGroup(
    string Node,
    string Model,
    int Runs,
    int DoneAndCorrect,
    int FalseAccepts,
    int Parked,
    int TimedOut,
    int Errors,
    IReadOnlyList<string> Tasks,
    bool TooFew,
    DateTimeOffset LastRunUtc);

internal sealed record BenchImportSummary(string ImportId, string? Label, DateTimeOffset ImportedAtUtc, int Rows);

internal sealed record BenchModelsResult(int? Days, IReadOnlyList<BenchModelGroup> Groups, IReadOnlyList<BenchImportSummary> Imports);

internal sealed record BenchImportResult(int Imported, int Duplicates, int Skipped, IReadOnlyList<string> Models);

/// <summary>An import the endpoint answers with <see cref="StatusCode"/> and the reason, instead of a result.</summary>
internal sealed class BenchImportRefusedException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

internal sealed class BenchResultStore
{
    public const int MaximumDays = 3650;
    public const int MinimumRunsToJudge = 5;
    public const int MaximumResultsBytes = 2 * 1024 * 1024;

    // The text is JSON inside the request body, where quotes and line breaks are escaped, so the body allows more than the text.
    public const int MaximumBodyBytes = 8 * 1024 * 1024;
    public const int MaximumLabelLength = 80;

    private const string FileName = "bench-results.jsonl";
    private const string NotRecorded = "not recorded";
    private const string Mixed = "mixed";
    private const string Done = "done";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly RunDetails NoRun = new(null, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private readonly string _path;
    private readonly object _gate = new();

    public BenchResultStore(string labDirectory)
    {
        string directory = Path.GetFullPath(labDirectory);
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, FileName);
    }

    public static bool IsValidDays(int? days) => days is null or >= 1 and <= MaximumDays;

    /// <summary>
    /// Keeps the runs of one import that are not stored yet. A run is recognised by its plan id, so importing the same
    /// file again adds nothing. Throws <see cref="BenchImportRefusedException"/> for a body that cannot be an import.
    /// </summary>
    public async Task<BenchImportResult> ImportAsync(Stream body, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        byte[] bytes = await ReadBoundedAsync(body, MaximumBodyBytes, cancellationToken)
            ?? throw Refused(StatusCodes.Status413PayloadTooLarge, $"The import is larger than {MaximumBodyBytes / (1024 * 1024)} MB.");
        ImportEnvelope envelope = ParseEnvelope(bytes);

        // Some editors start a saved file with a byte-order mark, which is not part of the JSON lines.
        string resultsText = (envelope.ResultsJsonl ?? string.Empty).TrimStart((char)0xFEFF);
        if (Encoding.UTF8.GetByteCount(resultsText) > MaximumResultsBytes)
        {
            throw Refused(StatusCodes.Status413PayloadTooLarge, $"The results file is larger than {MaximumResultsBytes / (1024 * 1024)} MB.");
        }

        if (string.IsNullOrWhiteSpace(resultsText))
        {
            throw Refused(StatusCodes.Status400BadRequest, "The results file is empty.");
        }

        string? label = string.IsNullOrWhiteSpace(envelope.Label) ? null : envelope.Label.Trim();
        // Counted in characters, not UTF-16 units: an emoji is one character to the person who typed it.
        if (label is not null && label.EnumerateRunes().Count() > MaximumLabelLength)
        {
            throw Refused(StatusCodes.Status400BadRequest, $"The label can be at most {MaximumLabelLength} characters.");
        }

        // The machines and models come from the run record when it has any; the models map is for a batch without one.
        RunDetails run = ParseRun(envelope.Run);
        IReadOnlyDictionary<string, string> models = run.ModelsByNode.Count > 0 ? run.ModelsByNode : ModelMap(envelope.Models);
        List<BenchResultRow> rows = [];
        int skipped = 0;
        foreach (string line in resultsText.Split('\n'))
        {
            // A byte-order mark is not whitespace to Trim, and a joined file can carry one at the top of a later line too.
            string trimmed = line.TrimStart((char)0xFEFF).Trim();
            if (trimmed.Length == 0) continue;
            BenchResultRow? row = ParseRow(trimmed, nowUtc, label, run.StartedAtUtc, models);
            if (row is null) skipped++;
            else rows.Add(row);
        }

        lock (_gate)
        {
            HashSet<string> stored = ReadRows().Select(row => row.PlanId).ToHashSet(StringComparer.Ordinal);
            string importId = Guid.NewGuid().ToString("N");
            List<BenchResultRow> fresh = [];
            int duplicates = 0;
            foreach (BenchResultRow row in rows)
            {
                if (stored.Add(row.PlanId)) fresh.Add(row with { ImportId = importId });
                else duplicates++;
            }

            if (fresh.Count > 0) Append(fresh);
            return new BenchImportResult(fresh.Count, duplicates, skipped, ModelNames(rows));
        }
    }

    /// <summary>Counts by machine and model. A window keeps runs that started within the last <paramref name="days"/> days.</summary>
    public BenchModelsResult Models(int? days, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            IReadOnlyList<BenchResultRow> rows = ReadRows();
            DateTimeOffset? since = days is null ? null : nowUtc.AddDays(-days.Value);
            BenchModelGroup[] groups = rows
                .Where(row => since is null || AsOf(row) >= since.Value)
                .GroupBy(row => (Node: NodeKey(row.Nodes), row.Model))
                .Select(grouped => Summarize(grouped.Key.Node, grouped.Key.Model, grouped.ToArray()))
                .OrderBy(group => group.Node, StringComparer.OrdinalIgnoreCase)
                .ThenBy(group => group.Model, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            BenchImportSummary[] imports = rows
                .GroupBy(row => row.ImportId, StringComparer.Ordinal)
                .Select(grouped => new BenchImportSummary(grouped.Key, grouped.First().Label, grouped.First().ImportedAtUtc, grouped.Count()))
                .OrderByDescending(summary => summary.ImportedAtUtc)
                .ToArray();
            return new BenchModelsResult(days, groups, imports);
        }
    }

    /// <summary>Removes every run of one import. Returns how many were removed, or null when no run has that import id.</summary>
    public int? Delete(string importId)
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return null;
            List<string> kept = [];
            int removed = 0;
            foreach (string line in File.ReadAllLines(_path))
            {
                // A line that cannot be read is kept as it is, so a rewrite never drops data it does not understand.
                if (string.Equals(TryReadRow(line)?.ImportId, importId, StringComparison.Ordinal)) removed++;
                else kept.Add(line);
            }

            if (removed == 0) return null;
            string temporary = _path + ".tmp";
            File.WriteAllLines(temporary, kept, Utf8);
            File.Move(temporary, _path, overwrite: true);
            return removed;
        }
    }

    private static BenchModelGroup Summarize(string node, string model, IReadOnlyList<BenchResultRow> rows) => new(
        node,
        model,
        rows.Count,
        rows.Count(row => row.Status == Done && row.HiddenPass && !row.FalseAccept),
        rows.Count(row => row.FalseAccept),
        rows.Count(row => row.StepsParked > 0),
        rows.Count(row => row.TimedOut),
        rows.Count(row => row.Errored),
        rows.Select(row => row.Task).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
        rows.Count < MinimumRunsToJudge,
        rows.Max(row => AsOf(row)));

    private static DateTimeOffset AsOf(BenchResultRow row) => row.StartedAtUtc ?? row.ImportedAtUtc;

    private static BenchResultRow? ParseRow(string line, DateTimeOffset importedAtUtc, string? label, DateTimeOffset? startedAtUtc,
        IReadOnlyDictionary<string, string> models)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? planId = Text(root, "planId")?.Trim();
            string? task = Text(root, "task")?.Trim();
            string? status = Text(root, "status")?.Trim();
            if (string.IsNullOrEmpty(planId) || string.IsNullOrEmpty(task) || string.IsNullOrEmpty(status)) return null;

            bool hiddenPass = Flag(root, "hiddenPass");
            IReadOnlyList<string> nodes = SplitNodes(Text(root, "models"));
            return new BenchResultRow(
                ImportId: string.Empty,
                ImportedAtUtc: importedAtUtc,
                Label: label,
                PlanId: planId,
                Task: task,
                Run: Integer(root, "run"),
                Status: status,
                HiddenPass: hiddenPass,
                // Derived, never copied: a done plan whose hidden tests failed is a false accept whatever the line says.
                FalseAccept: status == Done && !hiddenPass,
                Completed: Flag(root, "completed"),
                TimedOut: Flag(root, "timedOut"),
                StepsDone: Integer(root, "stepsDone") ?? 0,
                StepsParked: Integer(root, "stepsParked") ?? 0,
                ParkCauses: Text(root, "parkCauses") ?? string.Empty,
                Nodes: nodes,
                Model: ResolveModel(nodes, models),
                StartedAtUtc: startedAtUtc,
                Errored: !string.IsNullOrWhiteSpace(Text(root, "error")));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static RunDetails ParseRun(JsonElement run)
    {
        if (run.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return NoRun;
        string text = run.ValueKind == JsonValueKind.String ? run.GetString() ?? string.Empty : run.GetRawText();
        if (string.IsNullOrWhiteSpace(text)) return NoRun;
        try
        {
            using JsonDocument document = JsonDocument.Parse(text.TrimStart((char)0xFEFF));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Refused(StatusCodes.Status400BadRequest, RunMessage);
            DateTimeOffset? startedAtUtc = null;
            if (Text(root, "startedAtUtc") is { } started &&
                DateTimeOffset.TryParse(started, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
            {
                startedAtUtc = parsed;
            }

            var models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("nodes", out JsonElement nodes) && nodes.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement node in nodes.EnumerateArray())
                {
                    string? name = Text(node, "name");
                    string? model = Text(node, "model");
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(model)) models.TryAdd(name.Trim(), model.Trim());
                }
            }

            return new RunDetails(startedAtUtc, models);
        }
        catch (JsonException)
        {
            throw Refused(StatusCodes.Status400BadRequest, RunMessage);
        }
    }

    private const string RunMessage = "The run record is not a JSON object such as the run.json that a batch writes.";

    private static IReadOnlyDictionary<string, string> ModelMap(Dictionary<string, string?>? models)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (models is null) return map;
        foreach ((string node, string? model) in models)
        {
            if (!string.IsNullOrWhiteSpace(node) && !string.IsNullOrWhiteSpace(model)) map.TryAdd(node.Trim(), model.Trim());
        }

        return map;
    }

    // One machine is the model it ran. Several machines with the same model are that model. Several machines with
    // different models are "mixed". A machine without a known model makes the row "not recorded", never a guess.
    private static string ResolveModel(IReadOnlyList<string> nodes, IReadOnlyDictionary<string, string> models)
    {
        if (nodes.Count == 0) return NotRecorded;
        string[] known = nodes.Where(models.ContainsKey).Select(node => models[node]).ToArray();
        string[] distinct = known.Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length > 1) return Mixed;
        return distinct.Length == 1 && known.Length == nodes.Count ? distinct[0] : NotRecorded;
    }

    private static string NodeKey(IReadOnlyList<string> nodes) => nodes.Count == 0 ? NotRecorded : string.Join(',', nodes);

    private static IReadOnlyList<string> SplitNodes(string? models) =>
        (models ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<string> ModelNames(IEnumerable<BenchResultRow> rows) =>
        rows.Select(row => row.Model)
            .Where(model => model is not (NotRecorded or Mixed))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static ImportEnvelope ParseEnvelope(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<ImportEnvelope>(bytes, JsonOptions)
                ?? throw Refused(StatusCodes.Status400BadRequest, EnvelopeMessage);
        }
        catch (JsonException)
        {
            throw Refused(StatusCodes.Status400BadRequest, EnvelopeMessage);
        }
    }

    private const string EnvelopeMessage = "The import must be a JSON object with resultsJsonl, and optionally run, models and label.";

    private static async Task<byte[]?> ReadBoundedAsync(Stream body, int limit, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        while (true)
        {
            int read = await body.ReadAsync(chunk, cancellationToken);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > limit) return null;
            buffer.Write(chunk, 0, read);
        }
    }

    private List<BenchResultRow> ReadRows()
    {
        if (!File.Exists(_path)) return [];
        return File.ReadLines(_path).Select(TryReadRow).OfType<BenchResultRow>().ToList();
    }

    private static BenchResultRow? TryReadRow(string line)
    {
        try
        {
            BenchResultRow? row = JsonSerializer.Deserialize<BenchResultRow>(line, JsonOptions);
            if (row is null || string.IsNullOrEmpty(row.ImportId) || string.IsNullOrEmpty(row.PlanId) ||
                string.IsNullOrEmpty(row.Task) || string.IsNullOrEmpty(row.Status) || row.ImportedAtUtc == default)
            {
                return null;
            }

            return row with
            {
                Model = row.Model ?? NotRecorded,
                Nodes = row.Nodes ?? [],
                ParkCauses = row.ParkCauses ?? string.Empty,
                FalseAccept = row.Status == Done && !row.HiddenPass,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Append(IReadOnlyList<BenchResultRow> rows)
    {
        string text = string.Concat(rows.Select(row => JsonSerializer.Serialize(row, JsonOptions) + "\n"));
        if (EndsWithoutLineBreak()) text = "\n" + text;
        File.AppendAllText(_path, text, Utf8);
    }

    // After an interrupted write the last line may lack its line break; a new line must not be glued onto it.
    private bool EndsWithoutLineBreak()
    {
        if (!File.Exists(_path)) return false;
        using FileStream stream = File.OpenRead(_path);
        if (stream.Length == 0) return false;
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() != '\n';
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Flag(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static int? Integer(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int number)
            ? number
            : null;

    private static BenchImportRefusedException Refused(int statusCode, string message) => new(statusCode, message);

    private sealed record ImportEnvelope(string? ResultsJsonl, JsonElement Run, Dictionary<string, string?>? Models, string? Label);

    private sealed record RunDetails(DateTimeOffset? StartedAtUtc, IReadOnlyDictionary<string, string> ModelsByNode);
}
