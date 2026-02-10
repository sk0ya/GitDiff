namespace GitDiff.Models;

public class SideBySideLine
{
    public int? LeftLineNumber { get; init; }
    public string LeftContent { get; init; } = string.Empty;
    public DiffLineType LeftType { get; init; } = DiffLineType.Context;
    public int? RightLineNumber { get; init; }
    public string RightContent { get; init; } = string.Empty;
    public DiffLineType RightType { get; init; } = DiffLineType.Context;
}
