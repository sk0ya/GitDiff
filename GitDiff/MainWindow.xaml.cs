using System.Windows;
using System.Windows.Controls;
using GitDiff.ViewModels;

namespace GitDiff;

public partial class MainWindow : Window
{
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
}
