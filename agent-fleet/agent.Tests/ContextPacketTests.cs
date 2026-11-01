namespace AgentFleet.Tests;

/// <summary>
/// What a repair round is handed: one message with the step, the failing tests whole, the files the step may write, what changed
/// since the step began, what was undone, what was already tried and the failures split into old ones and new ones. Measured: a model
/// given only the output and the line a stack frame names kept bending code to a wrong test it had written itself.
/// </summary>
public sealed class ContextPacketTests
{
    private static ContextPacketInput Input(
        IReadOnlyList<PacketFile>? failing = null, IReadOnlyList<PacketFile>? writable = null, string diff = "", RollbackNote? reverted = null,
        IReadOnlyList<string>? tried = null, string output = "output") =>
        new("Add the alerts store.", ["src/alerts.ts", "test/alerts.test.ts"], failing ?? [], writable ?? [], diff, reverted, tried ?? [], output);

    private static string Lines(int count, string prefix = "line") => string.Join('\n', Enumerable.Range(1, count).Select(number => $"{prefix} {number}"));

    [Fact]
    public void The_sections_come_in_order_from_the_stable_to_the_changing()
    {
        string packet = ContextPacket.Build(Input(
            failing: [new PacketFile("test/alerts.test.ts", "test('adds', () => {\n  assert.equal(1, 2);\n});", [2])],
            writable: [new PacketFile("src/alerts.ts", "export const a = 1;")],
            diff: "src/alerts.ts: +1 -0",
            reverted: new RollbackNote(2, new RoundScore(true, 5), 1, new RoundScore(true, 3), ["x"], ["src/alerts.ts"]),
            tried: ["round 1 changed src/alerts.ts; the check then failed on adds"],
            output: "FAILURES"), 24000);

        int[] at =
        [
            packet.IndexOf("STEP", StringComparison.Ordinal),
            packet.IndexOf("FAILING TESTS", StringComparison.Ordinal),
            packet.IndexOf("FILES YOU MAY WRITE", StringComparison.Ordinal),
            packet.IndexOf("CHANGED SINCE THE STEP BEGAN", StringComparison.Ordinal),
            packet.IndexOf("NOTE:", StringComparison.Ordinal),
            packet.IndexOf("ALREADY TRIED", StringComparison.Ordinal),
            packet.IndexOf("FAILURES", StringComparison.Ordinal)
        ];
        Assert.All(at, position => Assert.True(position >= 0));
        Assert.Equal(at.OrderBy(position => position), at);
    }

