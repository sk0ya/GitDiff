namespace GitDiff.Models;

public class AppSettings
{
    public string? LastRepositoryPath { get; set; }
    public List<string> RecentRepositories { get; set; } = [];
}
