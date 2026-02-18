using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GitDiff.Models;
using GitDiff.Services;
using MaterialDesignThemes.Wpf;
using Microsoft.Win32;

namespace GitDiff.Views;

public partial class AzureDevOpsBatchExportWindow : Window
{
    private readonly ISettingsService _settingsService;
    private readonly IGitService _gitService;
    private readonly IFileExportService _fileExportService;
    private readonly IAzureDevOpsService _azureService;

    private List<AzureDevOpsWorkItem> _workItems = [];
    // repoPath → (repoDisplayName, prId → PR)
    private Dictionary<string, (string RepoName, Dictionary<int, AzureDevOpsPullRequestInfo> PRs)> _repoPullRequests = new();

    public AzureDevOpsBatchExportWindow(
        ISettingsService settingsService,
        IGitService gitService,
        IFileExportService fileExportService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _gitService = gitService;
        _fileExportService = fileExportService;
        _azureService = new AzureDevOpsService();
        LoadSettings();
    }

    private void LoadSettings()
    {
        var settings = _settingsService.Load();
        OrgInput.Text = settings.AzureDevOpsOrganization ?? string.Empty;
        ProjectInput.Text = settings.AzureDevOpsProject ?? string.Empty;
        CredentialTargetInput.Text = settings.AzureDevOpsCredentialTarget ?? string.Empty;

        SettingsExpander.IsExpanded = string.IsNullOrWhiteSpace(OrgInput.Text)
            || string.IsNullOrWhiteSpace(ProjectInput.Text)
            || string.IsNullOrWhiteSpace(CredentialTargetInput.Text);
    }

    private void SaveSettings()
    {
        var settings = _settingsService.Load();
        settings.AzureDevOpsOrganization = OrgInput.Text.Trim();
        settings.AzureDevOpsProject = ProjectInput.Text.Trim();
        settings.AzureDevOpsCredentialTarget = CredentialTargetInput.Text.Trim();
        _settingsService.Save(settings);
    }

    private string? GetPat()
    {
        var target = CredentialTargetInput.Text.Trim();
        if (string.IsNullOrEmpty(target))
        {
            SetStatus("資格情報のターゲット名を入力してください。");
            return null;
        }

        var pat = WindowsCredentialManager.GetPassword(target);
        if (string.IsNullOrEmpty(pat))
        {
            SetStatus($"資格情報が見つかりません: {target}");
            return null;
        }
        return pat;
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        var org = OrgInput.Text.Trim();
        var project = ProjectInput.Text.Trim();

        if (string.IsNullOrEmpty(org) || string.IsNullOrEmpty(project))
        {
            SetStatus("Organization と Project を入力してください。");
            return;
        }

        var pat = GetPat();
        if (pat == null) return;

        SetLoading(true);
        SetStatus("接続テスト中...");
        try
        {
            await _azureService.TestConnectionAsync(org, project, pat);
            SetStatus("接続成功!");
        }
        catch (Exception ex)
        {
            SetStatus($"接続失敗: {ex.Message}");
        }
        finally
        {
            SetLoading(false);
        }
    }

