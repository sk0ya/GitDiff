namespace GitDiff.Models;

public class FileDiffResult
{
    public string RawPatch { get; init; } = string.Empty;
    public IReadOnlyList<DiffLine> Lines { get; init; } = [];
}
