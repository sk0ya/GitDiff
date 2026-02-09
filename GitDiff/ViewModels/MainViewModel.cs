using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitDiff.Models;
using GitDiff.Services;
using Microsoft.Win32;

namespace GitDiff.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IGitService _gitService;
    private readonly IFileExportService _fileExportService;

    private List<CommitInfo> _allCommits = [];

    public MainViewModel()
        : this(new GitService(), new FileExportService(new GitService()))
    {
    }

    public MainViewModel(IGitService gitService, IFileExportService fileExportService)
    {
        _gitService = gitService;
        _fileExportService = fileExportService;
    }

    [ObservableProperty]
    private string _repositoryPath = string.Empty;

    [ObservableProperty]
    private ObservableCollection<CommitInfo> _commits = [];

    [ObservableProperty]
    private ObservableCollection<string> _committers = [];

    [ObservableProperty]
    private string? _selectedCommitter;

    [ObservableProperty]
    private string _folderFilter = string.Empty;

    [ObservableProperty]
    private CommitInfo? _baseCommit;

    [ObservableProperty]
    private CommitInfo? _targetCommit;

    [ObservableProperty]
    private ObservableCollection<DiffFileInfo> _diffFiles = [];

    [ObservableProperty]
    private string _outputPath = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private int _diffFileCount;

    public bool HasBaseCommit => BaseCommit != null;
    public bool HasTargetCommit => TargetCommit != null;

    partial void OnSelectedCommitterChanged(string? value) => ApplyFilters();
    partial void OnFolderFilterChanged(string value) => ApplyFilters();

    partial void OnBaseCommitChanged(CommitInfo? value)
    {
        OnPropertyChanged(nameof(HasBaseCommit));
        CompareCommand.NotifyCanExecuteChanged();
    }

    partial void OnTargetCommitChanged(CommitInfo? value)
    {
        OnPropertyChanged(nameof(HasTargetCommit));
        CompareCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task BrowseRepository()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Gitリポジトリフォルダを選択"
        };

        if (dialog.ShowDialog() == true)
        {
            RepositoryPath = dialog.FolderName;
            await LoadRepository();
        }
    }

    [RelayCommand]
    private async Task LoadRepository()
    {
        if (string.IsNullOrWhiteSpace(RepositoryPath))
        {
            StatusMessage = "リポジトリパスを指定してください。";
            return;
        }

        if (!_gitService.IsValidRepository(RepositoryPath))
        {
            StatusMessage = "有効なGitリポジトリではありません。";
            return;
        }

        IsLoading = true;
        StatusMessage = "リポジトリを読み込み中...";

        try
        {
            await Task.Run(() =>
            {
                _allCommits = _gitService.GetCommits(RepositoryPath).ToList();
                var committers = _gitService.GetCommitters(RepositoryPath);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    Committers = new ObservableCollection<string>(committers);
                    SelectedCommitter = null;
                    FolderFilter = string.Empty;
                    BaseCommit = null;
                    TargetCommit = null;
                    DiffFiles = [];
                    DiffFileCount = 0;
                    ApplyFilters();
                });
            });

            StatusMessage = $"リポジトリを読み込みました。コミット数: {_allCommits.Count}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"エラー: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void SetBaseCommit(CommitInfo? commit)
    {
        if (commit == null) return;
        if (TargetCommit == commit)
            TargetCommit = null;
        BaseCommit = commit;
    }

    [RelayCommand]
    private void SetTargetCommit(CommitInfo? commit)
    {
        if (commit == null) return;
        if (BaseCommit == commit)
            BaseCommit = null;
        TargetCommit = commit;
    }

    private bool CanCompare() => BaseCommit != null && TargetCommit != null;

    [RelayCommand(CanExecute = nameof(CanCompare))]
    private async Task Compare()
    {
        if (BaseCommit == null || TargetCommit == null) return;

        IsLoading = true;
        StatusMessage = "差分を抽出中...";

        try
        {
            var baseHash = BaseCommit.Hash;
            var targetHash = TargetCommit.Hash;
            var repoPath = RepositoryPath;
            var folderFilter = FolderFilter;

            var files = await Task.Run(() =>
            {
                var diffs = _gitService.GetDiffFiles(repoPath, baseHash, targetHash);

                if (!string.IsNullOrWhiteSpace(folderFilter))
                {
                    diffs = diffs
                        .Where(f => f.FilePath.StartsWith(folderFilter, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }

                return diffs;
            });

            DiffFiles = new ObservableCollection<DiffFileInfo>(files);
            DiffFileCount = files.Count;
            StatusMessage = $"差分ファイル: {files.Count} 件";
        }
        catch (Exception ex)
        {
            StatusMessage = $"エラー: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void BrowseOutputFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "出力先フォルダを選択"
        };

        if (dialog.ShowDialog() == true)
        {
            OutputPath = dialog.FolderName;
        }
    }

    [RelayCommand]
    private async Task Export()
    {
        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            StatusMessage = "出力先フォルダを指定してください。";
            return;
        }

        if (DiffFiles.Count == 0)
        {
            StatusMessage = "エクスポートする差分ファイルがありません。";
            return;
        }

        if (TargetCommit == null) return;

        IsLoading = true;
        StatusMessage = "エクスポート中...";

        try
        {
            var repoPath = RepositoryPath;
            var commitHash = TargetCommit.Hash;
            var files = DiffFiles.ToList();
            var outputPath = OutputPath;

            var count = await Task.Run(() =>
                _fileExportService.ExportDiffFiles(repoPath, commitHash, files, outputPath));

            StatusMessage = $"エクスポート完了: {count} ファイルを出力しました。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"エクスポートエラー: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyFilters()
    {
        var filtered = _allCommits.AsEnumerable();

        if (!string.IsNullOrEmpty(SelectedCommitter))
        {
            filtered = filtered.Where(c => c.Author == SelectedCommitter);
        }

        Commits = new ObservableCollection<CommitInfo>(filtered);
        StatusMessage = $"表示中のコミット: {Commits.Count} / {_allCommits.Count}";
    }
}
