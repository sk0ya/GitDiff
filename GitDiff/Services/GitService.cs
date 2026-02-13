using System.IO;
using System.Text.RegularExpressions;
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

    public IReadOnlyList<DiffFileInfo> GetDiffFiles(string repoPath, string baseCommitHash, string targetCommitHash, bool excludeMergeCommits = false)
    {
        using var repo = new Repository(repoPath);
        var baseCommit = repo.Lookup<Commit>(baseCommitHash);
        var targetCommit = repo.Lookup<Commit>(targetCommitHash);

        if (baseCommit == null || targetCommit == null)
            return [];

        // Always walk commits to populate AuthorName
        return GetDiffFilesWalkingCommits(repo, baseCommit, targetCommit, committerFilter: null, excludeMergeCommits);
    }

    private IReadOnlyList<DiffFileInfo> GetDiffFilesWalkingCommits(
        Repository repo, Commit baseCommit, Commit targetCommit,
        IReadOnlyList<string>? committerFilter, bool excludeMergeCommits)
    {
        // Walk commits between base and target, oldest first
        var commitFilter = new CommitFilter
        {
            IncludeReachableFrom = targetCommit,
            ExcludeReachableFrom = baseCommit,
            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Reverse
        };

        var committerSet = committerFilter != null
            ? new HashSet<string>(committerFilter, StringComparer.OrdinalIgnoreCase)
            : null;
        var result = new Dictionary<string, DiffFileInfo>(StringComparer.OrdinalIgnoreCase);

        // Analyze each commit individually and aggregate results
        foreach (var commit in repo.Commits.QueryBy(commitFilter))
        {
            if (committerSet != null && !committerSet.Contains(commit.Author.Name))
                continue;

            var parents = commit.Parents.ToList();

            if (excludeMergeCommits && parents.Count > 1)
                continue;

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
                        Status = status,
                        AuthorName = commit.Author.Name
                    };
                }
                else
                {
                    result[change.Path] = new DiffFileInfo
                    {
                        FilePath = change.Path,
                        OldPath = oldPath,
                        Status = status,
                        AuthorName = commit.Author.Name
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

    public IReadOnlyList<int> GetChangedLineNumbers(string repoPath, string baseCommitHash, string targetCommitHash, string filePath)
    {
        using var repo = new Repository(repoPath);
        var baseCommit = repo.Lookup<Commit>(baseCommitHash);
        var targetCommit = repo.Lookup<Commit>(targetCommitHash);

        if (baseCommit == null || targetCommit == null)
            return [];

        // For Added files, return all line numbers
        var treeEntry = targetCommit[filePath];
        if (treeEntry == null) return [];

        var baseEntry = baseCommit[filePath];
        if (baseEntry == null)
        {
            // File is new — all lines are changed
            var blob = (Blob)treeEntry.Target;
            using var reader = new StreamReader(blob.GetContentStream());
            var content = reader.ReadToEnd();
            var lineCount = content.Split('\n').Length;
            return Enumerable.Range(1, lineCount).ToList();
        }

        // Get patch for this specific file
        var patch = repo.Diff.Compare<Patch>(baseCommit.Tree, targetCommit.Tree, [filePath]);
        var patchEntry = patch[filePath];
        if (patchEntry == null) return [];

        return ParseChangedLineNumbers(patchEntry.Patch);
    }

    private static IReadOnlyList<int> ParseChangedLineNumbers(string patchText)
    {
        var changedLines = new List<int>();
        var hunkHeaderRegex = new Regex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@");
        var currentLine = 0;

        foreach (var line in patchText.Split('\n'))
        {
            var match = hunkHeaderRegex.Match(line);
            if (match.Success)
            {
                currentLine = int.Parse(match.Groups[1].Value);
                continue;
            }

            if (currentLine == 0) continue;

            if (line.StartsWith('+'))
            {
                changedLines.Add(currentLine);
                currentLine++;
            }
            else if (line.StartsWith('-'))
            {
                // Deleted line — don't increment target line number
            }
            else
            {
                // Context line
                currentLine++;
            }
        }

        return changedLines;
    }

    public IReadOnlyList<FileCommitInfo> GetFileCommitsBetween(string repoPath, string baseCommitHash, string targetCommitHash, string filePath)
    {
        using var repo = new Repository(repoPath);
        var baseCommit = repo.Lookup<Commit>(baseCommitHash);
        var targetCommit = repo.Lookup<Commit>(targetCommitHash);

        if (baseCommit == null || targetCommit == null)
            return [];

        var filter = new CommitFilter
        {
            IncludeReachableFrom = targetCommit,
            ExcludeReachableFrom = baseCommit,
            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Reverse
        };

        var result = new List<FileCommitInfo>();

        foreach (var commit in repo.Commits.QueryBy(filter))
        {
            var parents = commit.Parents.ToList();

            // Skip merge commits
            if (parents.Count > 1)
                continue;

            TreeChanges changes;

            if (parents.Count == 0)
            {
                changes = repo.Diff.Compare<TreeChanges>(null, commit.Tree);
            }
            else
            {
                changes = repo.Diff.Compare<TreeChanges>(parents[0].Tree, commit.Tree);
            }

            if (changes.Any(c => string.Equals(c.Path, filePath, StringComparison.OrdinalIgnoreCase)
                              || string.Equals(c.OldPath, filePath, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(new FileCommitInfo
                {
                    Hash = commit.Sha,
                    Message = commit.MessageShort,
                    Author = commit.Author.Name,
                    Date = commit.Author.When,
                    ParentHash = parents.Count > 0 ? parents[0].Sha : string.Empty
                });
            }
        }

        return result;
    }

    public FileDiffResult GetFileDiff(string repoPath, string oldCommitHash, string newCommitHash, string filePath)
    {
        using var repo = new Repository(repoPath);
        var oldCommit = repo.Lookup<Commit>(oldCommitHash);
        var newCommit = repo.Lookup<Commit>(newCommitHash);

        if (oldCommit == null || newCommit == null)
            return new FileDiffResult();

        var patch = repo.Diff.Compare<Patch>(oldCommit.Tree, newCommit.Tree);

        // Iterate instead of indexer for robust case-insensitive matching
        PatchEntryChanges? patchEntry = null;
        foreach (var entry in patch)
        {
            if (string.Equals(entry.Path, filePath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.OldPath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                patchEntry = entry;
                break;
            }
        }

        if (patchEntry == null || string.IsNullOrEmpty(patchEntry.Patch))
            return new FileDiffResult();

        var patchText = patchEntry.Patch;
        var lines = ParseDiffLines(patchText);

        return new FileDiffResult
        {
            RawPatch = patchText,
            Lines = lines
        };
    }

    private static IReadOnlyList<DiffLine> ParseDiffLines(string patchText)
    {
        var result = new List<DiffLine>();
        var hunkHeaderRegex = new Regex(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@(.*)");
        var oldLine = 0;
        var newLine = 0;

        foreach (var rawLine in patchText.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            var match = hunkHeaderRegex.Match(line);
            if (match.Success)
            {
                oldLine = int.Parse(match.Groups[1].Value);
                newLine = int.Parse(match.Groups[2].Value);
                result.Add(new DiffLine
                {
                    Type = DiffLineType.Hunk,
                    Content = line
                });
                continue;
            }

            if (oldLine == 0 && newLine == 0) continue;

            if (line.StartsWith('+'))
            {
                result.Add(new DiffLine
                {
                    Type = DiffLineType.Added,
                    NewLineNumber = newLine,
                    Content = line.Length > 1 ? line[1..] : string.Empty
                });
                newLine++;
            }
            else if (line.StartsWith('-'))
            {
                result.Add(new DiffLine
                {
                    Type = DiffLineType.Deleted,
                    OldLineNumber = oldLine,
                    Content = line.Length > 1 ? line[1..] : string.Empty
                });
                oldLine++;
            }
            else if (line.StartsWith(' '))
            {
                result.Add(new DiffLine
                {
                    Type = DiffLineType.Context,
                    OldLineNumber = oldLine,
                    NewLineNumber = newLine,
                    Content = line.Length > 1 ? line[1..] : string.Empty
                });
                oldLine++;
                newLine++;
            }
        }

        return result;
    }

    public IReadOnlyList<DiffFileInfo> GetDiffFilesForCommits(string repoPath, IReadOnlyList<string> commitHashes)
    {
        return GetDiffFilesForCommitsCore(repoPath, commitHashes, committerFilter: null);
    }

    public IReadOnlyList<string> GetCommittersForCommits(string repoPath, IReadOnlyList<string> commitHashes)
    {
        using var repo = new Repository(repoPath);
        return commitHashes
            .Select(h => repo.Lookup<Commit>(h))
            .Where(c => c != null && c.Parents.Count() <= 1)
            .Select(c => c!.Author.Name)
            .Distinct()
            .OrderBy(n => n)
            .ToList();
    }

    private IReadOnlyList<DiffFileInfo> GetDiffFilesForCommitsCore(string repoPath, IReadOnlyList<string> commitHashes, IReadOnlyList<string>? committerFilter)
    {
        using var repo = new Repository(repoPath);

        // Resolve each hash and pair with parent, sorted by date (oldest first)
        // Skip merge commits (multiple parents)
        var commits = new List<(Commit commit, Commit? parent)>();
        foreach (var hash in commitHashes)
        {
            var commit = repo.Lookup<Commit>(hash);
            if (commit == null)
                throw new ArgumentException($"コミットが見つかりません: {hash}");
            var parents = commit.Parents.ToList();
            if (parents.Count > 1)
                continue; // Skip merge commits
            var parent = parents.FirstOrDefault();
            commits.Add((commit, parent));
        }
        commits.Sort((a, b) => a.commit.Author.When.CompareTo(b.commit.Author.When));

        var committerSet = committerFilter != null
            ? new HashSet<string>(committerFilter, StringComparer.OrdinalIgnoreCase)
            : null;
        var result = new Dictionary<string, DiffFileInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var (commit, parent) in commits)
        {
            if (committerSet != null && !committerSet.Contains(commit.Author.Name))
                continue;

            TreeChanges treeDiff;
            if (parent == null)
                treeDiff = repo.Diff.Compare<TreeChanges>(null, commit.Tree);
            else
                treeDiff = repo.Diff.Compare<TreeChanges>(parent.Tree, commit.Tree);

            var parentHash = parent?.Sha ?? string.Empty;

            foreach (var change in treeDiff)
            {
                var status = MapStatus(change.Status);
                var oldPath = change.OldPath != change.Path ? change.OldPath : null;

                if (result.TryGetValue(change.Path, out var existing))
                {
                    if (existing.Status == ChangeStatus.Added && status == ChangeStatus.Deleted)
                    {
                        result.Remove(change.Path);
                        continue;
                    }
                    if (existing.Status == ChangeStatus.Added && status == ChangeStatus.Modified)
                    {
                        // Keep Added status but update source commit to latest
                        result[change.Path] = new DiffFileInfo
                        {
                            FilePath = existing.FilePath,
                            OldPath = existing.OldPath,
                            Status = ChangeStatus.Added,
                            SourceCommitHash = commit.Sha,
                            BaseCommitHash = existing.BaseCommitHash,
                            AuthorName = commit.Author.Name
                        };
                        continue;
                    }
                    result[change.Path] = new DiffFileInfo
                    {
                        FilePath = change.Path,
                        OldPath = oldPath ?? existing.OldPath,
                        Status = status,
                        SourceCommitHash = commit.Sha,
                        BaseCommitHash = existing.BaseCommitHash,
                        AuthorName = commit.Author.Name
                    };
                }
                else
                {
                    result[change.Path] = new DiffFileInfo
                    {
                        FilePath = change.Path,
                        OldPath = oldPath,
                        Status = status,
                        SourceCommitHash = commit.Sha,
                        BaseCommitHash = parentHash,
                        AuthorName = commit.Author.Name
                    };
                }
            }
        }

        return result.Values.ToList();
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
