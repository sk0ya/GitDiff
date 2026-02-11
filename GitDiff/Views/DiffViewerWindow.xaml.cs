using System.Linq;
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
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ScrollToFirstChange);
                    break;
            }
        };

        Loaded += async (_, _) =>
        {
            InitializeScrollSync();
            await viewModel.LoadCommand.ExecuteAsync(null);
        };
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

    private void ScrollToFirstChange()
    {
        if (DataContext is not DiffViewerViewModel vm) return;

        if (vm.IsSideBySideMode)
        {
            var firstChange = vm.SideBySideLines.FirstOrDefault(l =>
                l.LeftType is DiffLineType.Added or DiffLineType.Deleted ||
                l.RightType is DiffLineType.Added or DiffLineType.Deleted);
            if (firstChange != null)
                ScrollToCenter(RightDiffList, firstChange);
        }
        else
        {
            var firstChange = vm.DiffLines.FirstOrDefault(l =>
                l.Type is DiffLineType.Added or DiffLineType.Deleted);
            if (firstChange != null)
                ScrollToCenter(UnifiedDiffList, firstChange);
        }
    }

    private void ScrollToCenter(ListView listView, object item)
    {
        // まずScrollIntoViewでコンテナを実体化させる（仮想化対策）
        listView.ScrollIntoView(item);
        listView.UpdateLayout();

        var container = listView.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
        var scrollViewer = GetScrollViewer(listView);
        if (container == null || scrollViewer == null) return;

        var itemTop = container.TranslatePoint(new Point(0, 0), scrollViewer).Y;
        var centerOffset = scrollViewer.VerticalOffset + itemTop
                           - (scrollViewer.ViewportHeight - container.ActualHeight) / 2;
        scrollViewer.ScrollToVerticalOffset(Math.Max(0, centerOffset));
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
}
