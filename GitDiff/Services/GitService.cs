using System.IO;
using GitDiff.Models;
using LibGit2Sharp;

namespace GitDiff.Services;

public class GitService : IGitService
{
    public bool IsValidRepository(string path)
    {
        return Repository.IsValid(path);
    }

    public IReadOnlyList<CommitInfo> GetCommits(string repoPath)
    {
        using var repo = new Repository(repoPath);
        return repo.Commits
            .Select(c => new CommitInfo
            {
                Hash = c.Sha,
                Message = c.MessageShort,
                Author = c.Author.Name,
                Email = c.Author.Email,
                Date = c.Author.When
            })
            .ToList();
    }

    public IReadOnlyList<string> GetCommitters(string repoPath)
    {
        using var repo = new Repository(repoPath);
        return repo.Commits
            .Select(c => c.Author.Name)
            .Distinct()
            .OrderBy(name => name)
            .ToList();
    }

    public IReadOnlyList<DiffFileInfo> GetDiffFiles(string repoPath, string baseCommitHash, string targetCommitHash)
    {
        using var repo = new Repository(repoPath);
        var baseCommit = repo.Lookup<Commit>(baseCommitHash);
        var targetCommit = repo.Lookup<Commit>(targetCommitHash);

        if (baseCommit == null || targetCommit == null)
            return [];

        var changes = repo.Diff.Compare<TreeChanges>(baseCommit.Tree, targetCommit.Tree);

        return changes.Select(change => new DiffFileInfo
        {
            FilePath = change.Path,
            OldPath = change.OldPath != change.Path ? change.OldPath : null,
            Status = MapStatus(change.Status)
        }).ToList();
    }

    public byte[]? GetFileContent(string repoPath, string commitHash, string filePath)
    {
        using var repo = new Repository(repoPath);
        var commit = repo.Lookup<Commit>(commitHash);
        if (commit == null) return null;

        var treeEntry = commit[filePath];
        if (treeEntry == null) return null;

        var blob = (Blob)treeEntry.Target;
        using var stream = blob.GetContentStream();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static ChangeStatus MapStatus(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => ChangeStatus.Added,
        ChangeKind.Deleted => ChangeStatus.Deleted,
        ChangeKind.Modified => ChangeStatus.Modified,
        ChangeKind.Renamed => ChangeStatus.Renamed,
        ChangeKind.Copied => ChangeStatus.Copied,
        _ => ChangeStatus.Modified
    };
}
