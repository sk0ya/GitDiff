using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitDiff.Models;
using GitDiff.Services;

namespace GitDiff.ViewModels;

public partial class DiffViewerViewModel : ObservableObject
{
    private readonly IGitService _gitService;
    private readonly string _repoPath;
    private readonly string _baseHash;
    private readonly string _targetHash;

    private string _currentOldHash;
    private string _currentNewHash;

    public DiffViewerViewModel(IGitService gitService, string repoPath, string baseHash, string targetHash, DiffFileInfo fileInfo)
    {
        _gitService = gitService;
        _repoPath = repoPath;
        _baseHash = baseHash;
        _targetHash = targetHash;
        _currentOldHash = baseHash;
        _currentNewHash = targetHash;
        FilePath = fileInfo.FilePath;
        FileStatus = fileInfo.StatusText;
        WindowTitle = $"Diff - {fileInfo.FilePath}";
    }

    [ObservableProperty]
    private string _windowTitle = string.Empty;

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private string _fileStatus = string.Empty;

    [ObservableProperty]
    private ObservableCollection<DiffLine> _diffLines = [];

    [ObservableProperty]
    private ObservableCollection<SideBySideLine> _sideBySideLines = [];

    [ObservableProperty]
    private IReadOnlyList<FileCommitInfo> _fileCommits = [];

    [ObservableProperty]
    private FileCommitInfo? _selectedCommit;

    [ObservableProperty]
    private bool _isOverallSelected = true;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _diffSummary = string.Empty;

    [ObservableProperty]
    private bool _isSideBySideMode = true;

    public bool HasMultipleCommits => FileCommits.Count > 1;

    partial void OnIsSideBySideModeChanged(bool value)
    {
        if (value && SideBySideLines.Count == 0 && DiffLines.Count > 0)
        {
            _ = BuildSideBySideAsync();
        }
    }

