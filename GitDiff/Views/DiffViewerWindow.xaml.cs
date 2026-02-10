using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
