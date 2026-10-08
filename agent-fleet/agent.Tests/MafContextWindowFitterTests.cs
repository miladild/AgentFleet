#pragma warning disable MAAI001

using Microsoft.Extensions.AI;
using Xunit.Abstractions;

namespace AgentFleet.Tests;

public sealed class MafContextWindowFitterTests
{
    private readonly ITestOutputHelper _output;

    public MafContextWindowFitterTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Fitter_name_is_correct(bool collapse)
    {
        var fitter = new MafContextWindowFitter(collapse);
        Assert.Equal(collapse ? "maf-collapse" : "maf-truncate", fitter.Name);
    }

    // I1: Same instance when conversation fits.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task I1_same_instance_when_fits(bool collapse)
    {
        var fitter = new MafContextWindowFitter(collapse);
        IReadOnlyList<ChatMessage> input = [new ChatMessage(ChatRole.User, new string('x', 1_000))];

        IReadOnlyList<ChatMessage> fitted = await fitter.FitAsync(input, null, 10_000, 1.0, CancellationToken.None);

        Assert.Same(fitted, input);
    }

    // I2: System and task messages are always present and unchanged.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task I2_system_and_task_present_and_unchanged(bool collapse)
    {
        var fitter = new MafContextWindowFitter(collapse);
        List<ChatMessage> input = ContextWindowTestHelper.BuildConversation(35);

        IReadOnlyList<ChatMessage> fitted = await fitter.FitAsync(input, null, 20_000, 1.0, CancellationToken.None);

        // Must compact with this budget
        Assert.NotSame(fitted, input);

        // System and task are present
        Assert.True(fitted.Count >= 2);
        Assert.Equal(ChatRole.System, fitted[0].Role);
        Assert.Equal(ChatRole.User, fitted[1].Role);

        // Unchanged by signature
        Assert.Equal(ContextWindowTestHelper.Signature(fitted[0]), ContextWindowTestHelper.Signature(input[0]));
        Assert.Equal(ContextWindowTestHelper.Signature(fitted[1]), ContextWindowTestHelper.Signature(input[1]));
    }

    // I3: Original list not mutated.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task I3_original_not_mutated(bool collapse)
    {
        var fitter = new MafContextWindowFitter(collapse);
        List<ChatMessage> input = ContextWindowTestHelper.BuildConversation(20);

        var sigs = input.Select(ContextWindowTestHelper.Signature).ToList();
        IReadOnlyList<ChatMessage> fitted = await fitter.FitAsync(input, null, 8_000, 1.0, CancellationToken.None);

        Assert.Equal(sigs.Count, input.Count);
        for (int i = 0; i < input.Count; i++)
        {
            Assert.Equal(sigs[i], ContextWindowTestHelper.Signature(input[i]));
        }
    }

    // I4: No orphans.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task I4_no_orphans(bool collapse)
    {
        var fitter = new MafContextWindowFitter(collapse);
        List<ChatMessage> input = ContextWindowTestHelper.BuildConversation(30);

        IReadOnlyList<ChatMessage> fitted = await fitter.FitAsync(input, null, 20_000, 1.0, CancellationToken.None);

        Assert.False(ContextWindowTestHelper.HasOrphans(fitted));
    }

    // I5: Last call and result intact.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task I5_last_call_and_result_intact(bool collapse)
    {
        var fitter = new MafContextWindowFitter(collapse);
        List<ChatMessage> input = ContextWindowTestHelper.BuildConversation(30);

        IReadOnlyList<ChatMessage> fitted = await fitter.FitAsync(input, null, 20_000, 1.0, CancellationToken.None);

        // Last call and result present by signature
        Assert.Equal(ContextWindowTestHelper.Signature(fitted[^2]), ContextWindowTestHelper.Signature(input[^2]));
        Assert.Equal(ContextWindowTestHelper.Signature(fitted[^1]), ContextWindowTestHelper.Signature(input[^1]));
    }

    // I6: Extreme budget still returns without throwing.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task I6_extreme_budget_no_throw(bool collapse)
    {
        var fitter = new MafContextWindowFitter(collapse);
        var input = new List<ChatMessage>
        {
            new(ChatRole.System, new string('s', 5_000)),
            new(ChatRole.User, new string('t', 5_000)),
        };

        IReadOnlyList<ChatMessage> fitted = await fitter.FitAsync(input, null, 1_000, 1.0, CancellationToken.None);

        Assert.NotNull(fitted);
        Assert.True(fitted.Count > 0);
    }

