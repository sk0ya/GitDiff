namespace GitDiff.Models;

public enum ChangeStatus
{
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied
}

public class DiffFileInfo
{
    public string FilePath { get; init; } = string.Empty;
    public ChangeStatus Status { get; init; }
    public string? OldPath { get; init; }
    public string? SourceCommitHash { get; init; }
    public string? BaseCommitHash { get; init; }
    public string? AuthorName { get; init; }

    public string StatusText => Status switch
    {
        ChangeStatus.Added => "Added",
        ChangeStatus.Modified => "Modified",
        ChangeStatus.Deleted => "Deleted",
        ChangeStatus.Renamed => "Renamed",
        ChangeStatus.Copied => "Copied",
        _ => "Unknown"
    };
}
