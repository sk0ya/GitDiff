namespace GitDiff.Models;

public class FileCommitInfo
{
    public string Hash { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public DateTimeOffset Date { get; init; }
    public string ParentHash { get; init; } = string.Empty;

    public string ShortHash => Hash.Length >= 7 ? Hash[..7] : Hash;
    public string ShortMessage
    {
        get
        {
            var firstLine = Message.Split('\n', 2)[0].Trim();
            return firstLine.Length > 50 ? firstLine[..50] + "..." : firstLine;
        }
    }
    public string TooltipText => $"{ShortHash} - {Author}\n{Date.LocalDateTime:yyyy/MM/dd HH:mm}\n{Message}";
}
