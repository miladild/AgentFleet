using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace AgentFleet.Tests;

public sealed class BenchResultsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fleet-bench-results-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    private BenchResultStore Store() => new(Path.Combine(_directory, "lab"));

    private string StoreFile => Path.Combine(_directory, "lab", "bench-results.jsonl");

    private static string Plan(int number) => number.ToString("x32", CultureInfo.InvariantCulture);

    // One line, as a batch writes it: the same fields, including the times that the import does not keep.
    private static string Line(int number, string task, string status, bool hiddenPass, string models = "example-node-a",
        int parked = 0, bool timedOut = false, string error = "") => JsonSerializer.Serialize(new
    {
        planId = Plan(number),
        task,
        run = 1,
        status,
        hiddenPass,
        falseAccept = status == "done" && !hiddenPass,
        completed = status == "done" && hiddenPass,
        timedOut,
        minutes = 5.5,
        stepsDone = 3,
        stepsParked = parked,
        rounds = 9,
        maxRung = 1.0,
        parkCauses = parked > 0 ? "no-progress" : string.Empty,
        models,
        blockedReason = string.Empty,
        error,
    });

    private static string Lines(params string[] lines) => string.Join("\n", lines);

    // A run record as the batch writes it: the machines and their models, and when the batch started.
    private static string Run(params (string Name, string Model)[] nodes) => RunStartedAt(Now.AddDays(-1), nodes);

    private static string RunStartedAt(DateTimeOffset startedAtUtc, params (string Name, string Model)[] nodes) => JsonSerializer.Serialize(new
    {
        startedAtUtc = startedAtUtc.ToString("o", CultureInfo.InvariantCulture),
        options = new { tier = "standard", repeat = 3, workerOnly = true, review = "auto", autoRetries = (int?)null, timeoutMinutes = 120, tasks = new[] { "slugify" } },
        nodes = nodes.Select(node => new { name = node.Name, model = node.Model }),
    });

    private static MemoryStream Body(string resultsJsonl, string? run, Dictionary<string, string?>? models, string? label)
    {
        var envelope = new Dictionary<string, object?> { ["resultsJsonl"] = resultsJsonl };
        if (run is not null) envelope["run"] = run;
        if (models is not null) envelope["models"] = models;
        if (label is not null) envelope["label"] = label;
        return new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(envelope));
    }

    private static Task<BenchImportResult> Import(BenchResultStore store, string resultsJsonl, string? run = null,
        Dictionary<string, string?>? models = null, string? label = null, DateTimeOffset? nowUtc = null) =>
        store.ImportAsync(Body(resultsJsonl, run, models, label), nowUtc ?? Now, CancellationToken.None);

    private static BenchModelGroup Group(BenchModelsResult result, string node, string model) =>
        result.Groups.Single(group => group.Node == node && group.Model == model);

    [Fact]
    public async Task Lines_that_cannot_be_read_are_skipped_and_counted_and_blank_lines_are_ignored()
    {
        string text = Lines(
            Line(1, "slugify", "done", true),
            "{ not json",
            "[1, 2, 3]",
            "{\"task\":\"slugify\",\"status\":\"done\"}",
            string.Empty,
            Line(2, "slugify", "blocked", false));

        BenchImportResult result = await Import(Store(), text, Run(("example-node-a", "model-alpha")));

        Assert.Equal(2, result.Imported);
        Assert.Equal(0, result.Duplicates);
        Assert.Equal(3, result.Skipped);
        Assert.Equal(["model-alpha"], result.Models);
    }

    [Fact]
    public async Task Unknown_and_old_fields_are_ignored_and_missing_fields_take_safe_defaults()
    {
        // An older line: no hidden-test flag, a field the import never keeps, and one it does not know at all.
        string older = "{\"planId\":\"" + Plan(7) + "\",\"task\":\"slugify\",\"status\":\"done\",\"models\":\"example-node-a\"," +
            "\"minutes\":12.5,\"tokensPerSecond\":30,\"futureField\":{\"x\":1}}";
        BenchResultStore store = Store();

        await Import(store, older, Run(("example-node-a", "model-alpha")));

        BenchModelGroup group = Group(store.Models(null, Now), "example-node-a", "model-alpha");
        Assert.Equal(1, group.Runs);
        Assert.Equal(0, group.DoneAndCorrect);
        Assert.Equal(1, group.FalseAccepts);
        Assert.Equal(0, group.Errors);
    }

    [Fact]
    public async Task Importing_the_same_file_twice_adds_nothing()
    {
        BenchResultStore store = Store();
        string text = Lines(Line(1, "slugify", "done", true), Line(2, "slugify", "blocked", false));
        string run = Run(("example-node-a", "model-alpha"));
        await Import(store, text, run);

        BenchImportResult again = await Import(store, text, run);

        Assert.Equal(0, again.Imported);
        Assert.Equal(2, again.Duplicates);
        Assert.Equal(0, again.Skipped);
        BenchModelsResult result = store.Models(null, Now);
        Assert.Equal(2, Group(result, "example-node-a", "model-alpha").Runs);
        Assert.Single(result.Imports);
    }

    [Fact]
    public async Task A_plan_repeated_inside_one_file_is_counted_once()
    {
        string line = Line(1, "slugify", "done", true);

        BenchImportResult result = await Import(Store(), Lines(line, line), Run(("example-node-a", "model-alpha")));

        Assert.Equal(1, result.Imported);
        Assert.Equal(1, result.Duplicates);
    }

    [Fact]
    public async Task Runs_are_grouped_by_machine_and_model_and_listed_alphabetically()
    {
        BenchResultStore store = Store();
        await Import(store, Lines(
                Line(1, "slugify", "done", true, "example-node-b"),
                Line(2, "slugify", "done", true, "example-node-a"),
                Line(3, "semver-compare", "blocked", false, "example-node-a")),
            Run(("example-node-a", "model-alpha"), ("example-node-b", "model-beta")));

        BenchModelsResult result = store.Models(null, Now);

        Assert.Equal(new[] { "example-node-a", "example-node-b" }, result.Groups.Select(group => group.Node).ToArray());
        BenchModelGroup alpha = Group(result, "example-node-a", "model-alpha");
        Assert.Equal(2, alpha.Runs);
        Assert.Equal(1, alpha.DoneAndCorrect);
        Assert.Equal(new[] { "semver-compare", "slugify" }, alpha.Tasks);
        Assert.Equal(1, Group(result, "example-node-b", "model-beta").Runs);
    }

    [Fact]
    public async Task A_run_on_machines_with_different_models_is_mixed_under_its_own_key()
    {
        BenchResultStore store = Store();

        BenchImportResult result = await Import(store, Line(1, "slugify", "done", true, "example-node-a,example-node-b"),
            Run(("example-node-a", "model-alpha"), ("example-node-b", "model-beta")));

        Assert.Empty(result.Models);
        Assert.Equal(1, Group(store.Models(null, Now), "example-node-a,example-node-b", "mixed").Runs);
    }

    [Fact]
    public async Task A_run_on_several_machines_that_share_one_model_keeps_that_model()
    {
        BenchResultStore store = Store();

        BenchImportResult result = await Import(store, Line(1, "slugify", "done", true, "example-node-a,example-node-b"),
            Run(("example-node-a", "model-alpha"), ("example-node-b", "model-alpha")));

        Assert.Equal(["model-alpha"], result.Models);
        Assert.Equal(1, Group(store.Models(null, Now), "example-node-a,example-node-b", "model-alpha").Runs);
    }

    [Fact]
    public async Task A_machine_without_a_recorded_model_and_a_run_with_no_machine_are_both_not_recorded()
    {
        BenchResultStore store = Store();
        await Import(store, Lines(
                Line(1, "slugify", "done", true, "example-node-z"),
                Line(2, "slugify", "blocked", false, string.Empty)),
            Run(("example-node-a", "model-alpha")));

        BenchModelsResult result = store.Models(null, Now);

        Assert.Equal(1, Group(result, "example-node-z", "not recorded").Runs);
        Assert.Equal(1, Group(result, "not recorded", "not recorded").Runs);
    }

    [Fact]
    public async Task A_run_on_several_machines_where_only_some_have_a_known_model_is_not_recorded_and_never_guessed()
    {
        BenchResultStore store = Store();
        await Import(store, Line(1, "slugify", "done", true, "example-node-a,example-node-b"), Run(("example-node-a", "model-alpha")));

        BenchModelsResult result = store.Models(null, Now);

        Assert.Equal("not recorded", Assert.Single(result.Groups).Model);
    }

    [Fact]
    public async Task Groups_are_listed_alphabetically_whatever_their_counts_are()
    {
        BenchResultStore store = Store();
        // The machine that sorts first did the fewest runs, so a list ordered by count would put it last.
        await Import(store, Lines(
                Line(1, "slugify", "done", true, "example-node-a"),
                Line(2, "slugify", "done", true, "example-node-b"),
                Line(3, "slugify", "done", true, "example-node-b"),
                Line(4, "slugify", "done", true, "example-node-b")),
            Run(("example-node-a", "model-zulu"), ("example-node-b", "model-alpha")));

        BenchModelsResult result = store.Models(null, Now);

        Assert.Equal(["example-node-a", "example-node-b"], result.Groups.Select(group => group.Node).ToArray());
        Assert.Equal(1, result.Groups[0].Runs);
        Assert.Equal(3, result.Groups[1].Runs);
    }

    [Fact]
    public async Task The_models_map_names_the_models_when_the_run_record_has_no_machines_and_the_run_record_wins_otherwise()
    {
        BenchResultStore store = Store();
        var map = new Dictionary<string, string?> { ["example-node-a"] = "model-from-map" };
        // A batch whose backend could not be asked writes a run record with no machines.
        await Import(store, Line(1, "slugify", "done", true), RunStartedAt(Now.AddDays(-1)), map);
        await Import(store, Line(2, "slugify", "done", true), Run(("example-node-a", "model-from-run")), map);

        BenchModelsResult result = store.Models(null, Now);

        Assert.Equal(1, Group(result, "example-node-a", "model-from-map").Runs);
        Assert.Equal(1, Group(result, "example-node-a", "model-from-run").Runs);
    }

    [Fact]
    public async Task A_done_plan_whose_hidden_tests_failed_is_a_false_accept_whatever_the_line_says()
    {
        BenchResultStore store = Store();
        // This line claims no false accept. The import works the flag out from the status and the hidden tests instead.
        string claimsNoFalseAccept = "{\"planId\":\"" + Plan(2) + "\",\"task\":\"slugify\",\"status\":\"done\",\"hiddenPass\":false," +
            "\"falseAccept\":false,\"models\":\"example-node-a\"}";
        await Import(store, Lines(Line(1, "slugify", "done", true), claimsNoFalseAccept), Run(("example-node-a", "model-alpha")));

        BenchModelGroup group = Group(store.Models(null, Now), "example-node-a", "model-alpha");
        Assert.Equal(2, group.Runs);
        Assert.Equal(1, group.FalseAccepts);
        Assert.Equal(1, group.DoneAndCorrect);
    }

    [Fact]
    public async Task Four_runs_are_too_few_to_judge_and_five_are_not()
    {
        BenchResultStore store = Store();
        string run = Run(("example-node-a", "model-alpha"));
        await Import(store, Lines(Enumerable.Range(1, 4).Select(number => Line(number, "slugify", "done", true)).ToArray()), run);

        BenchModelGroup four = Group(store.Models(null, Now), "example-node-a", "model-alpha");
        Assert.Equal(4, four.Runs);
        Assert.True(four.TooFew);

        await Import(store, Line(5, "slugify", "done", true), run);

        BenchModelGroup five = Group(store.Models(null, Now), "example-node-a", "model-alpha");
        Assert.Equal(5, five.Runs);
        Assert.False(five.TooFew);
    }

    [Fact]
    public async Task Parked_timed_out_and_errored_runs_are_counted_on_their_own()
    {
        BenchResultStore store = Store();

        await Import(store, Lines(
                Line(1, "slugify", "blocked", false, parked: 2),
                Line(2, "slugify", "not-started", false, timedOut: true, error: "The backend did not answer."),
                Line(3, "slugify", "done", true)),
            Run(("example-node-a", "model-alpha")));

        BenchModelGroup group = Group(store.Models(null, Now), "example-node-a", "model-alpha");
        Assert.Equal(3, group.Runs);
        Assert.Equal(1, group.Parked);
        Assert.Equal(1, group.TimedOut);
        Assert.Equal(1, group.Errors);
        Assert.Equal(1, group.DoneAndCorrect);
        Assert.Equal(0, group.FalseAccepts);
    }

    [Fact]
    public async Task The_days_filter_counts_only_runs_that_started_inside_the_window()
    {
        BenchResultStore store = Store();
        await Import(store, Line(1, "slugify", "done", true), RunStartedAt(Now.AddDays(-10), ("example-node-a", "model-alpha")));
        await Import(store, Line(2, "slugify", "done", true), RunStartedAt(Now.AddDays(-2), ("example-node-a", "model-alpha")));
        // No run record: the import time is the only time there is.
        await Import(store, Line(3, "slugify", "done", true), models: new Dictionary<string, string?> { ["example-node-a"] = "model-alpha" });

        Assert.Equal(3, Group(store.Models(null, Now), "example-node-a", "model-alpha").Runs);
        Assert.Equal(2, Group(store.Models(7, Now), "example-node-a", "model-alpha").Runs);
        Assert.Equal(1, Group(store.Models(1, Now), "example-node-a", "model-alpha").Runs);
        Assert.Equal(3, store.Models(1, Now).Imports.Count);
    }

    [Fact]
    public async Task A_body_over_the_size_limit_is_refused_with_413_and_a_reason()
    {
        using var body = new MemoryStream(new byte[BenchResultStore.MaximumBodyBytes + 1]);

        BenchImportRefusedException refused = await Assert.ThrowsAsync<BenchImportRefusedException>(
            () => Store().ImportAsync(body, Now, CancellationToken.None));

        Assert.Equal(413, refused.StatusCode);
        Assert.Contains("8 MB", refused.Message);
    }

    [Fact]
    public async Task A_results_file_over_2_MB_is_refused_with_413_and_a_reason()
    {
        string tooLarge = new('x', BenchResultStore.MaximumResultsBytes + 1);

        BenchImportRefusedException refused = await Assert.ThrowsAsync<BenchImportRefusedException>(() => Import(Store(), tooLarge));

        Assert.Equal(413, refused.StatusCode);
        Assert.Contains("2 MB", refused.Message);
    }

    [Fact]
    public async Task A_results_file_of_exactly_2_MB_is_accepted()
    {
        string row = Line(1, "slugify", "done", true);
        string padded = row + "\n" + new string(' ', BenchResultStore.MaximumResultsBytes - row.Length - 1);

        BenchImportResult result = await Import(Store(), padded, Run(("example-node-a", "model-alpha")));

        Assert.Equal(1, result.Imported);
    }

    [Fact]
    public async Task An_empty_results_file_is_refused_with_400()
    {
        BenchImportRefusedException refused = await Assert.ThrowsAsync<BenchImportRefusedException>(() => Import(Store(), " \n\t "));

        Assert.Equal(400, refused.StatusCode);
    }

    [Fact]
    public async Task A_body_that_is_not_an_import_or_a_run_record_is_refused_with_400()
    {
        BenchResultStore store = Store();

        BenchImportRefusedException notJson = await Assert.ThrowsAsync<BenchImportRefusedException>(
            () => store.ImportAsync(new MemoryStream(Encoding.UTF8.GetBytes("not json")), Now, CancellationToken.None));
        BenchImportRefusedException notObject = await Assert.ThrowsAsync<BenchImportRefusedException>(
            () => Import(store, Line(1, "slugify", "done", true), "[1, 2]"));

        Assert.Equal(400, notJson.StatusCode);
        Assert.Equal(400, notObject.StatusCode);
    }

    [Fact]
    public async Task A_label_of_more_than_80_characters_is_refused_and_80_is_accepted()
    {
        string run = Run(("example-node-a", "model-alpha"));

        BenchImportRefusedException refused = await Assert.ThrowsAsync<BenchImportRefusedException>(
            () => Import(Store(), Line(1, "slugify", "done", true), run, label: new string('l', 81)));
        BenchImportResult accepted = await Import(Store(), Line(2, "slugify", "done", true), run, label: new string('l', 80));

        Assert.Equal(400, refused.StatusCode);
        Assert.Equal(1, accepted.Imported);
    }

    [Fact]
    public async Task Delete_removes_one_import_and_leaves_the_others_and_unreadable_lines_in_place()
    {
        BenchResultStore store = Store();
        string run = Run(("example-node-a", "model-alpha"));
        await Import(store, Line(1, "slugify", "done", true), run, label: "first");
        await Import(store, Line(2, "slugify", "done", true), run, label: "second");
        File.AppendAllText(StoreFile, "{ a line this version cannot read\n");
        string firstId = store.Models(null, Now).Imports.Single(summary => summary.Label == "first").ImportId;

        Assert.Equal(1, store.Delete(firstId));

        BenchModelsResult result = store.Models(null, Now);
        Assert.Equal("second", Assert.Single(result.Imports).Label);
        Assert.Equal(1, Group(result, "example-node-a", "model-alpha").Runs);
        Assert.Contains("a line this version cannot read", File.ReadAllText(StoreFile));
        Assert.Null(store.Delete(firstId));
        Assert.Null(store.Delete("no-such-import"));
    }

    [Fact]
    public void With_no_imports_the_answer_is_empty_lists()
    {
        BenchModelsResult result = Store().Models(null, Now);

        Assert.Empty(result.Groups);
        Assert.Empty(result.Imports);
        string json = JsonSerializer.Serialize(result, Web);
        Assert.Contains("\"groups\":[]", json);
        Assert.Contains("\"imports\":[]", json);
    }

    [Fact]
    public async Task The_answer_has_no_speed_fraction_or_ranking_field()
    {
        BenchResultStore store = Store();
        await Import(store, Line(1, "slugify", "done", true), Run(("example-node-a", "model-alpha")));
        string[] forbidden = ["minute", "speed", "token", "rank", "fraction", "percent", "ratio", "score", "second"];

        string json = JsonSerializer.Serialize(store.Models(null, Now), Web);

        foreach (string word in forbidden)
        {
            Assert.DoesNotContain(word, json, StringComparison.OrdinalIgnoreCase);
        }

        foreach (Type type in new[] { typeof(BenchModelsResult), typeof(BenchModelGroup), typeof(BenchImportSummary) })
        {
            foreach (PropertyInfo property in type.GetProperties())
            {
                foreach (string word in forbidden)
                {
                    Assert.DoesNotContain(word, property.Name, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    [Fact]
    public async Task The_store_only_appends_and_a_new_instance_reads_what_an_earlier_one_wrote()
    {
        string run = Run(("example-node-a", "model-alpha"));
        await Import(Store(), Line(1, "slugify", "done", true), run);
        string before = File.ReadAllText(StoreFile);

        await Import(Store(), Line(2, "slugify", "done", true), run);

        Assert.StartsWith(before, File.ReadAllText(StoreFile));
        Assert.Equal(2, Group(Store().Models(null, Now), "example-node-a", "model-alpha").Runs);
    }

    [Fact]
    public async Task A_line_cut_short_by_an_interrupted_write_does_not_swallow_the_next_run()
    {
        string run = Run(("example-node-a", "model-alpha"));
        await Import(Store(), Line(1, "slugify", "done", true), run);
        File.WriteAllText(StoreFile, File.ReadAllText(StoreFile).TrimEnd('\n'));

        await Import(Store(), Line(2, "slugify", "done", true), run);

        Assert.Equal(2, Group(Store().Models(null, Now), "example-node-a", "model-alpha").Runs);
    }
}