    [Fact]
    public void The_step_text_lists_the_files_it_may_write()
    {
        string packet = ContextPacket.Build(Input(), 24000);

        Assert.Contains("Add the alerts store.", packet, StringComparison.Ordinal);
        Assert.Contains("src/alerts.ts", packet, StringComparison.Ordinal);
        Assert.Contains("test/alerts.test.ts", packet, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failing_test_is_shown_whole_with_its_failing_lines_marked()
    {
        string packet = ContextPacket.Build(Input(failing: [new PacketFile("test/a.test.ts", "one\ntwo\nthree", [2])]), 24000);

        Assert.Contains("test/a.test.ts", packet, StringComparison.Ordinal);
        Assert.Contains("  one", packet, StringComparison.Ordinal);
        Assert.Contains(">>> two", packet, StringComparison.Ordinal);
        Assert.Contains("  three", packet, StringComparison.Ordinal);
    }

    [Fact]
    public void A_section_with_nothing_to_say_is_left_out()
    {
        string packet = ContextPacket.Build(Input(), 24000);

        Assert.DoesNotContain("FAILING TESTS", packet, StringComparison.Ordinal);
        Assert.DoesNotContain("CHANGED SINCE THE STEP BEGAN", packet, StringComparison.Ordinal);
        Assert.DoesNotContain("NOTE:", packet, StringComparison.Ordinal);
        Assert.DoesNotContain("ALREADY TRIED", packet, StringComparison.Ordinal);
    }

    [Fact]
    public void An_undone_round_is_said_with_the_files_and_the_failure_counts_and_that_the_files_are_as_shown()
    {
        var reverted = new RollbackNote(3, new RoundScore(true, 7), 2, new RoundScore(true, 4), ["x", "y"], ["src/a.ts", "src/b.ts"]);

        string packet = ContextPacket.Build(Input(reverted: reverted), 24000);

        Assert.Contains("NOTE: your edits to src/a.ts, src/b.ts were reverted because failures increased from 4 failing to 7 failing. " +
                        "The files are back to the content shown above.", packet, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_five_latest_things_tried_are_listed()
    {
        string[] tried = Enumerable.Range(1, 8).Select(number => $"try {number}").ToArray();

        string packet = ContextPacket.Build(Input(tried: tried), 24000);

        Assert.DoesNotContain("try 3", packet, StringComparison.Ordinal);
        Assert.Contains("try 4", packet, StringComparison.Ordinal);
        Assert.Contains("try 8", packet, StringComparison.Ordinal);
    }

    [Fact]
    public void When_the_packet_is_too_big_the_largest_file_that_is_not_failing_is_shortened_first_and_a_failing_test_never()
    {
        string failingTest = Lines(300, "assert");
        string small = Lines(20, "small");
        string large = Lines(2000, "large");
        ContextPacketInput input = Input(
            failing: [new PacketFile("test/a.test.ts", failingTest, [5])],
            writable: [new PacketFile("src/small.ts", small), new PacketFile("src/large.ts", large)]);

        string packet = ContextPacket.Build(input, 3000);

        Assert.Contains("assert 300", packet, StringComparison.Ordinal);
        Assert.Contains("small 20", packet, StringComparison.Ordinal);
        Assert.DoesNotContain("large 2000", packet, StringComparison.Ordinal);
        Assert.Contains("large 1", packet, StringComparison.Ordinal);
        Assert.Contains("lines of src/large.ts are not shown", packet, StringComparison.Ordinal);
        Assert.True(ContextPacket.EstimateTokens(packet) <= 3000 + ContextPacket.EstimateTokens(failingTest));
    }

    [Fact]
    public void A_packet_within_the_limit_shortens_nothing()
    {
        string packet = ContextPacket.Build(Input(writable: [new PacketFile("src/a.ts", Lines(100))]), 24000);

        Assert.Contains("line 100", packet, StringComparison.Ordinal);
        Assert.DoesNotContain("not shown", packet, StringComparison.Ordinal);
    }
}

/// <summary>The failures split into those that existed before the step and those the step brought, with the type errors apart.</summary>
public sealed class FailureReportTests
{
    private const string Output = """
        ✖ adds an alert (3.1ms)
        ✖ old quotes keep their order (1.2ms)
        error TS2345: Argument of type 'string' is not assignable to parameter of type 'number'.
        src/alerts.ts(12,5): error TS2322: Type 'x' is not assignable to type 'y'.
        """;

    private static readonly (string Path, string Content)[] Tests =
    [
        ("test/alerts.test.ts", "test('adds an alert', () => {});"),
        ("test/quotes.test.ts", "it('old quotes keep their order', () => {});")
    ];

    [Fact]
    public void A_failing_test_in_a_file_that_existed_before_the_step_is_a_regression_and_one_in_a_new_file_is_a_feature_failure()
    {
        string report = FailureReport.Build(Output, Tests, new HashSet<string>(["test/quotes.test.ts"], StringComparer.OrdinalIgnoreCase));

        int regression = report.IndexOf("REGRESSION FAILURES", StringComparison.Ordinal);
        int feature = report.IndexOf("FEATURE FAILURES", StringComparison.Ordinal);
        Assert.True(regression >= 0 && feature > regression);
        Assert.InRange(report.IndexOf("old quotes keep their order", StringComparison.Ordinal), regression, feature);
        Assert.True(report.IndexOf("adds an alert", feature, StringComparison.Ordinal) > feature);
        Assert.Contains("(tests that existed and passed before this step)", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Compiler_errors_have_their_own_section()
    {
        string report = FailureReport.Build(Output, Tests, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        int types = report.IndexOf("TYPE ERRORS", StringComparison.Ordinal);
        Assert.True(types >= 0);
        Assert.Contains("TS2345", report[types..], StringComparison.Ordinal);
        Assert.Contains("TS2322", report[types..], StringComparison.Ordinal);
        Assert.DoesNotContain("TS2345", report[..types], StringComparison.Ordinal);
    }

    [Fact]
    public void A_failure_no_test_file_names_is_listed_apart_and_output_with_no_failure_names_is_returned_as_it_is()
    {
        string report = FailureReport.Build("✖ something odd (1ms)", Tests, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.Contains("OTHER FAILURES", report, StringComparison.Ordinal);
        Assert.Contains("something odd", report, StringComparison.Ordinal);

        Assert.Equal("Error: boom", FailureReport.Build("Error: boom", Tests, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
    }
}
