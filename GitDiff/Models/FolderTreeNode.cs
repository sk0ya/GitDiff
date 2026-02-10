using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GitDiff.Models;

public partial class FolderTreeNode : ObservableObject
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public FolderTreeNode? Parent { get; set; }
    public ObservableCollection<FolderTreeNode> Children { get; } = [];

    [ObservableProperty]
    private bool? _isSelected = true;

    private bool _isUpdating;

    partial void OnIsSelectedChanged(bool? value)
    {
        if (_isUpdating) return;

        _isUpdating = true;
        try
        {
            if (value.HasValue)
            {
                SetChildrenSelected(value.Value);
            }

            Parent?.UpdateFromChildren();
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private void SetChildrenSelected(bool selected)
    {
        foreach (var child in Children)
        {
            child._isUpdating = true;
            child.IsSelected = selected;
            child._isUpdating = false;
            child.SetChildrenSelected(selected);
        }
    }

    public void UpdateFromChildren()
    {
        if (_isUpdating) return;
        if (Children.Count == 0) return;

        _isUpdating = true;
        try
        {
            var allChecked = Children.All(c => c.IsSelected == true);
            var allUnchecked = Children.All(c => c.IsSelected == false);

            if (allChecked) IsSelected = true;
            else if (allUnchecked) IsSelected = false;
            else IsSelected = null;

            Parent?.UpdateFromChildren();
        }
        finally
        {
            _isUpdating = false;
        }
    }
}
