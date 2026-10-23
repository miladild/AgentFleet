using AgentFleet;

namespace AgentFleet.Tests;

public sealed class EditMatchingTests
{
    [Fact]
    public void Trailing_spaces_and_tabs_are_ignored_when_the_text_is_otherwise_identical()
    {
        const string content = "const a = 1;  \nconst b = 2;\t\nend\n";

        (int count, int start, int end) = EditMatching.FindLenient(content, "const a = 1;\nconst b = 2;");

        Assert.Equal(1, count);
        Assert.Equal("const a = 1;  \nconst b = 2;\t", content[start..end]);
    }

    [Fact]
    public void A_trailing_line_break_in_oldText_is_part_of_the_matched_span()
    {
        const string content = "a  \nb\n";

        (int count, int start, int end) = EditMatching.FindLenient(content, "a\n");

        Assert.Equal(1, count);
        Assert.Equal("a  \n", content[start..end]);
    }

    [Fact]
    public void A_CRLF_span_ends_before_the_carriage_return_so_the_line_break_survives()
    {
        const string content = "first  \r\nsecond\r\n";

        (int count, int start, int end) = EditMatching.FindLenient(content, "first\r\nsecond");

        Assert.Equal(1, count);
        Assert.Equal("first  \r\nsecond", content[start..end]);
    }

    [Fact]
    public void Leading_indentation_is_never_ignored()
    {
        (int count, _, _) = EditMatching.FindLenient("def f():\n    return 1\n", "def f():\n  return 1");

        Assert.Equal(0, count);
    }

    [Fact]
    public void Line_breaks_are_never_ignored()
    {
        (int count, _, _) = EditMatching.FindLenient("a b\n", "a\nb");

        Assert.Equal(0, count);
    }

    [Fact]
    public void Every_lenient_place_is_counted()
    {
        (int count, _, _) = EditMatching.FindLenient("a \nb\nc\na\t\nb\n", "a\nb");

        Assert.Equal(2, count);
    }

    [Fact]
    public void Unicode_look_alikes_in_oldText_match_their_plain_forms_in_the_file()
    {
        (int quotes, _, _) = EditMatching.FindLenient("say \"hi\" - ok\n", "say \u201Chi\u201D \u2014 ok");
        (int space, _, _) = EditMatching.FindLenient("a b\n", "a\u00A0b");
        (int ellipsis, _, _) = EditMatching.FindLenient("wait...\n", "wait\u2026");
        (int apostrophe, _, _) = EditMatching.FindLenient("it's\n", "it\u2019s  ");

        Assert.Equal(1, quotes);
        Assert.Equal(1, space);
        Assert.Equal(1, ellipsis);
        Assert.Equal(1, apostrophe);
    }

    [Fact]
    public void Not_found_reply_points_at_the_closest_window_and_marks_the_lines_that_differ()
    {
        string reply = EditMatching.NotFoundReply("src/app.js", "src/app.js", "alpha\nbeta\ngamma\ndelta\n", "beta\nGAMMA");

        Assert.StartsWith("Error: oldText was not found in src/app.js. The closest text starts at line 2:\n", reply);
        Assert.Contains("\n2: beta\n", reply);
        Assert.Contains("\n3: gamma (differs from your line 2)\n", reply);
        Assert.EndsWith("Copy the exact lines from here, or read_file the region.", reply);
    }

    [Fact]
    public void Not_found_reply_shows_at_most_ten_lines_and_stays_short()
    {
        string content = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"line {i}")) + "\n";

        string reply = EditMatching.NotFoundReply("f.txt", "f.txt", content, "line 5\nnot here");

        Assert.Contains("The closest text starts at line 5:", reply);
        Assert.Contains("\n14: line 14\n", reply);
        Assert.DoesNotContain("15: line 15", reply);
        Assert.True(reply.Length < 1500, $"reply is {reply.Length} characters");
    }

    [Fact]
    public void Not_found_reply_says_so_when_nothing_resembles_the_text()
    {
        string reply = EditMatching.NotFoundReply("f.txt", "f.txt", "alpha\nbeta\n", "zzz\nyyy");

        Assert.Contains("No part of the file looks like it", reply);
        Assert.Contains("read_file with startLine and endLine", reply);
        Assert.DoesNotContain("closest text starts", reply);
    }

    [Fact]
    public void Not_found_reply_adds_the_escape_hint_only_when_the_region_lacks_the_sequence()
    {
        string missing = EditMatching.NotFoundReply("f.txt", "f.txt", "first\nsecondx\n", "first\nsecond\\n");
        string present = EditMatching.NotFoundReply("f.txt", "f.txt", "first\nsecond\\nY\n", "first\nsecond\\nX");

        Assert.Contains("Your text contains a literal backslash sequence; send real line breaks and characters instead of escape sequences.", missing);
        Assert.DoesNotContain("literal backslash sequence", present);
    }

    [Fact]
    public void Not_found_reply_is_remembered_for_the_guard_with_up_to_forty_numbered_lines()
    {
        string content = string.Join("\n", Enumerable.Range(1, 60).Select(i => $"row {i}")) + "\n";

        EditMatching.NotFoundReply("g.txt", "g.txt", content, "row 20\nmissing");
        EditMatching.MissRegion? region = EditMatching.LastMissRegion("g.txt");

        Assert.NotNull(region);
        Assert.Equal(20, region!.FirstLine);
        Assert.Equal(EditMatching.GuardRegionLines, region.Lines.Count);
        Assert.Equal("20: row 20", region.Lines[0]);
        Assert.Equal("21: row 21 (differs from your line 2)", region.Lines[1]);
        Assert.Equal("59: row 59", region.Lines[39]);
    }

    [Fact]
    public void Ambiguous_lenient_reply_names_the_count_and_the_path()
    {
        Assert.Equal(
            "Error: oldText matches 2 places in f.txt once trailing whitespace is ignored. Include more surrounding lines so it is unique.",
            EditMatching.AmbiguousReply("f.txt", 2));
    }
}
