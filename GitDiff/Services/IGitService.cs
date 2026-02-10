using GitDiff.Models;

namespace GitDiff.Services;

public interface IGitService
{
    bool IsValidRepository(string path);
    IReadOnlyList<CommitInfo> GetCommits(string repoPath);
    IReadOnlyList<string> GetCommitters(string repoPath);
    IReadOnlyList<DiffFileInfo> GetDiffFiles(string repoPath, string baseCommitHash, string targetCommitHash, bool excludeMergeCommits = false);
    IReadOnlyList<DiffFileInfo> GetDiffFiles(string repoPath, string baseCommitHash, string targetCommitHash, IReadOnlyList<string> committerFilter, bool excludeMergeCommits = false);
    IReadOnlyList<string> GetCommittersBetween(string repoPath, string baseCommitHash, string targetCommitHash);
    byte[]? GetFileContent(string repoPath, string commitHash, string filePath);
}
