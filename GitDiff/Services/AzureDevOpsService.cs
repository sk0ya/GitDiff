using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GitDiff.Models;

namespace GitDiff.Services;

public class AzureDevOpsService : IAzureDevOpsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task TestConnectionAsync(string org, string project, string pat)
    {
        using var client = CreateClient(pat);
        var url = $"https://dev.azure.com/{org}/{project}/_apis/projects/{project}?api-version=7.0";
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
    }

    public async Task<List<AzureDevOpsWorkItem>> GetWorkItemsAsync(
        string org, string project, string pat, IReadOnlyList<int> workItemIds)
    {
        if (workItemIds.Count == 0) return [];

        using var client = CreateClient(pat);
        var ids = string.Join(",", workItemIds);
        var url = $"https://dev.azure.com/{org}/{project}/_apis/wit/workitems?ids={ids}&$expand=relations&api-version=7.0";

        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);

        var result = new List<AzureDevOpsWorkItem>();
        foreach (var item in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var wi = new AzureDevOpsWorkItem
            {
                Id = item.GetProperty("id").GetInt32(),
                Title = GetFieldString(item, "System.Title"),
                WorkItemType = GetFieldString(item, "System.WorkItemType")
            };

            // Extract linked PR IDs from relations
            if (item.TryGetProperty("relations", out var relations))
            {
                foreach (var rel in relations.EnumerateArray())
                {
                    var relUrl = rel.GetProperty("url").GetString() ?? string.Empty;
                    var relName = string.Empty;
                    if (rel.TryGetProperty("attributes", out var attrs) &&
                        attrs.TryGetProperty("name", out var nameEl))
                    {
                        relName = nameEl.GetString() ?? string.Empty;
                    }

                    // PR links have "Pull Request" in the name attribute
                    if (relName.Contains("Pull Request", StringComparison.OrdinalIgnoreCase))
                    {
                        var prId = ExtractPullRequestIdFromUrl(relUrl);
                        if (prId > 0)
                            wi.LinkedPullRequestIds.Add(prId);
                    }
                }
            }

            result.Add(wi);
        }

        return result;
    }

    public async Task<List<AzureDevOpsPullRequestInfo>> GetPullRequestsWithCommitsAsync(
        string org, string project, string repoName, string pat, IReadOnlyList<int> pullRequestIds)
    {
        var result = new List<AzureDevOpsPullRequestInfo>();
        using var client = CreateClient(pat);

        foreach (var prId in pullRequestIds.Distinct())
        {
            var pr = new AzureDevOpsPullRequestInfo { Id = prId };

            // Get PR title
            try
            {
                var prUrl = $"https://dev.azure.com/{org}/{project}/_apis/git/repositories/{repoName}/pullrequests/{prId}?api-version=7.0";
                var prResponse = await client.GetAsync(prUrl);
                if (prResponse.IsSuccessStatusCode)
                {
                    var prJson = await prResponse.Content.ReadAsStringAsync();
                    var prDoc = JsonDocument.Parse(prJson);
                    pr.Title = prDoc.RootElement.TryGetProperty("title", out var titleEl)
                        ? titleEl.GetString() ?? $"PR #{prId}"
                        : $"PR #{prId}";
                }
            }
            catch
            {
                pr.Title = $"PR #{prId}";
            }

            // Get PR commits
            try
            {
                var commitsUrl = $"https://dev.azure.com/{org}/{project}/_apis/git/repositories/{repoName}/pullrequests/{prId}/commits?api-version=7.0";
                var commitsResponse = await client.GetAsync(commitsUrl);
                if (commitsResponse.IsSuccessStatusCode)
                {
                    var commitsJson = await commitsResponse.Content.ReadAsStringAsync();
                    var commitsDoc = JsonDocument.Parse(commitsJson);

                    foreach (var commit in commitsDoc.RootElement.GetProperty("value").EnumerateArray())
                    {
                        var commitId = commit.GetProperty("commitId").GetString();
                        if (!string.IsNullOrEmpty(commitId))
                            pr.CommitHashes.Add(commitId);
                    }
                }
            }
            catch
            {
                // Skip commits fetch error for this PR
            }

            result.Add(pr);
        }

        return result;
    }

    private static HttpClient CreateClient(string pat)
    {
        var client = new HttpClient();
        var encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        return client;
    }

    private static string GetFieldString(JsonElement item, string fieldName)
    {
        if (item.TryGetProperty("fields", out var fields) &&
            fields.TryGetProperty(fieldName, out var value))
        {
            return value.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static int ExtractPullRequestIdFromUrl(string url)
    {
        // Format: vstfs:///Git/PullRequestId/{projectId}%2F{repoId}%2F{pullRequestId}
        // or: https://dev.azure.com/{org}/{project}/_apis/git/repositories/{repo}/pullRequests/{id}
        try
        {
            // Try vstfs format
            if (url.Contains("vstfs:///Git/PullRequestId/", StringComparison.OrdinalIgnoreCase))
            {
                var decoded = Uri.UnescapeDataString(url);
                var lastSlash = decoded.LastIndexOf('/');
                if (lastSlash >= 0 && int.TryParse(decoded[(lastSlash + 1)..], out var id))
                    return id;
            }

            // Try REST API URL format
            var prIndex = url.LastIndexOf("pullRequests/", StringComparison.OrdinalIgnoreCase);
            if (prIndex < 0)
                prIndex = url.LastIndexOf("pullrequests/", StringComparison.OrdinalIgnoreCase);
            if (prIndex >= 0)
            {
                var afterPr = url[(prIndex + "pullRequests/".Length)..];
                var endIndex = afterPr.IndexOfAny(['?', '/']);
                var idStr = endIndex >= 0 ? afterPr[..endIndex] : afterPr;
                if (int.TryParse(idStr, out var id))
                    return id;
            }
        }
        catch
        {
            // Ignore parse errors
        }
        return 0;
    }
}
