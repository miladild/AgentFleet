namespace AgentFleet;

// The project's own top-level notes can state rules that the failing tests only sample. The step prompt names them
// in a fixed order, so the same folder always gives the same prompt text.
internal static class PlanProjectDocuments
{
    internal const long MaxBytes = 200_000;

    private static readonly string[] KnownNames = ["PLAN.md", "SPEC.md", "REQUIREMENTS.md", "ARCHITECTURE.md", "DESIGN.md", "README.md"];

    internal static IReadOnlyList<string> Find(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return [];
        }

        try
        {
            // Top-level files only: a folder is never named, and a link or an oversized file is skipped.
            var present = new List<string>();
            foreach (string path in Directory.EnumerateFiles(folder))
            {
                if (new FileInfo(path).Length <= MaxBytes && !PlanRunner.HasLinkedPathComponent(folder, path))
                {
                    present.Add(Path.GetFileName(path));
                }
            }

            var found = new List<string>();
            foreach (string known in KnownNames)
            {
                // Matched without regard to case, but named as the file is on disk.
                string? match = present
                    .Where(name => string.Equals(name, known, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (match is not null)
                {
                    found.Add(match);
                }
            }

            return found;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
