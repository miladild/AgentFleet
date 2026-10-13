namespace AgentFleet;

using System.Text.Json;
using Microsoft.Extensions.Logging;

internal sealed record ModelRegression(
    string? Node,
    string Model,
    int PreviousStepsDoneOnOwn,
    int PreviousStepsWorked,
    int CurrentStepsDoneOnOwn,
    int CurrentStepsWorked);

internal sealed record ModelRegressionNotice(
    string? Node,
    string Model,
    int PreviousStepsDoneOnOwn,
    int PreviousStepsWorked,
    int CurrentStepsDoneOnOwn,
    int CurrentStepsWorked,
    DateTimeOffset WeekStartUtc);

internal interface IModelRegressionNoticeSender
{
    Task SendAsync(ModelRegressionNotice notice);
}

internal static class ModelResults
{
    public static IReadOnlyList<ModelRegression> FindRegressions(ModelScorecardsResult current, ModelScorecardsResult previous)
    {
        var previousByKey = previous.Models.ToDictionary(scorecard => (scorecard.Node, scorecard.Model));
        return current.Models
            .Where(scorecard => scorecard.StepsWorked >= 5 &&
                previousByKey.TryGetValue((scorecard.Node, scorecard.Model), out ModelScorecard? prior) &&
                prior.StepsWorked >= 5 && prior.StepsDoneOnOwn > 0 &&
                scorecard.StepsDoneOnOwn * 2 <= prior.StepsDoneOnOwn)
            .Select(scorecard =>
            {
                ModelScorecard prior = previousByKey[(scorecard.Node, scorecard.Model)];
                return new ModelRegression(scorecard.Node, scorecard.Model, prior.StepsDoneOnOwn,
                    prior.StepsWorked, scorecard.StepsDoneOnOwn, scorecard.StepsWorked);
            })
            .OrderBy(regression => regression.Node, StringComparer.Ordinal)
            .ThenBy(regression => regression.Model, StringComparer.Ordinal)
            .ToArray();
    }

    public static string Describe(ModelRegression regression) =>
        $"{regression.PreviousStepsDoneOnOwn} of {regression.PreviousStepsWorked} last week, " +
        $"{regression.CurrentStepsDoneOnOwn} of {regression.CurrentStepsWorked} this week on {regression.Model}" +
        (regression.Node is null ? "." : $" ({regression.Node}).");

    public static SetupCheck Check(
        IReadOnlyList<ModelRegression> regressions,
        IReadOnlyList<string> staleMeasurements,
        DateTimeOffset nowUtc)
    {
        string status = regressions.Count > 0 ? FleetSetupService.Fail
            : staleMeasurements.Count > 0 ? FleetSetupService.Warn
            : FleetSetupService.Ok;
        var details = regressions.Select(Describe).ToList();
        if (staleMeasurements.Count > 0)
        {
            details.Add("Stale fit measurements: " + string.Join(", ", staleMeasurements) + ".");
        }
        if (details.Count == 0)
        {
            details.Add($"No large drop in own-step results this week (checked {nowUtc:yyyy-MM-dd} UTC).");
        }

        return new SetupCheck("model-results", "machines", status, "Model results", string.Join(" ", details));
    }
}

/// <summary>Persists per-node weekly claims before sending, so repeated setup refreshes do not repeat a regression notice.</summary>
internal sealed class ModelRegressionNotifier
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly IModelRegressionNoticeSender _sender;
    private readonly string _statePath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _sent = new(StringComparer.Ordinal);

    public ModelRegressionNotifier(IModelRegressionNoticeSender sender, string labDirectory, ILogger logger)
    {
        _sender = sender;
        _statePath = Path.Combine(labDirectory, "model-regression-notices.json");
        _logger = logger;
        try
        {
            if (File.Exists(_statePath))
            {
                foreach (string? key in JsonSerializer.Deserialize<string[]>(File.ReadAllText(_statePath)) ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(key)) _sent.Add(key);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(exception, "Could not read the model-results notice history.");
        }
    }

    public async Task NotifyOnceAsync(IReadOnlyList<ModelRegression> regressions, DateTimeOffset nowUtc)
    {
        DateTimeOffset weekStart = WeekStartUtc(nowUtc);
        foreach (ModelRegression regression in regressions)
        {
            string node = regression.Node ?? "not recorded";
            string key = $"{weekStart:yyyy-MM-dd}\n{node}";
            if (!await TryClaimAsync(key)) continue;

            await _sender.SendAsync(new ModelRegressionNotice(regression.Node, regression.Model,
                regression.PreviousStepsDoneOnOwn, regression.PreviousStepsWorked,
                regression.CurrentStepsDoneOnOwn, regression.CurrentStepsWorked, weekStart));
        }
    }

    internal static DateTimeOffset WeekStartUtc(DateTimeOffset nowUtc)
    {
        DateTime utcDay = nowUtc.UtcDateTime.Date;
        int sinceMonday = ((int)utcDay.DayOfWeek + 6) % 7;
        return new DateTimeOffset(utcDay.AddDays(-sinceMonday), TimeSpan.Zero);
    }

    private async Task<bool> TryClaimAsync(string key)
    {
        await _gate.WaitAsync();
        try
        {
            if (_sent.Contains(key)) return false;

            _sent.Add(key);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
                string temporary = _statePath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(_sent.Order(StringComparer.Ordinal), JsonOptions));
                File.Move(temporary, _statePath, overwrite: true);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _sent.Remove(key);
                _logger.LogWarning(exception, "Could not record the weekly model-results notice.");
                return false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
