namespace GitDiff.Models;

public class CommitInfo
{
    public string Hash { get; init; } = string.Empty;
    public string ShortHash => Hash.Length >= 7 ? Hash[..7] : Hash;
    public string Message { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public DateTimeOffset Date { get; init; }
    public string DateString => Date.LocalDateTime.ToString("yyyy/MM/dd HH:mm");

    public string DisplayText => $"{ShortHash} - {Message}";
}
