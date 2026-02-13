using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GitDiff.Models;
using GitDiff.ViewModels;

namespace GitDiff.Views;

public partial class DiffViewerWindow : Window
{
    private ScrollViewer? _leftScrollViewer;
    private ScrollViewer? _rightScrollViewer;
    private bool _isSyncingScroll;

    // Change navigation state
    private List<int> _changeGroupIndices = [];
    private int _currentChangeGroupIndex = -1;

    // Drag selection state
    private ListView? _dragListView;
    private int _dragStartIndex = -1;

    public DiffViewerWindow(DiffViewerViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();

        viewModel.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(DiffViewerViewModel.DiffLines):
                case nameof(DiffViewerViewModel.SideBySideLines):
                case nameof(DiffViewerViewModel.IsSideBySideMode):
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                    {
                        BuildChangeGroupIndices();
                        NavigateToNextChange();
                    });
                    break;
            }
        };

        Loaded += async (_, _) =>
        {
            InitializeScrollSync();
            await viewModel.LoadCommand.ExecuteAsync(null);
        };

        // Ctrl+C support for all diff ListViews
        UnifiedDiffList.KeyDown += OnDiffListKeyDown;
        LeftDiffList.KeyDown += OnDiffListKeyDown;
        RightDiffList.KeyDown += OnDiffListKeyDown;

        // Drag selection for all diff ListViews
        foreach (var lv in new[] { UnifiedDiffList, LeftDiffList, RightDiffList })
        {
            lv.PreviewMouseLeftButtonDown += OnDiffListPreviewMouseDown;
            lv.PreviewMouseMove += OnDiffListPreviewMouseMove;
            lv.PreviewMouseLeftButtonUp += OnDiffListPreviewMouseUp;
        }
    }

    private void InitializeScrollSync()
    {
        _leftScrollViewer = GetScrollViewer(LeftDiffList);
        _rightScrollViewer = GetScrollViewer(RightDiffList);

        if (_leftScrollViewer != null)
            _leftScrollViewer.ScrollChanged += OnLeftScrollChanged;
        if (_rightScrollViewer != null)
            _rightScrollViewer.ScrollChanged += OnRightScrollChanged;

        // Forward mouse wheel on left ListView to vertical scroll
        LeftDiffList.PreviewMouseWheel += OnLeftListPreviewMouseWheel;
    }

    private void OnLeftScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_isSyncingScroll || e.VerticalChange == 0) return;
        _isSyncingScroll = true;
        _rightScrollViewer?.ScrollToVerticalOffset(e.VerticalOffset);
        _isSyncingScroll = false;
    }

    private void OnRightScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_isSyncingScroll || e.VerticalChange == 0) return;
        _isSyncingScroll = true;
        _leftScrollViewer?.ScrollToVerticalOffset(e.VerticalOffset);
        _isSyncingScroll = false;
    }

    private void OnLeftListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Left ListView has hidden vertical scrollbar, so forward wheel to right
        if (_rightScrollViewer != null)
        {
            _rightScrollViewer.ScrollToVerticalOffset(
                _rightScrollViewer.VerticalOffset - e.Delta);
            e.Handled = true;
        }
    }

    private void UpdateChangeCountDisplay()
    {
        if (_changeGroupIndices.Count == 0)
        {
            ChangeCountText.Text = "";
            return;
        }
        ChangeCountText.Text = $"{_currentChangeGroupIndex + 1}/{_changeGroupIndices.Count}";
    }

    private void BuildChangeGroupIndices()
    {
        _changeGroupIndices.Clear();
        _currentChangeGroupIndex = -1;
        UpdateChangeCountDisplay();

        if (DataContext is not DiffViewerViewModel vm) return;

        if (vm.IsSideBySideMode)
        {
            var lines = vm.SideBySideLines;
            for (int i = 0; i < lines.Count; i++)
            {
                bool isChange = lines[i].LeftType is DiffLineType.Added or DiffLineType.Deleted
                             || lines[i].RightType is DiffLineType.Added or DiffLineType.Deleted;
                if (!isChange) continue;

                bool prevIsChange = i > 0
                    && (lines[i - 1].LeftType is DiffLineType.Added or DiffLineType.Deleted
                     || lines[i - 1].RightType is DiffLineType.Added or DiffLineType.Deleted);
                if (!prevIsChange)
                    _changeGroupIndices.Add(i);
            }
        }
        else
        {
            var lines = vm.DiffLines;
            for (int i = 0; i < lines.Count; i++)
            {
                bool isChange = lines[i].Type is DiffLineType.Added or DiffLineType.Deleted;
                if (!isChange) continue;

                bool prevIsChange = i > 0
                    && lines[i - 1].Type is DiffLineType.Added or DiffLineType.Deleted;
                if (!prevIsChange)
                    _changeGroupIndices.Add(i);
            }
        }
    }

    private void NavigateToNextChange()
    {
        if (_changeGroupIndices.Count == 0) return;

        if (_currentChangeGroupIndex < _changeGroupIndices.Count - 1)
            _currentChangeGroupIndex++;

        ScrollToChangeGroup(_currentChangeGroupIndex);
    }

    private void NavigateToPreviousChange()
    {
        if (_changeGroupIndices.Count == 0) return;

        if (_currentChangeGroupIndex > 0)
            _currentChangeGroupIndex--;

        ScrollToChangeGroup(_currentChangeGroupIndex);
    }

    private void ScrollToChangeGroup(int groupIndex)
    {
        if (groupIndex < 0 || groupIndex >= _changeGroupIndices.Count) return;
        if (DataContext is not DiffViewerViewModel vm) return;

        UpdateChangeCountDisplay();

        var itemIndex = _changeGroupIndices[groupIndex];

        if (vm.IsSideBySideMode)
        {
            if (itemIndex < vm.SideBySideLines.Count)
                ScrollToCenter(RightDiffList, vm.SideBySideLines[itemIndex]);
        }
        else
        {
            if (itemIndex < vm.DiffLines.Count)
                ScrollToCenter(UnifiedDiffList, vm.DiffLines[itemIndex]);
        }
    }

    private void OnPrevChangeClick(object sender, RoutedEventArgs e)
    {
        NavigateToPreviousChange();
    }

    private void OnNextChangeClick(object sender, RoutedEventArgs e)
    {
        NavigateToNextChange();
    }

    private void ScrollToCenter(ListView listView, object item)
    {
        var scrollViewer = GetScrollViewer(listView);
        var savedHorizontal = scrollViewer?.HorizontalOffset ?? 0;

        // まずScrollIntoViewでコンテナを実体化させる（仮想化対策）
        listView.ScrollIntoView(item);
        listView.UpdateLayout();

        var container = listView.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
        if (container == null || scrollViewer == null) return;

        var itemTop = container.TranslatePoint(new Point(0, 0), scrollViewer).Y;
        var centerOffset = scrollViewer.VerticalOffset + itemTop
                           - (scrollViewer.ViewportHeight - container.ActualHeight) / 2;
        scrollViewer.ScrollToVerticalOffset(Math.Max(0, centerOffset));
        scrollViewer.ScrollToHorizontalOffset(savedHorizontal);
    }

    private static ScrollViewer? GetScrollViewer(DependencyObject element)
    {
        if (element is ScrollViewer sv)
            return sv;

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            var result = GetScrollViewer(child);
            if (result != null)
                return result;
        }

        return null;
    }

    private void OnDiffListPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var listView = (ListView)sender;
        var index = GetItemIndexAtPoint(listView, e.GetPosition(listView));
        if (index < 0) return;

        _dragListView = listView;
        _dragStartIndex = index;

        listView.SelectedItems.Clear();
        listView.SelectedIndex = index;

        listView.CaptureMouse();
        e.Handled = true;
    }

    private void OnDiffListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        var listView = (ListView)sender;
        if (_dragListView != listView || _dragStartIndex < 0) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var index = GetItemIndexAtPoint(listView, e.GetPosition(listView));
        if (index < 0) return;

        var start = Math.Min(_dragStartIndex, index);
        var end = Math.Max(_dragStartIndex, index);

        listView.SelectedItems.Clear();
        for (int i = start; i <= end; i++)
            listView.SelectedItems.Add(listView.Items[i]);
    }

    private void OnDiffListPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        var listView = (ListView)sender;
        if (_dragListView == listView)
        {
            listView.ReleaseMouseCapture();
            _dragListView = null;
            _dragStartIndex = -1;
        }
    }

    private int GetItemIndexAtPoint(ListView listView, Point point)
    {
        var hit = VisualTreeHelper.HitTest(listView, point);
        if (hit?.VisualHit == null) return -1;

        // Walk up the visual tree to find the ListViewItem
        DependencyObject? current = hit.VisualHit;
        while (current != null && current != listView)
        {
            if (current is ListViewItem item)
                return listView.ItemContainerGenerator.IndexFromContainer(item);
            current = VisualTreeHelper.GetParent(current);
        }

        return -1;
    }

    private void OnDiffListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CopySelectedLines((ListView)sender);
            e.Handled = true;
        }
    }

    private void CopyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        // ContextMenu.PlacementTarget is the ListView that owns the context menu
        if (sender is MenuItem menuItem &&
            menuItem.Parent is ContextMenu contextMenu &&
            contextMenu.PlacementTarget is ListView listView)
        {
            CopySelectedLines(listView);
        }
    }

    private void CopySelectedLines(ListView listView)
    {
        if (listView.SelectedItems.Count == 0) return;

        var sb = new StringBuilder();

        if (listView == UnifiedDiffList)
        {
            foreach (DiffLine line in listView.SelectedItems)
                sb.AppendLine(line.Content);
        }
        else if (listView == LeftDiffList)
        {
            foreach (SideBySideLine line in listView.SelectedItems)
                sb.AppendLine(line.LeftContent ?? "");
        }
        else if (listView == RightDiffList)
        {
            foreach (SideBySideLine line in listView.SelectedItems)
                sb.AppendLine(line.RightContent ?? "");
        }

        var text = sb.ToString().TrimEnd('\r', '\n');
        if (!string.IsNullOrEmpty(text))
            Clipboard.SetText(text);
    }
}
