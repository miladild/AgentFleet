namespace AgentFleet.Tests;

// How a plan run treats a machine, from the node's own settings: a machine with a small context window rules on a disputed test from
// the prompt alone, because every file it reads is another model call that carries the whole conversation again.
public sealed class MachineProfileTests
{
    private static FleetNodeDefinition Node(int? window) =>
        new("n", new Uri("http://n.example.test/v1"), "m", "worker", FleetTiers.Standard, Vision: false, Fallback: false, ContextLength: window);

    private static string? NoSetting(string _) => null;

    [Theory]
    [InlineData(8192, false)]
    [InlineData(16384, false)]
    [InlineData(16385, true)]
    [InlineData(24576, true)]
    [InlineData(null, true)]
    public void A_machine_reads_files_as_a_ruler_only_when_its_window_is_above_the_small_limit(int? window, bool reads) =>
        Assert.Equal(reads, MachineProfile.For(Node(window), NoSetting).RulerReadsFiles);

    [Fact]
    public void A_machine_nothing_is_known_about_reads_as_every_ruler_did_before() =>
        Assert.True(MachineProfile.For(null, NoSetting).RulerReadsFiles);

    [Theory]
    [InlineData("on", 16384, true)]
    [InlineData("ON", 8192, true)]
    [InlineData("off", 24576, false)]
    [InlineData("auto", 16384, false)]
    [InlineData("auto", 24576, true)]
    [InlineData("", 16384, false)]
    public void The_setting_overrides_the_window_for_every_machine(string value, int window, bool reads) =>
        Assert.Equal(reads, MachineProfile.For(Node(window), _ => value).RulerReadsFiles);

    [Theory]
    [InlineData(16384, 32652)]   // (16384 - 5500 tokens kept for the answers) * 3 characters
    [InlineData(24576, 57228)]
    [InlineData(6000, 8000)]     // a very small window still gets the least prompt
    [InlineData(null, null)]     // an unknown window is not cut
    public void A_rulers_prompt_is_cut_to_what_leaves_room_for_its_answers_in_the_window(int? window, int? characters) =>
        Assert.Equal(characters, MachineProfile.For(Node(window), NoSetting).RulerPromptCharacters);

    [Fact]
    public void The_reason_says_what_decided_it()
    {
        Assert.Contains("16384", MachineProfile.For(Node(16384), NoSetting).Reason, StringComparison.Ordinal);
        Assert.Contains(MachineProfile.RulerReadsSetting, MachineProfile.For(Node(16384), _ => "on").Reason, StringComparison.Ordinal);
        Assert.Contains("not set", MachineProfile.For(Node(null), NoSetting).Reason, StringComparison.Ordinal);
    }
}
