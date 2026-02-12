using System.Windows;

namespace GitDiff.Views;

public partial class CommitHashInputDialog : Window
{
    public CommitHashInputDialog()
    {
        InitializeComponent();
        HashInput.Focus();
    }

    public List<string> CommitHashes { get; private set; } = [];

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var text = HashInput.Text ?? string.Empty;
        CommitHashes = text
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Distinct()
            .ToList();

        if (CommitHashes.Count == 0)
        {
            MessageBox.Show("コミットハッシュを1つ以上入力してください。", "入力エラー",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
