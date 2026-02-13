using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitDiff.Models;
using GitDiff.Services;
using GitDiff.Views;
using Microsoft.Win32;

namespace GitDiff.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IGitService _gitService;
    private readonly IFileExportService _fileExportService;
    private readonly IC0CaseService _c0CaseService;
    private readonly ISettingsService _settingsService;

    private List<CommitInfo> _allCommits = [];
    private List<DiffFileInfo> _allDiffFiles = [];
    private Dictionary<string, FolderTreeNode> _folderNodeLookup = new();
    private bool _suppressFolderFilter;
    private bool _multiCommitMode;
    private List<string> _multiCommitHashes = [];

    private readonly IAzureDevOpsService _azureDevOpsService;

    public MainViewModel()
        : this(new GitService(), new FileExportService(new GitService()), new C0CaseService(), new SettingsService(), new AzureDevOpsService())
    {
    }

    public MainViewModel(IGitService gitService, IFileExportService fileExportService,
        IC0CaseService c0CaseService, ISettingsService settingsService, IAzureDevOpsService azureDevOpsService)
    {
        _gitService = gitService;
        _fileExportService = fileExportService;
        _c0CaseService = c0CaseService;
        _settingsService = settingsService;
        _azureDevOpsService = azureDevOpsService;
        InitStatusFilters();
    }

    public IGitService GitService => _gitService;

    [ObservableProperty]
    private string _repositoryPath = string.Empty;

    [ObservableProperty]
    private ObservableCollection<string> _recentRepositories = [];

    [ObservableProperty]
    private ObservableCollection<CommitInfo> _commits = [];

    [ObservableProperty]
    private ObservableCollection<SelectableCommitter> _filterCommitters = [];

    [ObservableProperty]
    private ObservableCollection<SelectableCommitter> _filteredCommitters = [];

    [ObservableProperty]
    private string _committerFilterText = string.Empty;

    [ObservableProperty]
    private string _committerFilterLabel = "Committer(All)";

    [ObservableProperty]
    private DateTime? _dateFrom;

    [ObservableProperty]
    private DateTime? _dateTo;

    [ObservableProperty]
    private string _dateFilterLabel = "Date(All)";

    [ObservableProperty]
    private CommitInfo? _baseCommit;

    [ObservableProperty]
    private CommitInfo? _targetCommit;

    [ObservableProperty]
    private ObservableCollection<SelectableCommitter> _compareCommitters = [];

    [ObservableProperty]
    private string _compareCommitterLabel = "Committer(All)";

    [ObservableProperty]
    private ObservableCollection<FolderTreeNode> _folderTree = [];

    [ObservableProperty]
    private ObservableCollection<DiffFileInfo> _diffFiles = [];

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _excludeMergeCommits = true;

    [ObservableProperty]
    private int _diffFileCount;

    [ObservableProperty]
    private string _multiCommitLabel = string.Empty;

    [ObservableProperty]
    private string _messageFilterText = string.Empty;

    [ObservableProperty]
    private string _filePathFilterText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<SelectableCommitter> _statusFilters = [];

    [ObservableProperty]
    private string _statusFilterLabel = "Status(All)";

    public bool IsMultiCommitMode => _multiCommitMode;
    public bool HasBaseCommit => BaseCommit != null;
    public bool HasTargetCommit => TargetCommit != null;

    partial void OnCommitterFilterTextChanged(string value) => UpdateFilteredCommitters();

    partial void OnMessageFilterTextChanged(string value) => ApplyFilters();

    partial void OnFilePathFilterTextChanged(string value) => ApplyDiffFileFilters();

    partial void OnDateFromChanged(DateTime? value)
    {
        UpdateDateFilterLabel();
        ApplyFilters();
    }

    partial void OnDateToChanged(DateTime? value)
    {
        UpdateDateFilterLabel();
        ApplyFilters();
    }

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
                    InitFilterCommitters(committers);
                    BaseCommit = null;
                    TargetCommit = null;
                    _allDiffFiles = [];
                    FolderTree = [];
                    _folderNodeLookup = new();
                    DiffFiles = [];
                    DiffFileCount = 0;
                    ApplyFilters();
                });
            });

            _settingsService.AddRecentRepository(RepositoryPath);
            RefreshRecentRepositories();
            UpdateJumpList();
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

            var excludeMerge = ExcludeMergeCommits;

            var files = await Task.Run(() =>
                _gitService.GetDiffFiles(repoPath, baseHash, targetHash, excludeMerge));

            _multiCommitMode = false;
            OnPropertyChanged(nameof(IsMultiCommitMode));
            MultiCommitLabel = string.Empty;
            _allDiffFiles = files.ToList();
            BuildFolderTree(_allDiffFiles);
            ApplyDiffFileFilters();

            StatusMessage = $"差分ファイル: {_allDiffFiles.Count} 件";
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
    private async Task CompareMultiCommits()
    {
        if (string.IsNullOrWhiteSpace(RepositoryPath))
        {
            StatusMessage = "リポジトリを先に選択してください。";
            return;
        }

        var dialog = new CommitHashInputDialog { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;

        var hashes = dialog.CommitHashes;
        IsLoading = true;
        StatusMessage = "差分を抽出中...";

        try
        {
            var repoPath = RepositoryPath;
            var files = await Task.Run(() => _gitService.GetDiffFilesForCommits(repoPath, hashes));

            _multiCommitMode = true;
            _multiCommitHashes = hashes.ToList();
            OnPropertyChanged(nameof(IsMultiCommitMode));
            BaseCommit = null;
            TargetCommit = null;
            MultiCommitLabel = "Multi: " + string.Join(", ", hashes);

            InitMultiCommitCommitters(repoPath, hashes);

            _allDiffFiles = files.ToList();
            BuildFolderTree(_allDiffFiles);
            ApplyDiffFileFilters();

            StatusMessage = $"差分ファイル: {_allDiffFiles.Count} 件 ({hashes.Count} commits)";
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
    private async Task CompareAzureDevOpsPR()
    {
        if (string.IsNullOrWhiteSpace(RepositoryPath))
        {
            StatusMessage = "リポジトリを先に選択してください。";
            return;
        }

        var dialog = new AzureDevOpsDialog(_azureDevOpsService, _settingsService)
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() != true) return;

        var hashes = dialog.ResultCommitHashes;
        if (hashes.Count == 0)
        {
            StatusMessage = "コミットが見つかりませんでした。";
            return;
        }

        IsLoading = true;
        StatusMessage = "差分を抽出中...";

        try
        {
            var repoPath = RepositoryPath;
            var files = await Task.Run(() => _gitService.GetDiffFilesForCommits(repoPath, hashes));

            _multiCommitMode = true;
            _multiCommitHashes = hashes.ToList();
            OnPropertyChanged(nameof(IsMultiCommitMode));
            BaseCommit = null;
            TargetCommit = null;
            MultiCommitLabel = dialog.ResultLabel;

            InitMultiCommitCommitters(repoPath, hashes);

            _allDiffFiles = files.ToList();
            BuildFolderTree(_allDiffFiles);
            ApplyDiffFileFilters();

            StatusMessage = $"差分ファイル: {_allDiffFiles.Count} 件 ({hashes.Count} commits)";
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

        if (!_multiCommitMode && (BaseCommit == null || TargetCommit == null)) return;

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
            var baseHash = _multiCommitMode ? string.Empty : BaseCommit!.Hash;
            var targetHash = _multiCommitMode ? string.Empty : TargetCommit!.Hash;
            var files = DiffFiles.ToList();
            var outputPath = dialog.FolderName;

            var count = await Task.Run(() =>
                _fileExportService.ExportDiffFiles(repoPath, baseHash, targetHash, files, outputPath));

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

    [RelayCommand]
    private async Task GenerateC0Cases()
    {
        if (DiffFiles.Count == 0)
        {
            StatusMessage = "差分ファイルがありません。";
            return;
        }

        if (!_multiCommitMode && (BaseCommit == null || TargetCommit == null))
        {
            StatusMessage = "差分ファイルがありません。";
            return;
        }

        IsLoading = true;
        StatusMessage = "C0ケースを生成中...";

        try
        {
            var csFiles = DiffFiles
                .Where(f => f.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                            && f.Status != ChangeStatus.Deleted)
                .ToList();

            if (csFiles.Count == 0)
            {
                StatusMessage = "対象の.csファイルがありません。";
                return;
            }

            var repoPath = RepositoryPath;
            var baseHash = _multiCommitMode ? null : BaseCommit!.Hash;
            var targetHash = _multiCommitMode ? null : TargetCommit!.Hash;

            var allCases = await Task.Run(() =>
            {
                var cases = new List<C0TestCase>();

                foreach (var file in csFiles)
                {
                    var fileBase = file.BaseCommitHash ?? baseHash!;
                    var fileTarget = file.SourceCommitHash ?? targetHash!;

                    var changedLines = _gitService.GetChangedLineNumbers(repoPath, fileBase, fileTarget, file.FilePath);
                    if (changedLines.Count == 0) continue;

                    var contentBytes = _gitService.GetFileContent(repoPath, fileTarget, file.FilePath);
                    if (contentBytes == null) continue;

                    var content = Encoding.UTF8.GetString(contentBytes);
                    var fileCases = _c0CaseService.GenerateC0Cases(content, changedLines);
                    cases.AddRange(fileCases);
                }

                return cases;
            });

            if (allCases.Count == 0)
            {
                StatusMessage = "分岐条件が見つかりませんでした。";
                return;
            }

            // Build TSV (同一クラス名・メソッド名は省略)
            var sb = new StringBuilder();
            sb.AppendLine("クラス名\tメソッド名\t分岐条件");
            string prevClass = "", prevMethod = "";
            foreach (var c in allCases)
            {
                var showClass = c.ClassName != prevClass ? c.ClassName : "";
                var showMethod = (c.ClassName != prevClass || c.MethodName != prevMethod) ? c.MethodName : "";
                sb.AppendLine($"{showClass}\t{showMethod}\t{c.BranchCondition}");
                prevClass = c.ClassName;
                prevMethod = c.MethodName;
            }

            Clipboard.SetText(sb.ToString());
            StatusMessage = $"C0ケースをクリップボードにコピーしました。({allCases.Count} 件)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"C0ケース生成エラー: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private const string RootFolderKey = "";

    private void BuildFolderTree(List<DiffFileInfo> files)
    {
        var lookup = new Dictionary<string, FolderTreeNode>();
        var topLevel = new List<FolderTreeNode>();
        var hasRootFiles = false;

        foreach (var file in files)
        {
            var parts = file.FilePath.Split('/');
            if (parts.Length <= 1)
            {
                hasRootFiles = true;
                continue;
            }

            var folderParts = parts[..^1];
            var currentPath = "";
            FolderTreeNode? parentNode = null;

            for (var i = 0; i < folderParts.Length; i++)
            {
                currentPath = i == 0 ? folderParts[i] : currentPath + "/" + folderParts[i];

                if (!lookup.TryGetValue(currentPath, out var node))
                {
                    node = new FolderTreeNode
                    {
                        Name = folderParts[i],
                        FullPath = currentPath,
                        Parent = parentNode
                    };
                    lookup[currentPath] = node;

                    if (parentNode != null)
                        parentNode.Children.Add(node);
                    else
                        topLevel.Add(node);
                }

                parentNode = node;
            }
        }

        if (hasRootFiles)
        {
            var rootNode = new FolderTreeNode { Name = "(root)", FullPath = RootFolderKey };
            lookup[RootFolderKey] = rootNode;
            topLevel.Insert(0, rootNode);
        }

        CompressFolderTree(topLevel);
        _folderNodeLookup = BuildFolderNodeLookup(topLevel);
        SubscribeToTreeNodes(topLevel);
        FolderTree = new ObservableCollection<FolderTreeNode>(topLevel);
    }

    private static void CompressFolderTree(IList<FolderTreeNode> nodes)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];

            while (node.Children.Count == 1)
            {
                var child = node.Children[0];
                node.Name = node.Name + "/" + child.Name;
                node.FullPath = child.FullPath;
                node.Children.Clear();
                foreach (var grandchild in child.Children)
                {
                    grandchild.Parent = node;
                    node.Children.Add(grandchild);
                }
            }

            CompressFolderTree(node.Children);
        }
    }

    private static Dictionary<string, FolderTreeNode> BuildFolderNodeLookup(
        IEnumerable<FolderTreeNode> nodes, string parentPath = "")
    {
        var lookup = new Dictionary<string, FolderTreeNode>();

        foreach (var node in nodes)
        {
            // Map all intermediate paths within this compressed node to it
            var relative = parentPath == ""
                ? node.FullPath
                : node.FullPath[(parentPath.Length + 1)..];
            var parts = relative.Split('/');
            var current = parentPath;

            foreach (var part in parts)
            {
                current = current == "" ? part : current + "/" + part;
                lookup[current] = node;
            }

            foreach (var kv in BuildFolderNodeLookup(node.Children, node.FullPath))
                lookup[kv.Key] = kv.Value;
        }

        return lookup;
    }

    private void SubscribeToTreeNodes(IEnumerable<FolderTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            node.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(FolderTreeNode.IsSelected) && !_suppressFolderFilter)
                    ApplyDiffFileFilters();
            };
            SubscribeToTreeNodes(node.Children);
        }
    }

    private void ApplyDiffFileFilters()
    {
        var filtered = _allDiffFiles.Where(f =>
        {
            var lastSlash = f.FilePath.LastIndexOf('/');
            var folderPath = lastSlash < 0 ? RootFolderKey : f.FilePath[..lastSlash];
            return IsFolderSelected(folderPath);
        });

        if (!string.IsNullOrWhiteSpace(FilePathFilterText))
        {
            var text = FilePathFilterText;
            filtered = filtered.Where(f => f.FilePath.Contains(text, StringComparison.OrdinalIgnoreCase));
        }

        var statusTotal = StatusFilters.Count;
        var statusSelected = StatusFilters.Count(s => s.IsSelected);
        if (statusSelected > 0 && statusSelected < statusTotal)
        {
            var selectedNames = new HashSet<string>(
                StatusFilters.Where(s => s.IsSelected).Select(s => s.Name));
            filtered = filtered.Where(f => selectedNames.Contains(f.StatusText));
        }

        var committerTotal = CompareCommitters.Count;
        var committerSelected = CompareCommitters.Count(c => c.IsSelected);
        if (committerSelected > 0 && committerSelected < committerTotal)
        {
            var selectedCommitters = new HashSet<string>(
                CompareCommitters.Where(c => c.IsSelected).Select(c => c.Name),
                StringComparer.OrdinalIgnoreCase);
            filtered = filtered.Where(f => f.AuthorName != null && selectedCommitters.Contains(f.AuthorName));
        }

        var result = filtered.ToList();
        DiffFiles = new ObservableCollection<DiffFileInfo>(result);
        DiffFileCount = result.Count;
    }

    private bool IsFolderSelected(string folderPath)
    {
        if (_folderNodeLookup.TryGetValue(folderPath, out var node))
            return node.IsSelected != false;
        return true;
    }

    [RelayCommand]
    private void SelectAllFolders()
    {
        _suppressFolderFilter = true;
        foreach (var node in FolderTree)
            node.IsSelected = true;
        _suppressFolderFilter = false;
        ApplyDiffFileFilters();
    }

    [RelayCommand]
    private void ClearAllFolders()
    {
        _suppressFolderFilter = true;
        foreach (var node in FolderTree)
            node.IsSelected = false;
        _suppressFolderFilter = false;
        ApplyDiffFileFilters();
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

        ApplyDiffFileFilters();
    }

    private void InitMultiCommitCommitters(string repoPath, IReadOnlyList<string> hashes)
    {
        try
        {
            var committers = _gitService.GetCommittersForCommits(repoPath, hashes);
            var items = committers.Select(name => new SelectableCommitter { Name = name }).ToList();
            foreach (var item in items)
            {
                item.PropertyChanged += (_, _) => UpdateCompareCommitterLabel();
            }
            CompareCommitters = new ObservableCollection<SelectableCommitter>(items);
            CompareCommitterLabel = "Committer(All)";
        }
        catch
        {
            CompareCommitters = [];
            CompareCommitterLabel = "Committer(All)";
        }
    }

    private void InitStatusFilters()
    {
        var names = new[] { "Added", "Modified", "Deleted", "Renamed", "Copied" };
        var items = names.Select(n => new SelectableCommitter { Name = n }).ToList();
        foreach (var item in items)
        {
            item.PropertyChanged += (_, _) =>
            {
                UpdateStatusFilterLabel();
                ApplyDiffFileFilters();
            };
        }
        StatusFilters = new ObservableCollection<SelectableCommitter>(items);
    }

    private void UpdateStatusFilterLabel()
    {
        var total = StatusFilters.Count;
        var selected = StatusFilters.Count(s => s.IsSelected);
        StatusFilterLabel = selected == 0 || selected == total
            ? "Status(All)"
            : $"Status({selected}/{total})";
    }

    [RelayCommand]
    private void SelectAllStatusFilters()
    {
        foreach (var s in StatusFilters)
            s.IsSelected = true;
    }

    [RelayCommand]
    private void ClearAllStatusFilters()
    {
        foreach (var s in StatusFilters)
            s.IsSelected = false;
    }

    [RelayCommand]
    private void ClearAllCompareCommitters()
    {
        foreach (var c in CompareCommitters)
            c.IsSelected = false;
    }

    private void InitFilterCommitters(IReadOnlyList<string> committers)
    {
        var items = committers.Select(name => new SelectableCommitter { Name = name }).ToList();
        foreach (var item in items)
        {
            item.PropertyChanged += (_, _) =>
            {
                UpdateCommitterFilterLabel();
                ApplyFilters();
            };
        }
        FilterCommitters = new ObservableCollection<SelectableCommitter>(items);
        CommitterFilterText = string.Empty;
        UpdateFilteredCommitters();
        UpdateCommitterFilterLabel();
    }

    private void UpdateFilteredCommitters()
    {
        var text = CommitterFilterText ?? string.Empty;
        var items = string.IsNullOrWhiteSpace(text)
            ? FilterCommitters
            : new ObservableCollection<SelectableCommitter>(
                FilterCommitters.Where(c => c.Name.Contains(text, StringComparison.OrdinalIgnoreCase)));
        FilteredCommitters = items;
    }

    private void UpdateCommitterFilterLabel()
    {
        var total = FilterCommitters.Count;
        var selected = FilterCommitters.Count(c => c.IsSelected);

        CommitterFilterLabel = selected == 0 || selected == total
            ? "Committer(All)"
            : $"Committer({selected}/{total})";
    }

    [RelayCommand]
    private void ClearDateFilter()
    {
        DateFrom = null;
        DateTo = null;
    }

    private void UpdateDateFilterLabel()
    {
        if (DateFrom == null && DateTo == null)
        {
            DateFilterLabel = "Date(All)";
        }
        else if (DateFrom != null && DateTo != null)
        {
            DateFilterLabel = $"Date({DateFrom:yyyy/MM/dd}～{DateTo:yyyy/MM/dd})";
        }
        else if (DateFrom != null)
        {
            DateFilterLabel = $"Date({DateFrom:yyyy/MM/dd}～)";
        }
        else
        {
            DateFilterLabel = $"Date(～{DateTo:yyyy/MM/dd})";
        }
    }

    [RelayCommand]
    private void ClearAllCommitters()
    {
        foreach (var c in FilterCommitters)
            c.IsSelected = false;
    }

    public async Task InitializeAsync(string? repositoryPath)
    {
        RefreshRecentRepositories();

        if (!string.IsNullOrWhiteSpace(repositoryPath))
        {
            RepositoryPath = repositoryPath;
            await LoadRepository();
            return;
        }

        var settings = _settingsService.Load();
        if (!string.IsNullOrWhiteSpace(settings.LastRepositoryPath))
        {
            RepositoryPath = settings.LastRepositoryPath;
            await LoadRepository();
        }

        UpdateJumpList();
    }

    private void RefreshRecentRepositories()
    {
        var settings = _settingsService.Load();
        RecentRepositories = new ObservableCollection<string>(settings.RecentRepositories);
    }

    private void UpdateJumpList()
    {
        try
        {
            var settings = _settingsService.Load();
            var jumpList = new JumpList();

            foreach (var repoPath in settings.RecentRepositories)
            {
                jumpList.JumpItems.Add(new JumpTask
                {
                    Title = repoPath,
                    Arguments = $"\"{repoPath}\"",
                    Description = $"Open repository: {repoPath}",
                    ApplicationPath = Environment.ProcessPath,
                });
            }

            jumpList.Apply();
        }
        catch
        {
            // JumpList update is non-critical
        }
    }

    private void ApplyFilters()
    {
        var filtered = _allCommits.AsEnumerable();

        var total = FilterCommitters.Count;
        var selected = FilterCommitters.Count(c => c.IsSelected);
        if (selected > 0 && selected < total)
        {
            var selectedNames = new HashSet<string>(
                FilterCommitters.Where(c => c.IsSelected).Select(c => c.Name));
            filtered = filtered.Where(c => selectedNames.Contains(c.Author));
        }

        if (DateFrom is { } from)
        {
            filtered = filtered.Where(c => c.Date.LocalDateTime.Date >= from.Date);
        }

        if (DateTo is { } to)
        {
            filtered = filtered.Where(c => c.Date.LocalDateTime.Date <= to.Date);
        }

        if (!string.IsNullOrWhiteSpace(MessageFilterText))
        {
            var text = MessageFilterText;
            filtered = filtered.Where(c => c.Message.Contains(text, StringComparison.OrdinalIgnoreCase));
        }

        Commits = new ObservableCollection<CommitInfo>(filtered);
        StatusMessage = $"表示中のコミット: {Commits.Count} / {_allCommits.Count}";
    }
}
