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
    private ObservableCollection<SelectableCommitter> _compareCommitters = [];

    [ObservableProperty]
    private string _compareCommitterLabel = "Committer(All)";

    [ObservableProperty]
    private ObservableCollection<DiffFileInfo> _diffFiles = [];

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
        UpdateCompareCommitters();
    }

    partial void OnTargetCommitChanged(CommitInfo? value)
    {
        OnPropertyChanged(nameof(HasTargetCommit));
        CompareCommand.NotifyCanExecuteChanged();
        UpdateCompareCommitters();
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

            // Get selected committers (empty or all selected = no filter)
            var selectedCommitters = CompareCommitters
                .Where(c => c.IsSelected)
                .Select(c => c.Name)
                .ToList();
            var useCommitterFilter = selectedCommitters.Count > 0
                && selectedCommitters.Count < CompareCommitters.Count;

            var files = await Task.Run(() =>
            {
                var diffs = useCommitterFilter
                    ? _gitService.GetDiffFiles(repoPath, baseHash, targetHash, selectedCommitters)
                    : _gitService.GetDiffFiles(repoPath, baseHash, targetHash);

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
    private async Task Export()
    {
        if (DiffFiles.Count == 0)
        {
            StatusMessage = "エクスポートする差分ファイルがありません。";
            return;
        }

        if (TargetCommit == null) return;

        var dialog = new OpenFolderDialog
        {
            Title = "出力先フォルダを選択"
        };

        if (dialog.ShowDialog() != true) return;

        IsLoading = true;
        StatusMessage = "エクスポート中...";

        try
        {
            var repoPath = RepositoryPath;
            var commitHash = TargetCommit.Hash;
            var files = DiffFiles.ToList();
            var outputPath = dialog.FolderName;

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

    private void UpdateCompareCommitters()
    {
        if (BaseCommit == null || TargetCommit == null)
        {
            CompareCommitters = [];
            CompareCommitterLabel = "Committer(All)";
            return;
        }

        try
        {
            var committers = _gitService.GetCommittersBetween(RepositoryPath, BaseCommit.Hash, TargetCommit.Hash);
            var items = committers.Select(name => new SelectableCommitter { Name = name }).ToList();
            foreach (var item in items)
            {
                item.PropertyChanged += (_, _) => UpdateCompareCommitterLabel();
            }
            CompareCommitters = new ObservableCollection<SelectableCommitter>(items);
            UpdateCompareCommitterLabel();
        }
        catch
        {
            CompareCommitters = [];
            CompareCommitterLabel = "Committer(All)";
        }
    }

    private void UpdateCompareCommitterLabel()
    {
        var total = CompareCommitters.Count;
        var selected = CompareCommitters.Count(c => c.IsSelected);

        CompareCommitterLabel = selected == 0 || selected == total
            ? "Committer(All)"
            : $"Committer({selected}/{total})";
    }

    [RelayCommand]
    private void SelectAllCompareCommitters()
    {
        foreach (var c in CompareCommitters)
            c.IsSelected = true;
    }

    [RelayCommand]
    private void ClearAllCompareCommitters()
    {
        foreach (var c in CompareCommitters)
            c.IsSelected = false;
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