    // I7: Prefix stability - changing gradually over growing conversations.
    [Fact]
    public async Task I7_prefix_stability_growing()
    {
        int classicChanges = 0, truncateChanges = 0, collapseChanges = 0;
        int classicCompacted = 0, truncateCompacted = 0, collapseCompacted = 0;

        string? classicPrefix = null, truncatePrefix = null, collapsePrefix = null;

        for (int rounds = 8; rounds <= 35; rounds++)
        {
            List<ChatMessage> input = ContextWindowTestHelper.BuildConversation(rounds);

            var classic = ClassicContextWindowFitter.Instance;
            IReadOnlyList<ChatMessage> cf = await classic.FitAsync(input, null, 20_000, 1.0, CancellationToken.None);
            string cp = string.Join("\n", cf.Skip(2).Take(5).Select(ContextWindowTestHelper.Signature));
            if (!ReferenceEquals(cf, input))
            {
                classicCompacted++;
                if (classicPrefix != null && classicPrefix != cp) classicChanges++;
                classicPrefix = cp;
            }

            var truncate = new MafContextWindowFitter(collapseToolResults: false);
            IReadOnlyList<ChatMessage> tf = await truncate.FitAsync(input, null, 20_000, 1.0, CancellationToken.None);
            string tp = string.Join("\n", tf.Skip(2).Take(5).Select(ContextWindowTestHelper.Signature));
            if (!ReferenceEquals(tf, input))
            {
                truncateCompacted++;
                if (truncatePrefix != null && truncatePrefix != tp) truncateChanges++;
                truncatePrefix = tp;
            }

            var collapse = new MafContextWindowFitter(collapseToolResults: true);
            IReadOnlyList<ChatMessage> cof = await collapse.FitAsync(input, null, 20_000, 1.0, CancellationToken.None);
            string cop = string.Join("\n", cof.Skip(2).Take(5).Select(ContextWindowTestHelper.Signature));
            if (!ReferenceEquals(cof, input))
            {
                collapseCompacted++;
                if (collapsePrefix != null && collapsePrefix != cop) collapseChanges++;
                collapsePrefix = cop;
            }
        }

        _output.WriteLine($"classic:      {classicCompacted} compacted, {classicChanges} prefix changes");
        _output.WriteLine($"maf-truncate: {truncateCompacted} compacted, {truncateChanges} prefix changes");
        _output.WriteLine($"maf-collapse: {collapseCompacted} compacted, {collapseChanges} prefix changes");

        if (truncateCompacted > 0)
            Assert.True(truncateChanges <= truncateCompacted / 2.0);
        if (collapseCompacted > 0)
            Assert.True(collapseChanges <= collapseCompacted / 2.0);
    }

