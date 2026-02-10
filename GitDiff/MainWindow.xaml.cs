using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GitDiff.Models;
using GitDiff.ViewModels;
using GitDiff.Views;

namespace GitDiff;

public partial class MainWindow : Window
{
    private readonly List<CommitInfo> _selectionOrder = [];

    public MainWindow()
    {
        InitializeComponent();
    }

    private void ClearCommitterFilter_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.SelectedCommitter = null;
        }
    }

    private void CommitsGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Find the clicked DataGridRow
        var dep = (DependencyObject)e.OriginalSource;
        while (dep != null && dep is not DataGridRow)
            dep = VisualTreeHelper.GetParent(dep);

        if (dep is not DataGridRow row) return;
        if (row.Item is not CommitInfo commit) return;

        e.Handled = true;
        var grid = (DataGrid)sender;

        if (grid.SelectedItems.Contains(commit))
        {
            // Toggle off
            _selectionOrder.Remove(commit);
            grid.SelectedItems.Remove(commit);
        }
        else
        {
            // If already 2 selected, remove the oldest
            if (_selectionOrder.Count >= 2)
            {
                var oldest = _selectionOrder[0];
                _selectionOrder.RemoveAt(0);
                grid.SelectedItems.Remove(oldest);
            }
            _selectionOrder.Add(commit);
            grid.SelectedItems.Add(commit);
        }

        UpdateBaseTarget();
    }

    private void DiffFilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (vm.BaseCommit == null || vm.TargetCommit == null) return;

        var dep = (DependencyObject)e.OriginalSource;
        while (dep != null && dep is not DataGridRow)
            dep = VisualTreeHelper.GetParent(dep);

        if (dep is not DataGridRow row) return;
        if (row.Item is not DiffFileInfo fileInfo) return;

        var diffVm = new DiffViewerViewModel(
            vm.GitService, vm.RepositoryPath,
            vm.BaseCommit.Hash, vm.TargetCommit.Hash, fileInfo);

        var window = new DiffViewerWindow(diffVm) { Owner = this };
        window.Show();
    }

    private void UpdateBaseTarget()
    {
        if (DataContext is not MainViewModel vm) return;

        if (_selectionOrder.Count == 2)
        {
            var commits = _selectionOrder.OrderByDescending(c => c.Date).ToList();
            vm.BaseCommit = commits[1];  // older
            vm.TargetCommit = commits[0]; // newer
        }
        else
        {
            vm.BaseCommit = null;
            vm.TargetCommit = null;
        }
    }
}
