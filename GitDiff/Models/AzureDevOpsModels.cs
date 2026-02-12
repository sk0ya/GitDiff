namespace GitDiff.Models;

public class AzureDevOpsWorkItem
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string WorkItemType { get; set; } = string.Empty;
    public List<int> LinkedPullRequestIds { get; set; } = [];
    public List<int> ChildWorkItemIds { get; set; } = [];
    public List<AzureDevOpsWorkItem> Children { get; set; } = [];
    public bool IsSelected { get; set; } = true;
}

public class AzureDevOpsPullRequestInfo
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public List<string> CommitHashes { get; set; } = [];
}
