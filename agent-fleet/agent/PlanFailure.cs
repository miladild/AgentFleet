using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentFleet;

internal enum FailureClass
{
    Infra,
    Environment,
    CheckDefect,
    CodeProgress,
    Stall,
    NoOp,
    Unknown
}

internal sealed record FailureFingerprint(string Value, int Size, IReadOnlyList<string> Items);

internal sealed record FailureRound(
    string? Signature,
    int SignatureSize,
    int FilesChanged,
    bool EditToolCalled);

internal static partial class PlanFailure
{
    [GeneratedRegex(@"\b(?:error|fatal error)\s+(?<code>(?:CS|TS|MSB|NETSDK)\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CompilerErrorCode();

    [GeneratedRegex(@"^\s*(?:FAILED|FAIL(?:ED)?|✖|✗|×)\s+(?<name>.+?)\s*(?:\(\s*\d+(?:\.\d+)?\s*(?:ms|s)\s*\))?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex FailedTestLine();

    [GeneratedRegex(@"^\s*Failed\s+(?<name>[\w./\\:<>` -]+?)(?:\s+\[\s*\d+(?:\.\d+)?\s*(?:ms|s)\s*\])?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex XunitFailedLine();

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiEscape();

    [GeneratedRegex(@"\b\d+(?:\.\d+)?\s*(?:ms|msec|milliseconds?|seconds?|secs?|s)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ElapsedTime();

    [GeneratedRegex(@"(?:[A-Za-z]:[\\/]|/)[^\s:]+")]
    private static partial Regex AbsolutePath();

    [GeneratedRegex(@"\s+in\s+\d+(?:\.\d+)?\s*(?:ms|s|seconds?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DurationSuffix();

    [GeneratedRegex(@"(?:ParserError|UnexpectedToken|MissingEndCurlyBrace|SyntaxError|syntax error|unexpected token)", RegexOptions.IgnoreCase)]
    private static partial Regex CheckSyntaxError();

    [GeneratedRegex(@"(?:\bSyntaxError\b|\bIndentationError\b|\bParserError\b|\berror\s+(?:CS|TS)\d+|\bBuild FAILED\b|could not compile|compilation (?:failed|error))", RegexOptions.IgnoreCase)]
    private static partial Regex BuildBrokenOutput();

    // esbuild (tsx, vite) prints "Transform failed with N error(s):" and then the file position of each error; swc prints "Failed to parse source".
    [GeneratedRegex(@"Transform failed with \d+ errors?:\s*[\r\n]+[^\r\n]*:\d+:\d+: ERROR:|name:\s*'TransformError'|Failed to parse source", RegexOptions.IgnoreCase)]
    private static partial Regex TransformFailure();

    // A parser's "Unexpected token" that names the source file and line it is in, on the same line.
    [GeneratedRegex(@"Unexpected token[^\r\n]*\.(?:[cm]?[jt]sx?|py|cs):\d+|\.(?:[cm]?[jt]sx?|py|cs):\d+[^\r\n]*Unexpected token", RegexOptions.IgnoreCase)]
    private static partial Regex UnexpectedTokenInSource();

    // A project source file named with its line, or as Python names it ("x.py", line 3): where a syntax error came from.
    [GeneratedRegex(@"\.(?:[cm]?[jt]sx?|py|cs)(?::\d+|""|',\s*line\s+\d+|\(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SourceFileLocation();

    [GeneratedRegex(@"^\s*ℹ tests (?<count>\d+)\s*$")]
    private static partial Regex NodeTestCount();

    [GeneratedRegex(@"^(?:CS|TS|MSB|NETSDK)\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex BuildErrorCode();

    [GeneratedRegex(@"(?:llama-server(?:\.exe)?[^\r\n]{0,80}not found|not found[^\r\n]{0,80}llama-server)", RegexOptions.IgnoreCase)]
    private static partial Regex MissingLlamaServer();

    [GeneratedRegex(@"(?:Worker workspace unavailable:|access is denied|permission denied|unauthorizedaccess|no compatible \.NET SDK|no \.NET SDKs? were found|NETSDK1045|MSB4236)", RegexOptions.IgnoreCase)]
    private static partial Regex EnvironmentFailure();

    [GeneratedRegex(@"(?:command not found|is not recognized as an internal or external command|file not found|cannot find the file|the system cannot find the path)", RegexOptions.IgnoreCase)]
    private static partial Regex MissingCheckInput();

    /// <summary>
    /// Every failing item the check printed, sorted, with no limit. FailureSignature keeps only the first 40 of them for the
    /// repair ladder; a rule that must see every failure (a dispute) reads this list instead.
    /// </summary>
    public static IReadOnlyList<string> FailingItems(string? checkOutput) =>
        FailingItemsIn(AnsiEscape().Replace(checkOutput ?? string.Empty, string.Empty));

    // namesOnly leaves out the runner's "failing tests:" header and the file-level lines that name a test file: they name no test.
    // The dispute reader keeps the file-level lines, because a file that did not load may be the step's own test file.
    private static string[] FailingItemsIn(string output, bool namesOnly = false)
    {
        var items = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (CompilerErrorCode().Match(line) is { Success: true } code)
            {
                items.Add(code.Groups["code"].Value.ToUpperInvariant());
            }

            Match failed = FailedTestLine().Match(line);
            if (!failed.Success) failed = XunitFailedLine().Match(line);
            if (failed.Success && !(namesOnly && TestDispute.SpecFailingHeader().IsMatch(line)))
            {
                string name = NormalizeLine(failed.Groups["name"].Value);
                if (namesOnly && TestDispute.BareTestFile().IsMatch(failed.Groups["name"].Value.Trim())) continue;
                if (name.Length > 0) items.Add(name);
            }
        }

        return items.ToArray();
    }

    public static FailureFingerprint FailureSignature(string? checkOutput)
    {
        string output = AnsiEscape().Replace(checkOutput ?? string.Empty, string.Empty);
        string[] found = FailingItemsIn(output, namesOnly: true);
        if (found.Length > 0)
        {
            string[] names = found.Take(40).ToArray();
            return new FailureFingerprint(string.Join('\n', names), names.Length, names);
        }

        string[] normalized = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeLine)
            .Where(line => line.Length > 0 && !IsSummaryLine(line))
            .TakeLast(40)
            .ToArray();
        if (normalized.Length == 0) return new FailureFingerprint(string.Empty, 0, []);

        string stable = string.Join('\n', normalized);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stable))).ToLowerInvariant();
        return new FailureFingerprint($"sha256:{hash}", 1, []);
    }

    public static FailureClass Classify(
        Exception? exception,
        string? checkOutput,
        IReadOnlyList<FailureRound> roundHistory,
        int filesChanged = 0,
        bool editToolCalled = false)
    {
        if (exception is WorkerPathRefusedException) return FailureClass.Unknown;

        string evidence = string.Join('\n', new[] { exception?.ToString(), checkOutput }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (EnvironmentFailure().IsMatch(evidence)) return FailureClass.Environment;
        if (IsInfrastructure(exception, evidence))
        {
            return FailureClass.Infra;
        }

        if (exception is not null)
        {
            // An environment pattern in the evidence was already answered above.
            return exception is UnauthorizedAccessException ? FailureClass.Environment : FailureClass.Unknown;
        }

        // A syntax error in a project source file is the code's fault, not the check's; the verify command's own parser errors have no such file.
        if (CheckSyntaxError().IsMatch(evidence) && !InSourceFile(evidence)) return FailureClass.CheckDefect;
        if (MissingCheckInput().IsMatch(evidence)) return FailureClass.CheckDefect;

        FailureFingerprint current = FailureSignature(checkOutput);
        if (current.Size == 0) return FailureClass.Unknown;

        FailureRound? previous = roundHistory.LastOrDefault(round => !string.IsNullOrWhiteSpace(round.Signature));
        if (previous is not null)
        {
            if (string.Equals(previous.Signature, current.Value, StringComparison.Ordinal))
            {
                return filesChanged == 0 ? FailureClass.Stall : FailureClass.CodeProgress;
            }

            string[] previousItems = previous.Signature!.StartsWith("sha256:", StringComparison.Ordinal)
                ? []
                : previous.Signature.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (current.Items.Count > 0 && previousItems.Length > current.Items.Count &&
                current.Items.All(item => previousItems.Contains(item, StringComparer.OrdinalIgnoreCase)))
            {
                return FailureClass.CodeProgress;
            }
        }

        if (!editToolCalled && filesChanged == 0) return FailureClass.NoOp;
        return FailureClass.CodeProgress;
    }

    /// <summary>The check printed compile, syntax or build errors: the code does not build, so no test result is to be trusted.</summary>
    internal static bool BuildBroken(string? checkOutput)
    {
        if (string.IsNullOrEmpty(checkOutput)) return false;
        string output = AnsiEscape().Replace(checkOutput, string.Empty);
        // A failing line that names a test is not a build error, whatever words the name uses; one that prints a compiler error code is.
        string[] lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        string unnamed = string.Join('\n', lines.Where(line => !NamesATest(line)));
        return BuildBrokenOutput().IsMatch(unnamed) || TransformFailure().IsMatch(output) ||
               lines.Any(line => !char.IsWhiteSpace(line[0]) && !NamesATest(line) && UnexpectedTokenInSource().IsMatch(line)) ||
               TestFileDidNotLoad(output);
    }

    private static bool NamesATest(string line) =>
        (FailedTestLine().IsMatch(line) || XunitFailedLine().IsMatch(line)) && !CompilerErrorCode().IsMatch(line);

    // A test file that did not load is printed as the one failure, and node counts it as the one test that ran: no test ran at all.
    // A failing line that names a test, or a count of tests that differs from the file lines, means the file did load.
    private static bool TestFileDidNotLoad(string output)
    {
        int files = 0;
        int? tests = null;
        foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (NodeTestCount().Match(line) is { Success: true } count && int.TryParse(count.Groups["count"].Value, out int counted))
            {
                tests = counted;
            }

            Match failed = FailedTestLine().Match(line);
            if (!failed.Success) failed = XunitFailedLine().Match(line);
            if (!failed.Success || TestDispute.SpecFailingHeader().IsMatch(line)) continue;
            if (!TestDispute.BareTestFile().IsMatch(failed.Groups["name"].Value.Trim())) return false;
            files++;
        }

        return files > 0 && tests == files;
    }

    private static bool InSourceFile(string evidence)
    {
        string[] lines = evidence.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < lines.Length; index++)
        {
            if (!CheckSyntaxError().IsMatch(lines[index])) continue;
            for (int above = Math.Max(0, index - 5); above <= index; above++)
            {
                if (SourceFileLocation().IsMatch(lines[above])) return true;
            }
        }

        return false;
    }

    /// <summary>A failure signature item that is a compiler or build error code (CS1002, TS2322) and not a failing test.</summary>
    internal static bool IsBuildErrorCode(string item) => BuildErrorCode().IsMatch(item);

    internal static bool IsInfrastructure(Exception? exception, string? evidence = null)
    {
        if (MissingLlamaServer().IsMatch(evidence ?? string.Empty)) return true;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (MissingLlamaServer().IsMatch(current.Message)) return true;
            switch (current)
            {
                case OllamaSharp.Models.Exceptions.ModelDoesNotSupportToolsException:
                    return false;
                case Renci.SshNet.Common.SshConnectionException or Renci.SshNet.Common.SshOperationTimeoutException:
                    return true;
                case OllamaSharp.Models.Exceptions.OllamaException:
                case HttpRequestException or System.Net.Sockets.SocketException or IOException or TimeoutException:
                case System.ClientModel.ClientResultException { Status: 0 or 408 or 429 or >= 500 }:
                    return true;
                case AggregateException aggregate when aggregate.InnerExceptions.Any(inner => IsInfrastructure(inner)):
                    return true;
            }
        }

        return false;
    }

    private static string NormalizeLine(string line)
    {
        string normalized = DurationSuffix().Replace(line, string.Empty);
        normalized = ElapsedTime().Replace(normalized, "<time>");
        normalized = AbsolutePath().Replace(normalized, "<path>");
        normalized = Regex.Replace(normalized, @"(?<=\bline\s)\d+\b|(?<=\bchar\s)\d+\b", "<n>", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized.Length <= 300 ? normalized : normalized[..300];
    }

    private static bool IsSummaryLine(string line) =>
        line.StartsWith("Exit code:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("--- stdout", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("--- stderr", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Test run ", StringComparison.OrdinalIgnoreCase) && line.Contains("passed", StringComparison.OrdinalIgnoreCase);
}
