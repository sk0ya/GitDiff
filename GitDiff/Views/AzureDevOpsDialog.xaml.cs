using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GitDiff.Models;
using GitDiff.Services;
using MaterialDesignThemes.Wpf;

namespace GitDiff.Views;

public partial class AzureDevOpsDialog : Window
{
    private readonly IAzureDevOpsService _azureService;
    private readonly ISettingsService _settingsService;
    private List<AzureDevOpsWorkItem> _workItems = [];
    private Dictionary<int, AzureDevOpsPullRequestInfo> _pullRequests = new();

    public List<string> ResultCommitHashes { get; private set; } = [];
    public string ResultLabel { get; private set; } = string.Empty;

    public AzureDevOpsDialog(IAzureDevOpsService azureService, ISettingsService settingsService)
    {
        InitializeComponent();
        _azureService = azureService;
        _settingsService = settingsService;
        LoadSettings();
    }

    private void LoadSettings()
    {
        var settings = _settingsService.Load();
        OrgInput.Text = settings.AzureDevOpsOrganization ?? string.Empty;
        ProjectInput.Text = settings.AzureDevOpsProject ?? string.Empty;
        RepoInput.Text = settings.AzureDevOpsRepository ?? string.Empty;
        CredentialTargetInput.Text = settings.AzureDevOpsCredentialTarget ?? string.Empty;

        // Expand settings if not configured
        SettingsExpander.IsExpanded = string.IsNullOrWhiteSpace(OrgInput.Text)
            || string.IsNullOrWhiteSpace(ProjectInput.Text)
            || string.IsNullOrWhiteSpace(RepoInput.Text)
            || string.IsNullOrWhiteSpace(CredentialTargetInput.Text);
    }

