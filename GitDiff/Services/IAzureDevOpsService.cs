using GitDiff.Models;

namespace GitDiff.Services;

public interface IAzureDevOpsService
{
    Task<List<AzureDevOpsWorkItem>> GetWorkItemsAsync(string org, string project, string pat, IReadOnlyList<int> workItemIds);
    Task<List<AzureDevOpsPullRequestInfo>> GetPullRequestsWithCommitsAsync(string org, string project, string repoName, string pat, IReadOnlyList<int> pullRequestIds);
    Task TestConnectionAsync(string org, string project, string pat);
}
