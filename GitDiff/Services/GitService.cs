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

    public IReadOnlyList<DiffFileInfo> GetDiffFiles(string repoPath, string baseCommitHash, string targetCommitHash, IReadOnlyList<string> committerFilter)
    {
        using var repo = new Repository(repoPath);
        var baseCommit = repo.Lookup<Commit>(baseCommitHash);
        var targetCommit = repo.Lookup<Commit>(targetCommitHash);

        if (baseCommit == null || targetCommit == null)
            return [];

        // Walk commits between base and target, oldest first
        var commitFilter = new CommitFilter
        {
            IncludeReachableFrom = targetCommit,
            ExcludeReachableFrom = baseCommit,
            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Reverse
        };

        var committerSet = new HashSet<string>(committerFilter, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, DiffFileInfo>(StringComparer.OrdinalIgnoreCase);

        // Analyze each commit individually and aggregate results
        foreach (var commit in repo.Commits.QueryBy(commitFilter))
        {
            if (!committerSet.Contains(commit.Author.Name))
                continue;

            var parents = commit.Parents.ToList();
            TreeChanges treeDiff;

            if (parents.Count == 0)
            {
                treeDiff = repo.Diff.Compare<TreeChanges>(null, commit.Tree);
            }
            else
            {
                // Diff against first parent (standard for non-merge commits)
                treeDiff = repo.Diff.Compare<TreeChanges>(parents[0].Tree, commit.Tree);
            }

            foreach (var change in treeDiff)
            {
                var status = MapStatus(change.Status);
                var oldPath = change.OldPath != change.Path ? change.OldPath : null;

                if (result.TryGetValue(change.Path, out var existing))
                {
                    // Aggregate: if previously Added then later Deleted → remove
                    if (existing.Status == ChangeStatus.Added && status == ChangeStatus.Deleted)
                    {
                        result.Remove(change.Path);
                        continue;
                    }
                    // If previously Added then later Modified → keep as Added
                    if (existing.Status == ChangeStatus.Added && status == ChangeStatus.Modified)
                        continue;
                    // Otherwise update to latest status
                    result[change.Path] = new DiffFileInfo
                    {
                        FilePath = change.Path,
                        OldPath = oldPath ?? existing.OldPath,
                        Status = status
                    };
                }
                else
                {
                    result[change.Path] = new DiffFileInfo
                    {
                        FilePath = change.Path,
                        OldPath = oldPath,
                        Status = status
                    };
                }
            }
        }

        return result.Values.ToList();
    }

    public IReadOnlyList<string> GetCommittersBetween(string repoPath, string baseCommitHash, string targetCommitHash)
    {
        using var repo = new Repository(repoPath);
        var baseCommit = repo.Lookup<Commit>(baseCommitHash);
        var targetCommit = repo.Lookup<Commit>(targetCommitHash);

        if (baseCommit == null || targetCommit == null)
            return [];

        var filter = new CommitFilter
        {
            IncludeReachableFrom = targetCommit,
            ExcludeReachableFrom = baseCommit
        };

        return repo.Commits.QueryBy(filter)
            .Select(c => c.Author.Name)
            .Distinct()
            .OrderBy(name => name)
            .ToList();
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
