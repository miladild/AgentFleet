namespace AgentFleet;

/// <summary>
/// The worker's files as the edit check sees them. Paths are the model's own spelling; <see cref="Canonical"/> gives one key
/// per file, so "src\\a.ts" and "./src/a.ts" count as one file.
/// </summary>
internal interface IEditCheckFiles
{
    /// <summary>The file's bytes, or null when it does not exist.</summary>
    Task<byte[]?> ReadBytesAsync(string path, CancellationToken cancellationToken);

    /// <summary>Writes the bytes as they are, or deletes the file when they are null.</summary>
    Task WriteBytesAsync(string path, byte[]? contents, CancellationToken cancellationToken);

    string Canonical(string path);
}

/// <summary>How the edit check behaves. On by default; the kill switch turns it into a no-op.</summary>
internal sealed record EditCheckSettings
{
    public bool Enabled { get; init; } = true;

    /// <summary>The longest one check may take. A check that runs longer than this when it is measured disables the gate.</summary>
    public TimeSpan MaximumCheckTime { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How many edits in a row may fail to improve before the best state is put back.</summary>
    public int Patience { get; init; } = 3;

    /// <summary>How many edits to one file that break a loading project are undone before the next one is kept with a warning.</summary>
    public int RefusalsBeforeKeeping { get; init; } = 2;

    /// <summary>
    /// The settings from the three configuration values, as they are read at startup. Anything that is not a recognised
    /// off value leaves the check on; a limit or a patience outside its range, or not a number, takes the default.
    /// </summary>
    public static EditCheckSettings Parse(string? switchValue, string? seconds, string? patience)
    {
        string normalized = (switchValue ?? string.Empty).Trim().ToLowerInvariant();
        int limit = int.TryParse(seconds, out int parsedSeconds) && parsedSeconds is >= 5 and <= 120
            ? parsedSeconds
            : (int)new EditCheckSettings().MaximumCheckTime.TotalSeconds;
        int attempts = int.TryParse(patience, out int parsedPatience) && parsedPatience is >= 1 and <= 10
            ? parsedPatience
            : new EditCheckSettings().Patience;
        return new EditCheckSettings
        {
            Enabled = normalized is not ("off" or "false" or "0"),
            MaximumCheckTime = TimeSpan.FromSeconds(limit),
            Patience = attempts,
        };
    }
}