    private void SaveSettings()
    {
        var settings = _settingsService.Load();
        settings.AzureDevOpsOrganization = OrgInput.Text.Trim();
        settings.AzureDevOpsProject = ProjectInput.Text.Trim();
        settings.AzureDevOpsRepository = RepoInput.Text.Trim();
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

    private async void FetchButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        var org = OrgInput.Text.Trim();
        var project = ProjectInput.Text.Trim();
        var repoName = RepoInput.Text.Trim();

        if (string.IsNullOrEmpty(org) || string.IsNullOrEmpty(project) || string.IsNullOrEmpty(repoName))
        {
            SetStatus("Settings の Organization, Project, Repository を入力してください。");
            SettingsExpander.IsExpanded = true;
            return;
        }

        var pat = GetPat();
        if (pat == null) return;

        // Parse work item IDs
        var text = WorkItemIdInput.Text ?? string.Empty;
        var idStrings = text.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries);
        var ids = new List<int>();
        foreach (var s in idStrings)
        {
            // Remove # prefix if present
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
        OkButton.IsEnabled = false;
        ResultsTree.Items.Clear();

        try
        {
            // 1. Fetch work items
            _workItems = await _azureService.GetWorkItemsAsync(org, project, pat, ids);

            // 2. Recursively fetch child work items
            SetStatus("子Work Itemを取得中...");
            await FetchChildrenRecursive(_workItems, org, project, pat);

            // 3. Collect all PR IDs (including children)
            var allPrIds = CollectAllPrIds(_workItems);

            if (allPrIds.Count == 0)
            {
                SetStatus("紐づくPull Requestが見つかりませんでした。");
                BuildResultsTree();
                SetLoading(false);
                return;
            }

            SetStatus($"PR を取得中... ({allPrIds.Count} 件)");

            // 4. Fetch PR details and commits
            var prs = await _azureService.GetPullRequestsWithCommitsAsync(org, project, repoName, pat, allPrIds);
            _pullRequests = prs.ToDictionary(p => p.Id);

            // 5. Build tree display
            BuildResultsTree();

            var totalCommits = _pullRequests.Values.SelectMany(p => p.CommitHashes).Distinct().Count();
            var totalWiCount = CountAllWorkItems(_workItems);
            CommitCountRun.Text = $" (コミット数: {totalCommits})";
            OkButton.IsEnabled = totalCommits > 0;
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
            .Where(id => fetched.Add(id)) // Add returns false if already present
            .Distinct()
            .ToList();

        if (childIdsToFetch.Count == 0) return;

        var children = await _azureService.GetWorkItemsAsync(org, project, pat, childIdsToFetch);

        // Assign children to their parents
        var childLookup = children.ToDictionary(c => c.Id);
        foreach (var wi in workItems)
        {
            foreach (var childId in wi.ChildWorkItemIds)
            {
                if (childLookup.TryGetValue(childId, out var child))
                    wi.Children.Add(child);
            }
        }

        // Recurse for grandchildren
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

        // Work Item header with icon and checkbox
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

        var wiIcon = new PackIcon
        {
            Kind = GetWorkItemIcon(wi.WorkItemType),
            Width = 15, Height = 15,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = GetWorkItemBrush(wi.WorkItemType),
            Margin = new Thickness(0, 0, 4, 0)
        };
        wiPanel.Children.Add(wiIcon);

        var wiIdText = new TextBlock
        {
            Text = $"#{wi.Id}",
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        };
        wiPanel.Children.Add(wiIdText);

        var wiTypeText = new TextBlock
        {
            Text = wi.WorkItemType,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = GetWorkItemBrush(wi.WorkItemType),
            Background = GetWorkItemBackgroundBrush(wi.WorkItemType),
            Padding = new Thickness(4, 1, 4, 1),
            Margin = new Thickness(0, 0, 5, 0)
        };
        wiPanel.Children.Add(wiTypeText);

        var wiTitleText = new TextBlock
        {
            Text = wi.Title,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 400
        };
        wiPanel.Children.Add(wiTitleText);
        wiNode.Header = wiPanel;

        // Add PR nodes
        foreach (var prId in wi.LinkedPullRequestIds)
        {
            if (_pullRequests.TryGetValue(prId, out var pr))
            {
                var prPanel = new StackPanel { Orientation = Orientation.Horizontal };

                var prIcon = new PackIcon
                {
                    Kind = PackIconKind.SourcePull,
                    Width = 14, Height = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromRgb(130, 80, 223)),
                    Margin = new Thickness(0, 0, 4, 0)
                };
                prPanel.Children.Add(prIcon);

                var prIdText = new TextBlock
                {
                    Text = $"!{pr.Id}",
                    FontWeight = FontWeights.Medium,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromRgb(130, 80, 223)),
                    Margin = new Thickness(0, 0, 5, 0),
                    FontSize = 11
                };
                prPanel.Children.Add(prIdText);

                var prTitleText = new TextBlock
                {
                    Text = pr.Title,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 350,
                    FontSize = 11
                };
                prPanel.Children.Add(prTitleText);

                if (pr.CommitHashes.Count > 0)
                {
                    var commitBadge = new Border
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
                    };
                    prPanel.Children.Add(commitBadge);
                }

                var prNode = new TreeViewItem
                {
                    Header = prPanel,
                    IsExpanded = false,
                    Margin = new Thickness(0)
                };

                // Add commit items
                if (pr.CommitHashes.Count > 0)
                {
                    foreach (var hash in pr.CommitHashes)
                    {
                        var commitPanel = new StackPanel { Orientation = Orientation.Horizontal };

                        var commitIcon = new PackIcon
                        {
                            Kind = PackIconKind.SourceCommit,
                            Width = 13, Height = 13,
                            VerticalAlignment = VerticalAlignment.Center,
                            Foreground = Brushes.Gray,
                            Margin = new Thickness(0, 0, 3, 0)
                        };
                        commitPanel.Children.Add(commitIcon);

                        var commitText = new TextBlock
                        {
                            Text = hash.Length >= 7 ? hash[..7] : hash,
                            FontFamily = new FontFamily("Consolas"),
                            FontSize = 10,
                            VerticalAlignment = VerticalAlignment.Center,
                            Foreground = Brushes.Gray
                        };
                        commitPanel.Children.Add(commitText);

                        prNode.Items.Add(new TreeViewItem
                        {
                            Header = commitPanel,
                            Padding = new Thickness(0),
                            Margin = new Thickness(0),
                            MinHeight = 18
                        });
                    }
                }
                else
                {
                    prNode.Items.Add(new TreeViewItem
                    {
                        Header = new TextBlock { Text = "(コミットなし)", FontStyle = FontStyles.Italic, Foreground = Brushes.Gray, FontSize = 11 },
                        IsEnabled = false,
                        Margin = new Thickness(0),
                        MinHeight = 18
                    });
                }
                wiNode.Items.Add(prNode);
            }
            else
            {
                var failPanel = new StackPanel { Orientation = Orientation.Horizontal };
                failPanel.Children.Add(new PackIcon
                {
                    Kind = PackIconKind.AlertCircleOutline,
                    Width = 14, Height = 14,
                    Foreground = Brushes.OrangeRed,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0)
                });
                failPanel.Children.Add(new TextBlock
                {
                    Text = $"PR #{prId}: (取得失敗)",
                    Foreground = Brushes.OrangeRed,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 11
                });
                wiNode.Items.Add(new TreeViewItem { Header = failPanel, Margin = new Thickness(0) });
            }
        }

        // Add child work item nodes recursively
        foreach (var child in wi.Children)
            wiNode.Items.Add(BuildWorkItemNode(child));

        // Show "(PRなし)" only if no PRs and no children
        if (wi.LinkedPullRequestIds.Count == 0 && wi.Children.Count == 0)
        {
            wiNode.Items.Add(new TreeViewItem
            {
                Header = new TextBlock { Text = "(PRなし)", FontStyle = FontStyles.Italic, Foreground = Brushes.Gray, FontSize = 11 },
                IsEnabled = false,
                Margin = new Thickness(0),
                MinHeight = 18
            });
        }

        return wiNode;
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

    private void UpdateCommitCount()
    {
        var prIds = new HashSet<int>();
        CollectSelectedPrIds(_workItems, prIds);

        var totalCommits = prIds
            .Where(id => _pullRequests.ContainsKey(id))
            .SelectMany(id => _pullRequests[id].CommitHashes)
            .Distinct()
            .Count();

        CommitCountRun.Text = $" (コミット数: {totalCommits})";
        OkButton.IsEnabled = totalCommits > 0;
    }

    private static void CollectSelectedPrIds(List<AzureDevOpsWorkItem> workItems, HashSet<int> result)
    {
        foreach (var wi in workItems)
        {
            if (wi.IsSelected)
            {
                foreach (var prId in wi.LinkedPullRequestIds)
                    result.Add(prId);
            }
            CollectSelectedPrIds(wi.Children, result);
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();

        // Collect commit hashes from selected work items (including children)
        var prIds = new HashSet<int>();
        CollectSelectedPrIds(_workItems, prIds);

        ResultCommitHashes = prIds
            .Where(id => _pullRequests.ContainsKey(id))
            .SelectMany(id => _pullRequests[id].CommitHashes)
            .Distinct()
            .ToList();

        // Build label for display (top-level IDs only)
        var wiIds = _workItems.Where(wi => wi.IsSelected).Select(wi => $"#{wi.Id}");
        ResultLabel = "Azure DevOps: " + string.Join(", ", wiIds);

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        DialogResult = false;
    }

    private void SetStatus(string message) => StatusText.Text = message;

    private void SetLoading(bool loading)
    {
        LoadingBar.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
    }
}
