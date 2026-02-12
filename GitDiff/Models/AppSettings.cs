namespace GitDiff.Models;

public class AppSettings
{
    public string? LastRepositoryPath { get; set; }
    public List<string> RecentRepositories { get; set; } = [];
    public string? AzureDevOpsOrganization { get; set; }
    public string? AzureDevOpsProject { get; set; }
    public string? AzureDevOpsRepository { get; set; }
    public string? AzureDevOpsCredentialTarget { get; set; }
}
