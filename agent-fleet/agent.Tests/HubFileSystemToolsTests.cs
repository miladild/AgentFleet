using System.Text;
using AgentFleet;

namespace AgentFleet.Tests;

public sealed class HubFileSystemToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fleet-fs-tests-" + Guid.NewGuid().ToString("N"));

    public HubFileSystemToolsTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string relative, string content, Encoding? encoding = null)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, encoding ?? new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public async Task EditFile_replaces_unique_text_and_reports_the_line()
    {
        string file = Write("a.txt", "one\ntwo\nthree\n");

        string result = await HubFileSystemTools.EditFileAsync(file, "two", "TWO", null, default);

        Assert.Equal("one\nTWO\nthree\n", File.ReadAllText(file));
        Assert.Contains("line 2", result);
    }

    [Fact]
    public async Task EditFile_matches_multi_line_text_in_a_CRLF_file_when_the_model_sends_LF()
    {
        string file = Write("crlf.txt", "first\r\nsecond\r\nthird\r\n");

        string result = await HubFileSystemTools.EditFileAsync(file, "first\nsecond", "1\n2", null, default);

        Assert.DoesNotContain("Error", result);
        Assert.Equal("1\r\n2\r\nthird\r\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task EditFile_refuses_an_ambiguous_match_unless_replaceAll()
    {
        string file = Write("dup.txt", "x = 1\nx = 1\n");

        string refused = await HubFileSystemTools.EditFileAsync(file, "x = 1", "x = 2", null, default);
        Assert.StartsWith("Error", refused);
        Assert.Contains("2 places", refused);
        Assert.Equal("x = 1\nx = 1\n", File.ReadAllText(file));

        string all = await HubFileSystemTools.EditFileAsync(file, "x = 1", "x = 2", true, default);
        Assert.Contains("2 occurrences", all);
        Assert.Equal("x = 2\nx = 2\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task EditFile_reports_missing_text_missing_file_and_no_op_edits()
    {
        string file = Write("b.txt", "hello\n");

        Assert.Contains("not found", await HubFileSystemTools.EditFileAsync(file, "absent", "x", null, default));
        Assert.StartsWith("Error: file not found", await HubFileSystemTools.EditFileAsync(Path.Combine(_root, "nope.txt"), "a", "b", null, default));
        Assert.Contains("identical", await HubFileSystemTools.EditFileAsync(file, "hello", "hello", null, default));
        Assert.Contains("must not be empty", await HubFileSystemTools.EditFileAsync(file, "", "x", null, default));
        Assert.Equal("hello\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task EditFile_keeps_a_UTF8_byte_order_mark()
    {
        string file = Write("bom.txt", "café\n", new UTF8Encoding(true));

        await HubFileSystemTools.EditFileAsync(file, "café", "cafe", null, default);

        byte[] bytes = File.ReadAllBytes(file);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal("cafe\n", Encoding.UTF8.GetString(bytes[3..]));
    }

    [Fact]
    public async Task ReadFile_returns_a_line_range_with_a_header()
    {
        string file = Write("lines.txt", "l1\nl2\nl3\nl4\nl5\n");

        string slice = await HubFileSystemTools.ReadFileAsync(file, 2, 4, default);

        Assert.StartsWith("[lines 2-4 of 6]", slice);
        Assert.Contains("l2\nl3\nl4", slice);
        Assert.DoesNotContain("l1", slice);
        Assert.DoesNotContain("l5", slice);
    }

    [Fact]
    public async Task ReadFile_without_a_range_returns_the_whole_file()
    {
        string file = Write("whole.txt", "abc");

        Assert.Equal("abc", await HubFileSystemTools.ReadFileAsync(file, null, null, default));
    }

    [Fact]
    public void FindFiles_matches_names_and_paths_and_skips_dependency_folders()
    {
        Write("src/App.cs", "");
        Write("src/deep/Util.cs", "");
        Write("src/readme.md", "");
        Write("node_modules/pkg/index.cs", "");
        Write("bin/Debug/Out.cs", "");

        string byName = HubFileSystemTools.FindFiles("*.cs", _root);
        Assert.Contains("src/App.cs", byName);
        Assert.Contains("src/deep/Util.cs", byName);
        Assert.DoesNotContain("node_modules", byName);
        Assert.DoesNotContain("bin/", byName);
        Assert.DoesNotContain("readme.md", byName);

        string byPath = HubFileSystemTools.FindFiles("src/**/*.cs", _root);
        Assert.Contains("src/App.cs", byPath);
        Assert.Contains("src/deep/Util.cs", byPath);

        Assert.StartsWith("No files matching", HubFileSystemTools.FindFiles("*.rs", _root));
    }

    [Fact]
    public void SearchFiles_returns_path_line_text_and_honours_filters()
    {
        Write("a.cs", "class Foo {}\nvar x = 1;\n// TODO fix\n");
        Write("b.txt", "todo lowercase\n");
        Write("node_modules/dep.cs", "TODO in a dependency\n");
        File.WriteAllBytes(Path.Combine(_root, "blob.bin"), [0x54, 0x4F, 0x44, 0x4F, 0x00, 0x01]);

        string sensitive = HubFileSystemTools.SearchFiles("TODO", _root, null, null);
        Assert.Contains("a.cs:3: // TODO fix", sensitive);
        Assert.DoesNotContain("b.txt", sensitive);
        Assert.DoesNotContain("node_modules", sensitive);
        Assert.DoesNotContain("blob.bin", sensitive);

        string insensitive = HubFileSystemTools.SearchFiles("todo", _root, null, true);
        Assert.Contains("a.cs:3:", insensitive);
        Assert.Contains("b.txt:1:", insensitive);

        string onlyCs = HubFileSystemTools.SearchFiles("todo", _root, "*.cs", true);
        Assert.Contains("a.cs", onlyCs);
        Assert.DoesNotContain("b.txt", onlyCs);
    }

    [Fact]
    public void SearchFiles_reports_bad_patterns_and_no_matches()
    {
        Write("a.txt", "hello\n");

        Assert.StartsWith("Error: pattern is not a valid regular expression", HubFileSystemTools.SearchFiles("(unclosed", _root, null, null));
        Assert.StartsWith("No matches", HubFileSystemTools.SearchFiles("zzz", _root, null, null));
        Assert.StartsWith("Error: path not found", HubFileSystemTools.SearchFiles("x", Path.Combine(_root, "missing"), null, null));
    }

    [Fact]
    public void SearchFiles_stops_at_the_match_cap()
    {
        Write("many.txt", string.Join("\n", Enumerable.Repeat("needle", 500)));

        string result = HubFileSystemTools.SearchFiles("needle", _root, null, null);

        Assert.Contains("stopped at 100 matches", result);
        Assert.Equal(100, result.Split('\n').Count(line => line.StartsWith("many.txt:")));
    }
}