    private void AddRepository_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Gitリポジトリフォルダを選択"
        };
        if (dialog.ShowDialog() == true)
        {
            var path = dialog.FolderName;
            if (!RepositoriesListBox.Items.Contains(path))
                RepositoriesListBox.Items.Add(path);
        }
    }

    private void RemoveRepository_Click(object sender, RoutedEventArgs e)
    {
        if (RepositoriesListBox.SelectedItem != null)
            RepositoriesListBox.Items.Remove(RepositoriesListBox.SelectedItem);
    }

    private List<string> GetRepositoryPaths() =>
        RepositoriesListBox.Items.Cast<string>().ToList();

    private async void FetchButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        var org = OrgInput.Text.Trim();
        var project = ProjectInput.Text.Trim();

        if (string.IsNullOrEmpty(org) || string.IsNullOrEmpty(project))
        {
            SetStatus("Settings の Organization, Project を入力してください。");
            SettingsExpander.IsExpanded = true;
            return;
        }

        var pat = GetPat();
        if (pat == null) return;

        var repoPaths = GetRepositoryPaths();
        if (repoPaths.Count == 0)
        {
            SetStatus("対象Gitリポジトリを1つ以上追加してください。");
            return;
        }

        var text = WorkItemIdInput.Text ?? string.Empty;
        var idStrings = text.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries);
        var ids = new List<int>();
        foreach (var s in idStrings)
        {
            var cleaned = s.TrimStart('#');
            if (int.TryParse(cleaned, out var id))
                ids.Add(id);
        }

        if (ids.Count == 0)
        {
            SetStatus("Work Item IDを入力してください。");
            return;
        }

        SetLoading(true);
        SetStatus("Work Item を取得中...");
        ExportButton.IsEnabled = false;
        ResultsTree.Items.Clear();
        _repoPullRequests.Clear();

        try
        {
            // 1. Fetch work items + children
            _workItems = await _azureService.GetWorkItemsAsync(org, project, pat, ids);
            SetStatus("子Work Itemを取得中...");
            await FetchChildrenRecursive(_workItems, org, project, pat);

            var allPrIds = CollectAllPrIds(_workItems);
            if (allPrIds.Count == 0)
            {
                SetStatus("紐づくPull Requestが見つかりませんでした。");
                BuildResultsTree();
                return;
            }

            // 2. For each repository, fetch PRs+commits
            for (int i = 0; i < repoPaths.Count; i++)
            {
                var repoPath = repoPaths[i];
                var remoteUrl = _gitService.GetRemoteUrl(repoPath);
                var (parsedProject, parsedRepoName) = ParseAzureDevOpsRemoteUrl(remoteUrl);
                var prProject = parsedProject ?? project;
                var prRepoName = parsedRepoName;

                if (string.IsNullOrEmpty(prRepoName))
                {
                    SetStatus($"[{i + 1}/{repoPaths.Count}] リモートURLからリポジトリ名を取得できません: {repoPath}");
                    continue;
                }

                SetStatus($"[{i + 1}/{repoPaths.Count}] PR取得中: {prRepoName} ({allPrIds.Count} PR)");
                var prs = await _azureService.GetPullRequestsWithCommitsAsync(org, prProject, prRepoName, pat, allPrIds);
                var prDict = prs
                    .Where(p => p.CommitHashes.Count > 0)
                    .ToDictionary(p => p.Id);

                _repoPullRequests[repoPath] = (prRepoName, prDict);
            }

            // 3. Build results tree
            BuildResultsTree();

            var totalCommits = _repoPullRequests.Values
                .SelectMany(r => r.PRs.Values)
                .SelectMany(p => p.CommitHashes)
                .Distinct()
                .Count();

            var totalWiCount = CountAllWorkItems(_workItems);
            CommitCountRun.Text = $" (コミット数: {totalCommits})";
            ExportButton.IsEnabled = totalCommits > 0;
            SetStatus($"取得完了: {totalWiCount} Work Items, {allPrIds.Count} PRs, {totalCommits} Commits");
        }
        catch (Exception ex)
        {
            SetStatus($"エラー: {ex.Message}");
        }
        finally
        {
            SetLoading(false);
        }
    }

    private async Task FetchChildrenRecursive(List<AzureDevOpsWorkItem> workItems,
        string org, string project, string pat, HashSet<int>? fetched = null)
    {
        fetched ??= new HashSet<int>(workItems.Select(wi => wi.Id));

        var childIdsToFetch = workItems
            .SelectMany(wi => wi.ChildWorkItemIds)
            .Where(id => fetched.Add(id))
            .Distinct()
            .ToList();

        if (childIdsToFetch.Count == 0) return;

        var children = await _azureService.GetWorkItemsAsync(org, project, pat, childIdsToFetch);

        var childLookup = children.ToDictionary(c => c.Id);
        foreach (var wi in workItems)
        {
            foreach (var childId in wi.ChildWorkItemIds)
            {
                if (childLookup.TryGetValue(childId, out var child))
                    wi.Children.Add(child);
            }
        }

        if (children.Any(c => c.ChildWorkItemIds.Count > 0))
            await FetchChildrenRecursive(children, org, project, pat, fetched);
    }

    private static List<int> CollectAllPrIds(List<AzureDevOpsWorkItem> workItems)
    {
        var result = new HashSet<int>();
        CollectPrIdsRecursive(workItems, result);
        return result.ToList();
    }

    private static void CollectPrIdsRecursive(List<AzureDevOpsWorkItem> workItems, HashSet<int> result)
    {
        foreach (var wi in workItems)
        {
            foreach (var prId in wi.LinkedPullRequestIds)
                result.Add(prId);
            CollectPrIdsRecursive(wi.Children, result);
        }
    }

    private static int CountAllWorkItems(List<AzureDevOpsWorkItem> workItems)
    {
        var count = workItems.Count;
        foreach (var wi in workItems)
            count += CountAllWorkItems(wi.Children);
        return count;
    }

    private void BuildResultsTree()
    {
        ResultsTree.Items.Clear();
        foreach (var wi in _workItems)
            ResultsTree.Items.Add(BuildWorkItemNode(wi));
    }

    private TreeViewItem BuildWorkItemNode(AzureDevOpsWorkItem wi)
    {
        var wiNode = new TreeViewItem { IsExpanded = true, Margin = new Thickness(0, 2, 0, 2) };

        var wiPanel = new StackPanel { Orientation = Orientation.Horizontal };
        var wiCheckBox = new CheckBox
        {
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 3, 0),
            Tag = wi
        };
        wiCheckBox.Checked += (_, _) => { wi.IsSelected = true; UpdateCommitCount(); };
        wiCheckBox.Unchecked += (_, _) => { wi.IsSelected = false; UpdateCommitCount(); };
        wiPanel.Children.Add(wiCheckBox);

        wiPanel.Children.Add(new PackIcon
        {
            Kind = GetWorkItemIcon(wi.WorkItemType),
            Width = 15, Height = 15,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = GetWorkItemBrush(wi.WorkItemType),
            Margin = new Thickness(0, 0, 4, 0)
        });

        wiPanel.Children.Add(new TextBlock
        {
            Text = $"#{wi.Id}",
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        });

        wiPanel.Children.Add(new TextBlock
        {
            Text = wi.WorkItemType,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = GetWorkItemBrush(wi.WorkItemType),
            Background = GetWorkItemBackgroundBrush(wi.WorkItemType),
            Padding = new Thickness(4, 1, 4, 1),
            Margin = new Thickness(0, 0, 5, 0)
        });

        wiPanel.Children.Add(new TextBlock
        {
            Text = wi.Title,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 380
        });

        wiNode.Header = wiPanel;

        // Add PR nodes: find this WI's PRs across all repos
        bool hasPrOrChild = false;
        foreach (var prId in wi.LinkedPullRequestIds)
        {
            foreach (var (repoPath, (repoName, prs)) in _repoPullRequests)
            {
                if (!prs.TryGetValue(prId, out var pr)) continue;

                hasPrOrChild = true;
                var prPanel = new StackPanel { Orientation = Orientation.Horizontal };

                prPanel.Children.Add(new PackIcon
                {
                    Kind = PackIconKind.SourcePull,
                    Width = 14, Height = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromRgb(130, 80, 223)),
                    Margin = new Thickness(0, 0, 4, 0)
                });

                prPanel.Children.Add(new TextBlock
                {
                    Text = $"!{pr.Id}",
                    FontWeight = FontWeights.Medium,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromRgb(130, 80, 223)),
                    Margin = new Thickness(0, 0, 5, 0),
                    FontSize = 11
                });

                prPanel.Children.Add(new TextBlock
                {
                    Text = pr.Title,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 270,
                    FontSize = 11
                });

                prPanel.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                    CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(5, 0, 5, 0),
                    Margin = new Thickness(5, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = $"{pr.CommitHashes.Count} commits",
                        FontSize = 9,
                        Foreground = Brushes.White
                    }
                });

                // Repo name badge
                prPanel.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(40, 0, 120, 212)),
                    CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(5, 0, 5, 0),
                    Margin = new Thickness(4, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = repoName,
                        FontSize = 9,
                        Foreground = new SolidColorBrush(Color.FromRgb(0, 120, 212))
                    }
                });

                var prNode = new TreeViewItem
                {
                    Header = prPanel,
                    IsExpanded = false,
                    Margin = new Thickness(0)
                };

                foreach (var hash in pr.CommitHashes)
                {
                    var commitPanel = new StackPanel { Orientation = Orientation.Horizontal };
                    commitPanel.Children.Add(new PackIcon
                    {
                        Kind = PackIconKind.SourceCommit,
                        Width = 13, Height = 13,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = Brushes.Gray,
                        Margin = new Thickness(0, 0, 3, 0)
                    });
                    commitPanel.Children.Add(new TextBlock
                    {
                        Text = hash.Length >= 7 ? hash[..7] : hash,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 10,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = Brushes.Gray
                    });
                    prNode.Items.Add(new TreeViewItem
                    {
                        Header = commitPanel,
                        Padding = new Thickness(0),
                        Margin = new Thickness(0),
                        MinHeight = 18
                    });
                }

                wiNode.Items.Add(prNode);
            }
        }

        // Recursively add child work items
        foreach (var child in wi.Children)
        {
            hasPrOrChild = true;
            wiNode.Items.Add(BuildWorkItemNode(child));
        }

        if (!hasPrOrChild)
        {
            wiNode.Items.Add(new TreeViewItem
            {
                Header = new TextBlock
                {
                    Text = "(PRなし、またはリポジトリに存在しないPR)",
                    FontStyle = FontStyles.Italic,
                    Foreground = Brushes.Gray,
                    FontSize = 11
                },
                IsEnabled = false,
                Margin = new Thickness(0),
                MinHeight = 18
            });
        }

        return wiNode;
    }

    private static HashSet<int> CollectSelectedPrIds(List<AzureDevOpsWorkItem> workItems)
    {
        var result = new HashSet<int>();
        CollectSelectedPrIdsRecursive(workItems, result);
        return result;
    }

    private static void CollectSelectedPrIdsRecursive(List<AzureDevOpsWorkItem> workItems, HashSet<int> result)
    {
        foreach (var wi in workItems)
        {
            if (wi.IsSelected)
            {
                foreach (var prId in wi.LinkedPullRequestIds)
                    result.Add(prId);
            }
            CollectSelectedPrIdsRecursive(wi.Children, result);
        }
    }

    private void UpdateCommitCount()
    {
        var selectedPrIds = CollectSelectedPrIds(_workItems);
        var totalCommits = _repoPullRequests.Values
            .SelectMany(r => r.PRs)
            .Where(kv => selectedPrIds.Contains(kv.Key))
            .SelectMany(kv => kv.Value.CommitHashes)
            .Distinct()
            .Count();

        CommitCountRun.Text = $" (コミット数: {totalCommits})";
        ExportButton.IsEnabled = totalCommits > 0;
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "差分出力先フォルダを選択"
        };
        if (dialog.ShowDialog() != true) return;

        var outputPath = dialog.FolderName;
        var selectedPrIds = CollectSelectedPrIds(_workItems);

        SetLoading(true);
        ExportButton.IsEnabled = false;
        var totalExported = 0;
        var repoCount = 0;

        try
        {
            foreach (var (repoPath, (repoName, prs)) in _repoPullRequests)
            {
                var commitHashes = prs
                    .Where(kv => selectedPrIds.Contains(kv.Key))
                    .SelectMany(kv => kv.Value.CommitHashes)
                    .Distinct()
                    .ToList();

                if (commitHashes.Count == 0) continue;

                SetStatus($"差分取得中: {repoName} ({commitHashes.Count} コミット)");
                var (diffFiles, _) = await Task.Run(() =>
                    _gitService.GetDiffFilesForCommits(repoPath, commitHashes));

                if (diffFiles.Count == 0) continue;

                var repoOutputPath = Path.Combine(outputPath, repoName);
                SetStatus($"エクスポート中: {repoName} ({diffFiles.Count} ファイル)");

                var count = await Task.Run(() =>
                    _fileExportService.ExportDiffFiles(
                        repoPath, string.Empty, string.Empty, diffFiles, repoOutputPath));

                totalExported += count;
                repoCount++;
            }

            SetStatus($"完了: {repoCount} リポジトリ、{totalExported} ファイルをエクスポートしました → {outputPath}");
        }
        catch (Exception ex)
        {
            SetStatus($"エラー: {ex.Message}");
        }
        finally
        {
            SetLoading(false);
            ExportButton.IsEnabled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        Close();
    }

    private static (string? Project, string? RepoName) ParseAzureDevOpsRemoteUrl(string? remoteUrl)
    {
        if (string.IsNullOrEmpty(remoteUrl)) return (null, null);

        var m = Regex.Match(remoteUrl, @"dev\.azure\.com/[^/]+/([^/]+)/_git/([^/?#]+)", RegexOptions.IgnoreCase);
        if (m.Success)
            return (Uri.UnescapeDataString(m.Groups[1].Value), Uri.UnescapeDataString(m.Groups[2].Value));

        m = Regex.Match(remoteUrl, @"visualstudio\.com/([^/]+)/_git/([^/?#]+)", RegexOptions.IgnoreCase);
        if (m.Success)
            return (Uri.UnescapeDataString(m.Groups[1].Value), Uri.UnescapeDataString(m.Groups[2].Value));

        return (null, null);
    }

    private static PackIconKind GetWorkItemIcon(string workItemType) => workItemType.ToLower() switch
    {
        "bug" => PackIconKind.Bug,
        "task" => PackIconKind.CheckboxMarkedCircleOutline,
        "user story" => PackIconKind.BookOpenPageVariant,
        "feature" => PackIconKind.TrophyVariant,
        "epic" => PackIconKind.FlashOutline,
        "issue" => PackIconKind.AlertCircleOutline,
        _ => PackIconKind.CardText
    };

    private static SolidColorBrush GetWorkItemBrush(string workItemType) => workItemType.ToLower() switch
    {
        "bug" => new SolidColorBrush(Color.FromRgb(204, 41, 61)),
        "task" => new SolidColorBrush(Color.FromRgb(242, 203, 29)),
        "user story" => new SolidColorBrush(Color.FromRgb(0, 156, 204)),
        "feature" => new SolidColorBrush(Color.FromRgb(119, 59, 147)),
        "epic" => new SolidColorBrush(Color.FromRgb(255, 123, 0)),
        _ => new SolidColorBrush(Color.FromRgb(150, 150, 150))
    };

    private static SolidColorBrush GetWorkItemBackgroundBrush(string workItemType) => workItemType.ToLower() switch
    {
        "bug" => new SolidColorBrush(Color.FromArgb(30, 204, 41, 61)),
        "task" => new SolidColorBrush(Color.FromArgb(30, 242, 203, 29)),
        "user story" => new SolidColorBrush(Color.FromArgb(30, 0, 156, 204)),
        "feature" => new SolidColorBrush(Color.FromArgb(30, 119, 59, 147)),
        "epic" => new SolidColorBrush(Color.FromArgb(30, 255, 123, 0)),
        _ => new SolidColorBrush(Color.FromArgb(20, 150, 150, 150))
    };

    private void SetStatus(string message) => StatusText.Text = message;

    private void SetLoading(bool loading) =>
        LoadingBar.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
}
