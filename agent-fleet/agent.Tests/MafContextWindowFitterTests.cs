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

    // I8: Collapse - collapsed lines are valid, bounded.
    [Fact]
    public async Task I8_collapse_lines_valid()
    {
        var fitter = new MafContextWindowFitter(collapseToolResults: true);
        List<ChatMessage> input = ContextWindowTestHelper.BuildConversation(35);

        // Add a large write_file
        input.Add(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("large_write", "write_file",
                new Dictionary<string, object?> { ["path"] = "file.txt", ["content"] = new string('x', 5_000) })]));
        input.Add(new ChatMessage(ChatRole.Tool,
            [new FunctionResultContent("large_write", "Wrote file")]));

        IReadOnlyList<ChatMessage> fitted = await fitter.FitAsync(input, null, 20_000, 1.0, CancellationToken.None);

        var collapsed = fitted
            .Where(m => m.Role == ChatRole.Assistant)
            .SelectMany(m => m.Contents.OfType<TextContent>())
            .Where(t => t.Text.StartsWith("[earlier tool call]"))
            .ToList();

        foreach (var line in collapsed)
        {
            // No function calls in the text
            Assert.DoesNotContain("FunctionCallContent", line.Text);

            // RemovedMarker at most once
            int markerCount = line.Text.Split(ContextSizeChatClient.RemovedMarker, StringSplitOptions.None).Length - 1;
            Assert.True(markerCount <= 1);

            // Content is bounded
            Assert.True(line.Text.Length < 2000);
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
}
