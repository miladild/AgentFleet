namespace AgentFleet.Tests;

// The line diff a correction is judged by and shown with: what it removes and adds, ignoring line endings.
public sealed class LineDiffTests
{
    [Fact]
    public void Identical_texts_have_no_changed_lines()
    {
        LineChange change = LineDiff.Compute("a\nb\nc\n", "a\nb\nc\n");

        Assert.Empty(change.Removed);
        Assert.Empty(change.Added);
        Assert.Equal(3, change.OldLines);
        Assert.False(change.TooLarge);
    }

    [Fact]
    public void One_changed_line_is_one_removed_and_one_added()
    {
        LineChange change = LineDiff.Compute("a\nold\nc\n", "a\nnew\nc\n");

        Assert.Equal(["old"], change.Removed);
        Assert.Equal(["new"], change.Added);
    }

    [Fact]
    public void An_added_block_is_only_added_and_keeps_its_order()
    {
        LineChange change = LineDiff.Compute("a\nc\n", "a\nb1\nb2\nc\n");

        Assert.Empty(change.Removed);
        Assert.Equal(["b1", "b2"], change.Added);
    }

    [Fact]
    public void A_removed_block_is_only_removed_and_keeps_its_order()
    {
        LineChange change = LineDiff.Compute("a\nb1\nb2\nc\n", "a\nc\n");

        Assert.Equal(["b1", "b2"], change.Removed);
        Assert.Empty(change.Added);
    }

    [Fact]
    public void Line_endings_do_not_count_as_a_change()
    {
        LineChange change = LineDiff.Compute("a\r\nb\r\n", "a\nb");

        Assert.Empty(change.Removed);
        Assert.Empty(change.Added);
    }

    [Fact]
    public void An_empty_old_text_has_every_new_line_added()
    {
        LineChange change = LineDiff.Compute(string.Empty, "x\ny\n");

        Assert.Equal(0, change.OldLines);
        Assert.Empty(change.Removed);
        Assert.Equal(["x", "y"], change.Added);
    }

    [Fact]
    public void A_file_over_4000_lines_is_too_large_to_compare()
    {
        string big = string.Concat(Enumerable.Repeat("line\n", 4001));

        LineChange change = LineDiff.Compute(big, big + "more\n");

        Assert.True(change.TooLarge);
        Assert.Empty(change.Removed);
        Assert.Empty(change.Added);
    }

    [Fact]
    public void A_corrected_file_over_4000_lines_is_too_large_to_compare_even_when_the_old_one_is_small()
    {
        string corrected = string.Concat(Enumerable.Repeat("line\n", 4001));

        LineChange change = LineDiff.Compute("line\n", corrected);

        Assert.True(change.TooLarge);
        Assert.Equal(1, change.OldLines);
    }
}