    [RelayCommand]
    private void ToggleViewMode()
    {
        IsSideBySideMode = !IsSideBySideMode;
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;

        try
        {
            var repoPath = _repoPath;
            var baseHash = _baseHash;
            var targetHash = _targetHash;
            var filePath = FilePath;

            var (commits, diffResult) = await Task.Run(() =>
            {
                var c = _gitService.GetFileCommitsBetween(repoPath, baseHash, targetHash, filePath);
                var d = _gitService.GetFileDiff(repoPath, baseHash, targetHash, filePath);
                return (c, d);
            });

            FileCommits = commits;
            OnPropertyChanged(nameof(HasMultipleCommits));
            _currentOldHash = baseHash;
            _currentNewHash = targetHash;
            UpdateDiffDisplay(diffResult);
            IsOverallSelected = true;
            SelectedCommit = null;
        }
        catch (Exception ex)
        {
            DiffSummary = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SelectOverall()
    {
        IsOverallSelected = true;
        SelectedCommit = null;
        IsLoading = true;

        try
        {
            _currentOldHash = _baseHash;
            _currentNewHash = _targetHash;
            var diffResult = await Task.Run(() =>
                _gitService.GetFileDiff(_repoPath, _baseHash, _targetHash, FilePath));
            UpdateDiffDisplay(diffResult);
        }
        catch (Exception ex)
        {
            DiffSummary = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SelectCommit(FileCommitInfo commit)
    {
        IsOverallSelected = false;
        SelectedCommit = commit;
        IsLoading = true;

        try
        {
            var parentHash = string.IsNullOrEmpty(commit.ParentHash) ? _baseHash : commit.ParentHash;
            _currentOldHash = parentHash;
            _currentNewHash = commit.Hash;
            var diffResult = await Task.Run(() =>
                _gitService.GetFileDiff(_repoPath, parentHash, commit.Hash, FilePath));
            UpdateDiffDisplay(diffResult);
        }
        catch (Exception ex)
        {
            DiffSummary = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void UpdateDiffDisplay(FileDiffResult result)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            DiffLines = new ObservableCollection<DiffLine>(result.Lines);
            SideBySideLines = [];
        });

        var added = result.Lines.Count(l => l.Type == DiffLineType.Added);
        var deleted = result.Lines.Count(l => l.Type == DiffLineType.Deleted);
        DiffSummary = $"+{added} -{deleted}";

        if (IsSideBySideMode)
        {
            _ = BuildSideBySideAsync();
        }
    }

    private async Task BuildSideBySideAsync()
    {
        IsLoading = true;
        try
        {
            var repoPath = _repoPath;
            var oldHash = _currentOldHash;
            var newHash = _currentNewHash;
            var filePath = FilePath;
            var diffLines = DiffLines.ToList();

            var lines = await Task.Run(() =>
            {
                var oldContent = _gitService.GetFileContent(repoPath, oldHash, filePath);
                var newContent = _gitService.GetFileContent(repoPath, newHash, filePath);

                var oldFileLines = oldContent != null
                    ? Encoding.UTF8.GetString(oldContent).Split('\n')
                    : [];
                var newFileLines = newContent != null
                    ? Encoding.UTF8.GetString(newContent).Split('\n')
                    : [];

                // Trim trailing \r from lines (CRLF handling)
                for (int i = 0; i < oldFileLines.Length; i++)
                    oldFileLines[i] = oldFileLines[i].TrimEnd('\r');
                for (int i = 0; i < newFileLines.Length; i++)
                    newFileLines[i] = newFileLines[i].TrimEnd('\r');

                return BuildSideBySideFromDiff(oldFileLines, newFileLines, diffLines);
            });

            Application.Current.Dispatcher.Invoke(() =>
            {
                SideBySideLines = new ObservableCollection<SideBySideLine>(lines);
            });
        }
        catch (Exception ex)
        {
            DiffSummary = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string GetLine(string[] lines, int oneBasedIndex)
    {
        return oneBasedIndex >= 1 && oneBasedIndex <= lines.Length ? lines[oneBasedIndex - 1] : string.Empty;
    }

    private static List<SideBySideLine> BuildSideBySideFromDiff(
        string[] oldFileLines, string[] newFileLines, IReadOnlyList<DiffLine> diffLines)
    {
        var result = new List<SideBySideLine>();
        int oldPos = 1;
        int newPos = 1;
        var hunkRegex = new Regex(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@");

        int i = 0;
        while (i < diffLines.Count)
        {
            var dl = diffLines[i];

            if (dl.Type == DiffLineType.Hunk)
            {
                var match = hunkRegex.Match(dl.Content);
                if (match.Success)
                {
                    int hunkOldStart = int.Parse(match.Groups[1].Value);
                    int hunkNewStart = int.Parse(match.Groups[2].Value);

                    // Fill context lines before this hunk
                    while (oldPos < hunkOldStart && newPos < hunkNewStart)
                    {
                        result.Add(new SideBySideLine
                        {
                            LeftLineNumber = oldPos,
                            LeftContent = GetLine(oldFileLines, oldPos),
                            RightLineNumber = newPos,
                            RightContent = GetLine(newFileLines, newPos),
                        });
                        oldPos++;
                        newPos++;
                    }
                }
                i++;
                continue;
            }

            if (dl.Type == DiffLineType.Context)
            {
                result.Add(new SideBySideLine
                {
                    LeftLineNumber = dl.OldLineNumber,
                    LeftContent = dl.Content,
                    RightLineNumber = dl.NewLineNumber,
                    RightContent = dl.Content,
                });
                oldPos = (dl.OldLineNumber ?? oldPos) + 1;
                newPos = (dl.NewLineNumber ?? newPos) + 1;
                i++;
            }
            else
            {
                // Collect consecutive deleted then added lines
                var deletedLines = new List<DiffLine>();
                var addedLines = new List<DiffLine>();

                while (i < diffLines.Count && diffLines[i].Type == DiffLineType.Deleted)
                {
                    deletedLines.Add(diffLines[i]);
                    i++;
                }
                while (i < diffLines.Count && diffLines[i].Type == DiffLineType.Added)
                {
                    addedLines.Add(diffLines[i]);
                    i++;
                }

                int maxCount = Math.Max(deletedLines.Count, addedLines.Count);
                for (int j = 0; j < maxCount; j++)
                {
                    result.Add(new SideBySideLine
                    {
                        LeftLineNumber = j < deletedLines.Count ? deletedLines[j].OldLineNumber : null,
                        LeftContent = j < deletedLines.Count ? deletedLines[j].Content : string.Empty,
                        LeftType = j < deletedLines.Count ? DiffLineType.Deleted : DiffLineType.Context,
                        RightLineNumber = j < addedLines.Count ? addedLines[j].NewLineNumber : null,
                        RightContent = j < addedLines.Count ? addedLines[j].Content : string.Empty,
                        RightType = j < addedLines.Count ? DiffLineType.Added : DiffLineType.Context,
                    });
                }

                if (deletedLines.Count > 0)
                    oldPos = (deletedLines[^1].OldLineNumber ?? oldPos) + 1;
                if (addedLines.Count > 0)
                    newPos = (addedLines[^1].NewLineNumber ?? newPos) + 1;
            }
        }

        // Emit remaining context lines after last hunk
        while (oldPos <= oldFileLines.Length || newPos <= newFileLines.Length)
        {
            result.Add(new SideBySideLine
            {
                LeftLineNumber = oldPos <= oldFileLines.Length ? oldPos : null,
                LeftContent = oldPos <= oldFileLines.Length ? oldFileLines[oldPos - 1] : string.Empty,
                RightLineNumber = newPos <= newFileLines.Length ? newPos : null,
                RightContent = newPos <= newFileLines.Length ? newFileLines[newPos - 1] : string.Empty,
            });
            if (oldPos <= oldFileLines.Length) oldPos++;
            if (newPos <= newFileLines.Length) newPos++;
        }

        return result;
    }
}
