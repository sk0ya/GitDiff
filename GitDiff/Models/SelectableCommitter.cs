using CommunityToolkit.Mvvm.ComponentModel;

namespace GitDiff.Models;

public partial class SelectableCommitter : ObservableObject
{
    public string Name { get; init; } = string.Empty;

    [ObservableProperty]
    private bool _isSelected = true;
}
