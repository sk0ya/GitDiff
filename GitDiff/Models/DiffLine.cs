namespace GitDiff.Models;

public enum DiffLineType
{
    Context,
    Added,
    Deleted,
    Hunk
}

public class DiffLine
{
    public int? OldLineNumber { get; init; }
    public int? NewLineNumber { get; init; }
    public string Content { get; init; } = string.Empty;
    public DiffLineType Type { get; init; }
}