    // I8: Collapse - collapsed lines are valid, deterministic, with merged message.
    [Fact]
    public async Task I8_collapse_lines_valid()
    {
        var fitter = new MafContextWindowFitter(collapseToolResults: true);
        List<ChatMessage> input = ContextWindowTestHelper.BuildConversation(35);

        // INSERT large write_file at index 2 (right after task) so it falls in collapsed region
        input.Insert(2, new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("large_write", "write_file",
                new Dictionary<string, object?> { ["path"] = "file.txt", ["content"] = new string('x', 5_000) })]));
        input.Insert(3, new ChatMessage(ChatRole.Tool,
            [new FunctionResultContent("large_write", "Wrote file")]));

        IReadOnlyList<ChatMessage> fitted = await fitter.FitAsync(input, null, 20_000, 1.0, CancellationToken.None);

        // Must compact
        Assert.NotSame(fitted, input);
        Assert.Equal(0, fitter.FallbackCount);

        // Exactly ONE merged message
        var mergedMessages = fitted
            .Where(m => m.Role == ChatRole.Assistant &&
                        m.Contents.Count == 1 &&
                        m.Contents[0] is TextContent t &&
                        t.Text.StartsWith("[earlier tool calls, shortened]"))
            .ToList();

        Assert.Single(mergedMessages);
        var mergedMessage = mergedMessages[0];

        // Message must contain only TextContent
        Assert.All(mergedMessage.Contents, content => Assert.IsType<TextContent>(content));

        // Check structure
        var mergedText = ((TextContent)mergedMessage.Contents[0]).Text;
        Assert.True(mergedText.StartsWith("[earlier tool calls, shortened]\n"));

        // Check numbered lines exist (at least 10)
        var lines = mergedText.Split('\n');
        var numberedLines = lines.Where(l => char.IsDigit(l.FirstOrDefault()) && l.Contains(". ")).ToList();
        Assert.True(numberedLines.Count >= 10);

        // Check line that mentions file.txt
        var fileLineLine = numberedLines.FirstOrDefault(l => l.Contains("file.txt"));
        Assert.NotNull(fileLineLine);
        Assert.Contains("...", fileLineLine);
        Assert.True(fileLineLine.Length < 700);
        Assert.DoesNotContain(new string('x', 200), fileLineLine);

        // Check RemovedMarker appears at most once per line
        foreach (var line in numberedLines)
        {
            int markerCount = line.Split(ContextSizeChatClient.RemovedMarker, StringSplitOptions.None).Length - 1;
            Assert.True(markerCount <= 1);
        }

        // Last six messages of output equal input's last six by Signature
        Assert.True(fitted.Count >= 6);
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(
                ContextWindowTestHelper.Signature(fitted[fitted.Count - 6 + i]),
                ContextWindowTestHelper.Signature(input[input.Count - 6 + i]));
        }
    }

    // I9: FallbackCount zero on normal operation.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task I9_fallback_count_zero_normal(bool collapse)
    {
        var fitter = new MafContextWindowFitter(collapse);
        List<ChatMessage> input = ContextWindowTestHelper.BuildConversation(30);

        IReadOnlyList<ChatMessage> fitted = await fitter.FitAsync(input, null, 20_000, 1.0, CancellationToken.None);

        Assert.NotSame(fitted, input);
        Assert.Equal(0, fitter.FallbackCount);
    }

    // Edge case: no user message.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task No_user_message_head_system_only(bool collapse)
    {
        var fitter = new MafContextWindowFitter(collapse);
        List<ChatMessage> input = ContextWindowTestHelper.BuildConversation(30, withUser: false);

        IReadOnlyList<ChatMessage> fitted = await fitter.FitAsync(input, null, 20_000, 1.0, CancellationToken.None);

        Assert.NotSame(fitted, input);
        Assert.True(ContextSizeChatClient.EstimateTokens(fitted, null) <= 20_000);
        Assert.Equal(ChatRole.System, fitted[0].Role);
        Assert.Equal(ContextWindowTestHelper.Signature(fitted[0]), ContextWindowTestHelper.Signature(input[0]));
        Assert.Equal(ContextWindowTestHelper.Signature(fitted[^2]), ContextWindowTestHelper.Signature(input[^2]));
        Assert.Equal(ContextWindowTestHelper.Signature(fitted[^1]), ContextWindowTestHelper.Signature(input[^1]));
    }

    // Fallback seam tests using CompactAsyncOverride.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fallback_seam_injected_exception_triggers_fallback(bool collapse)
    {
        var warnings = new ContextWindowTestHelper.CollectingLogger();
        var fitter = new MafContextWindowFitter(collapse, warnings)
        {
            CompactAsyncOverride = (_, _, _, _) => throw new InvalidOperationException("boom")
        };
        List<ChatMessage> input = ContextWindowTestHelper.BuildConversation(35);
        var classic = ClassicContextWindowFitter.Instance;

        // Call FitAsync twice
        IReadOnlyList<ChatMessage> result1 = await fitter.FitAsync(input, null, 20_000, 1.0, CancellationToken.None);
        IReadOnlyList<ChatMessage> result2 = await fitter.FitAsync(input, null, 20_000, 1.0, CancellationToken.None);

        // Check fallback count
        Assert.Equal(2, fitter.FallbackCount);

        // Check exactly one warning
        Assert.Single(warnings.Warnings);

        // Check results match ClassicContextWindowFitter
        IReadOnlyList<ChatMessage> classicResult = await classic.FitAsync(input, null, 20_000, 1.0, CancellationToken.None);
        var result1Sigs = result1.Select(ContextWindowTestHelper.Signature).ToList();
        var result2Sigs = result2.Select(ContextWindowTestHelper.Signature).ToList();
        var classicSigs = classicResult.Select(ContextWindowTestHelper.Signature).ToList();

        Assert.Equal(classicSigs, result1Sigs);
        Assert.Equal(classicSigs, result2Sigs);
        Assert.NotSame(result1, input);
        Assert.NotSame(result2, input);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fallback_seam_cancellation_not_caught(bool collapse)
    {
        var warnings = new ContextWindowTestHelper.CollectingLogger();
        var fitter = new MafContextWindowFitter(collapse, warnings)
        {
            CompactAsyncOverride = (_, _, _, _) => throw new OperationCanceledException()
        };
        List<ChatMessage> input = ContextWindowTestHelper.BuildConversation(35);

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await fitter.FitAsync(input, null, 20_000, 1.0, CancellationToken.None));

        Assert.Equal(0, fitter.FallbackCount);
    }

    // I6b: Floor test - tight budget leaves only preserved parts.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task I6b_only_preserved_parts_remain_under_tight_budget(bool collapse)
    {
        var fitter = new MafContextWindowFitter(collapse);
        List<ChatMessage> input = ContextWindowTestHelper.BuildConversation(30);
        var head = input.Take(2).ToList();
        int budget = ContextSizeChatClient.EstimateTokens(head, null) + 1_000;

        IReadOnlyList<ChatMessage> fitted = await fitter.FitAsync(input, null, budget, 1.0, CancellationToken.None);

        Assert.Equal(0, fitter.FallbackCount);
        Assert.NotSame(fitted, input);

        // First two messages match
        Assert.Equal(ContextWindowTestHelper.Signature(fitted[0]), ContextWindowTestHelper.Signature(input[0]));
        Assert.Equal(ContextWindowTestHelper.Signature(fitted[1]), ContextWindowTestHelper.Signature(input[1]));

        // Last six messages match (last three groups)
        Assert.True(fitted.Count >= 8);
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(
                ContextWindowTestHelper.Signature(fitted[fitted.Count - 6 + i]),
                ContextWindowTestHelper.Signature(input[input.Count - 6 + i]));
        }

        // For maf-truncate, should be 2 + 6
        if (!collapse)
        {
            Assert.Equal(8, fitted.Count);
        }
    }

    // Head pin edge test: system + assistant text + user task.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Head_pin_system_assistant_user(bool collapse)
    {
        var fitter = new MafContextWindowFitter(collapse);

        // Build conversation: System + Assistant text + User task + 35 rounds
        var base35 = ContextWindowTestHelper.BuildConversation(35, withSystem: false, withUser: false);
        var input = new List<ChatMessage>
        {
            new(ChatRole.System, "You are helpful."),
            new(ChatRole.Assistant, "Hello, how can I help?"),
            new(ChatRole.User, "TASK: implement compare and sort. " + new string('t', 1500))
        };
        input.AddRange(base35);

        IReadOnlyList<ChatMessage> fitted = await fitter.FitAsync(input, null, 20_000, 1.0, CancellationToken.None);

        Assert.NotSame(fitted, input);

        // Task message at index 2, present and unchanged
        Assert.True(fitted.Count > 2);
        Assert.Equal(ChatRole.User, fitted[2].Role);
        Assert.Equal(
            ContextWindowTestHelper.Signature(fitted[2]),
            ContextWindowTestHelper.Signature(input[2]));
    }

    // B3: Merge is deterministic.
    [Fact]
    public async Task Collapse_merge_is_deterministic()
    {
        List<ChatMessage> input = ContextWindowTestHelper.BuildConversation(35);

        var fitter1 = new MafContextWindowFitter(collapseToolResults: true);
        IReadOnlyList<ChatMessage> fitted1 = await fitter1.FitAsync(input, null, 20_000, 1.0, CancellationToken.None);
        var sigs1 = fitted1.Select(ContextWindowTestHelper.Signature).ToList();

        var fitter2 = new MafContextWindowFitter(collapseToolResults: true);
        IReadOnlyList<ChatMessage> fitted2 = await fitter2.FitAsync(input, null, 20_000, 1.0, CancellationToken.None);
        var sigs2 = fitted2.Select(ContextWindowTestHelper.Signature).ToList();

        Assert.Equal(sigs1, sigs2);
    }

    // B4: Truncate has no merged messages.
    [Fact]
    public async Task Truncate_policy_has_no_merged_message()
    {
        var fitter = new MafContextWindowFitter(collapseToolResults: false);
        List<ChatMessage> input = ContextWindowTestHelper.BuildConversation(35);

        IReadOnlyList<ChatMessage> fitted = await fitter.FitAsync(input, null, 20_000, 1.0, CancellationToken.None);

        var mergedCount = fitted
            .Count(m => m.Role == ChatRole.Assistant &&
                        m.Contents.Count == 1 &&
                        m.Contents[0] is TextContent t &&
                        t.Text.StartsWith("[earlier tool calls"));

        Assert.Equal(0, mergedCount);
    }

    // H1: Whitespace flattening - collapsed lines contain newlines and tabs which must be flattened to spaces.
    [Fact]
    public async Task H1_whitespace_flattening_collapsed_lines()
    {
        var fitter = new MafContextWindowFitter(collapseToolResults: true);

        // Build a conversation with a large number of rounds to trigger compaction
        List<ChatMessage> input = ContextWindowTestHelper.BuildConversation(35);

        // Insert tool calls with results containing newlines, carriage returns, and tabs
        // Insert at index 2, right after the system and task messages
        input.Insert(2, new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call1", "write_file",
                new Dictionary<string, object?> { ["path"] = "file.txt", ["content"] = "hello world" })]));
        input.Insert(3, new ChatMessage(ChatRole.Tool,
            [new FunctionResultContent("call1", "File written:\nLine 1\r\nLine 2\twith\ttabs")]));

        input.Insert(4, new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call2", "read_file",
                new Dictionary<string, object?> { ["path"] = "file.txt" })]));
        input.Insert(5, new ChatMessage(ChatRole.Tool,
            [new FunctionResultContent("call2", "Content:\nMultiple\n\nlines\r\nwith\r\rwhitespace")]));

        // Fit with a tight budget to trigger collapse
        IReadOnlyList<ChatMessage> fitted = await fitter.FitAsync(input, null, 20_000, 1.0, CancellationToken.None);

        // Find the merged message (should have header "[earlier tool calls, shortened]")
        var mergedMessages = fitted
            .Where(m => m.Role == ChatRole.Assistant &&
                        m.Contents.Count == 1 &&
                        m.Contents[0] is TextContent t &&
                        t.Text.StartsWith("[earlier tool calls, shortened]"))
            .ToList();

        // Must have at least one merged message
        Assert.NotEmpty(mergedMessages);

        var mergedText = ((TextContent)mergedMessages[0].Contents[0]).Text;

        // Check precondition: if whitespace is NOT flattened, the merged text would contain
        // the original newlines, carriage returns, and tabs from the tool results.
        // The result strings are:
        //   "File written:\nLine 1\r\nLine 2\twith\ttabs"
        //   "Content:\nMultiple\n\nlines\r\nwith\r\rwhitespace"
        // These contain 5 embedded \n + 3 embedded \r + 2 embedded \t = 10 embedded whitespace chars

        // When split by '\n', without flattening we'd get many more array elements.
        // With flattening, each tool result becomes one line: "File written: Line 1 Line 2 with tabs" (space-separated).
        var lines = mergedText.Split('\n');

        // First line is the header
        Assert.Equal("[earlier tool calls, shortened]", lines[0]);

        // Count of numbered lines: each should start with a digit and contain ". "
        var numberedLines = lines.Skip(1)
            .Where(l => !string.IsNullOrWhiteSpace(l) && char.IsDigit(l[0]) && l.Contains(". "))
            .ToList();

        // Must have at least 2 numbered lines for our 2 tool calls
        Assert.True(numberedLines.Count >= 2,
            $"Expected at least 2 numbered lines for the tool calls. Got {numberedLines.Count}.");

        // Check that the merged message has NOT been fragmented by embedded newlines.
        // If whitespace was NOT flattened, we'd have many non-numbered lines from the embedded
        // newlines in the tool results. Count them.
        var nonNumberedLines = lines.Skip(1)
            .Where(l => !string.IsNullOrWhiteSpace(l) && (!char.IsDigit(l[0]) || !l.Contains(". ")))
            .ToList();

        // If flattening is applied, we should have few or no non-numbered lines.
        // Without flattening, we would have 8+ non-numbered lines from the result strings.
        Assert.True(nonNumberedLines.Count <= 3,
            $"Found {nonNumberedLines.Count} non-numbered lines, suggesting whitespace was not flattened. " +
            $"Lines: {string.Join("|", nonNumberedLines)}");
    }
}
