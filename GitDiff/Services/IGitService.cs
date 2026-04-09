using GitDiff.Models;

namespace GitDiff.Services;

public interface IGitService
{
    bool IsValidRepository(string path);
    IReadOnlyList<CommitInfo> GetCommits(string repoPath);
    IReadOnlyList<string> GetCommitters(string repoPath);
    (string BaseCommitHash, string TargetCommitHash) NormalizeCommitPair(string repoPath, string firstCommitHash, string secondCommitHash);
    IReadOnlyList<DiffFileInfo> GetDiffFiles(string repoPath, string baseCommitHash, string targetCommitHash, bool excludeMergeCommits = false);
    byte[]? GetFileContent(string repoPath, string commitHash, string filePath);
    IReadOnlyList<int> GetChangedLineNumbers(string repoPath, string baseCommitHash, string targetCommitHash, string filePath);
    (IReadOnlyList<int> OldLines, IReadOnlyList<int> NewLines) GetChangedLineNumbersDetailed(string repoPath, string baseCommitHash, string targetCommitHash, string filePath);
    IReadOnlyList<FileCommitInfo> GetFileCommitsBetween(string repoPath, string baseCommitHash, string targetCommitHash, string filePath);
    FileDiffResult GetFileDiff(string repoPath, string oldCommitHash, string newCommitHash, string filePath);
    (IReadOnlyList<DiffFileInfo> Files, IReadOnlyList<string> NotFoundHashes) GetDiffFilesForCommits(string repoPath, IReadOnlyList<string> commitHashes);
    string? GetRemoteUrl(string repoPath);
}
