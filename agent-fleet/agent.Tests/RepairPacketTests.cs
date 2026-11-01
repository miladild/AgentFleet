namespace AgentFleet.Tests;

/// <summary>The repair packet read from a real project folder: the step's files, the failing tests whole, the change since the step began.</summary>
public sealed class RepairPacketTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fleet-packet-" + Guid.NewGuid().ToString("N"));

    public RepairPacketTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "test"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "dep"));
        File.WriteAllText(Path.Combine(_root, "src", "alerts.ts"), "export const limit = 5;\nexport function add() {}\n");
        File.WriteAllText(Path.Combine(_root, "test", "alerts.test.ts"), "test('adds an alert', () => {\n  assert.equal(add(), 1);\n});\n");
        File.WriteAllText(Path.Combine(_root, "test", "quotes.test.ts"), "test('old quotes keep their order', () => {\n  assert.equal(1, 2);\n});\n");
        File.WriteAllText(Path.Combine(_root, "node_modules", "dep", "index.js"), "module.exports = 1;");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private (PlanRecord Plan, PlanStep Step) Plan()
    {
        var step = new PlanStep(1, "Add alerts", "Store price alerts.", ["src/alerts.ts", "test/alerts.test.ts"], "npm test", "standard", StepStatus.Running, null, 1, null, null);
        var plan = new PlanRecord("0123456789abcdef0123456789abcdef", "t", "Price alerts", PlanStatus.Running, null, [], [], [], null, null,
            [step], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null) with { WorkingDirectory = _root };
        return (plan, step);
    }

    private string Full(string relative) => Path.GetFullPath(Path.Combine(_root, relative));

    private const string Failure =
        "✖ adds an alert (2ms)\n  AssertionError: 0 !== 1\n      at Context.<anonymous> (project/test/alerts.test.ts:2:10)\n" +
        "✖ old quotes keep their order (1ms)\n  AssertionError: 1 !== 2\n      at Context.<anonymous> (project/test/quotes.test.ts:2:10)";

    [Fact]
    public void The_packet_has_the_step_the_failing_tests_whole_with_the_failing_line_marked_the_files_and_the_split_failures()
    {
        (PlanRecord plan, PlanStep step) = Plan();
        var atStart = new HashSet<string>([Full("src/alerts.ts"), Full("test/quotes.test.ts")], StringComparer.OrdinalIgnoreCase);
        var content = new Dictionary<string, string> { [Full("src/alerts.ts")] = "export const limit = 5;\n" };

        string packet = RepairPacket.Build(plan, step, Failure, atStart, content, null, [], 24000);

        Assert.Contains("Add alerts", packet, StringComparison.Ordinal);
        Assert.Contains(">>>   assert.equal(add(), 1);", packet, StringComparison.Ordinal);
        Assert.Contains("--- test/alerts.test.ts", packet, StringComparison.Ordinal);
        Assert.Contains("--- src/alerts.ts", packet, StringComparison.Ordinal);
        Assert.DoesNotContain("module.exports", packet, StringComparison.Ordinal);
        int regression = packet.IndexOf("REGRESSION FAILURES", StringComparison.Ordinal);
        int feature = packet.IndexOf("FEATURE FAILURES", StringComparison.Ordinal);
        Assert.InRange(packet.IndexOf("old quotes keep their order (test/quotes.test.ts)", StringComparison.Ordinal), regression, feature);
        Assert.True(packet.IndexOf("adds an alert (test/alerts.test.ts)", feature, StringComparison.Ordinal) > feature);
    }

    [Fact]
    public void What_changed_since_the_step_began_is_shown_as_removed_and_added_lines_and_a_new_file_is_said_to_be_new()
    {
        (PlanRecord plan, PlanStep step) = Plan();
        var atStart = new HashSet<string>([Full("src/alerts.ts")], StringComparer.OrdinalIgnoreCase);
        var content = new Dictionary<string, string> { [Full("src/alerts.ts")] = "export const limit = 3;\nexport function add() {}\n" };

        string packet = RepairPacket.Build(plan, step, Failure, atStart, content, null, [], 24000);

        int diff = packet.IndexOf("CHANGED SINCE THE STEP BEGAN", StringComparison.Ordinal);
        Assert.True(diff >= 0);
        string section = packet[diff..];
        Assert.Contains("src/alerts.ts: +1 -1", section, StringComparison.Ordinal);
        Assert.Contains("- export const limit = 3;", section, StringComparison.Ordinal);
        Assert.Contains("+ export const limit = 5;", section, StringComparison.Ordinal);
        Assert.Contains("test/alerts.test.ts (new file, 3 lines)", section, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_did_not_change_is_not_in_the_diff_and_a_file_the_step_does_not_list_is_not_shown()
    {
        (PlanRecord plan, PlanStep step) = Plan();
        var atStart = new HashSet<string>([Full("src/alerts.ts"), Full("test/alerts.test.ts")], StringComparer.OrdinalIgnoreCase);
        var content = new Dictionary<string, string>
        {
            [Full("src/alerts.ts")] = File.ReadAllText(Full("src/alerts.ts")),
            [Full("test/alerts.test.ts")] = File.ReadAllText(Full("test/alerts.test.ts"))
        };

        string packet = RepairPacket.Build(plan, step, Failure[..Failure.IndexOf("✖ old", StringComparison.Ordinal)], atStart, content, null, [], 24000);

        Assert.DoesNotContain("CHANGED SINCE THE STEP BEGAN", packet, StringComparison.Ordinal);
        Assert.DoesNotContain("--- test/quotes.test.ts", packet, StringComparison.Ordinal);
    }
}
