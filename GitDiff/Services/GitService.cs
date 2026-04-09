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

    public (string BaseCommitHash, string TargetCommitHash) NormalizeCommitPair(string repoPath, string firstCommitHash, string secondCommitHash)
    {
        using var repo = new Repository(repoPath);
        var firstCommit = repo.Lookup<Commit>(firstCommitHash);
        var secondCommit = repo.Lookup<Commit>(secondCommitHash);

        if (firstCommit == null || secondCommit == null)
            return (firstCommitHash, secondCommitHash);

        var (baseCommit, targetCommit) = NormalizeCommitOrder(repo, firstCommit, secondCommit);
        return (baseCommit.Sha, targetCommit.Sha);
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
        (baseCommit, targetCommit) = NormalizeCommitOrder(repo, baseCommit, targetCommit);

        var commitFilter = new CommitFilter
        {
            IncludeReachableFrom = targetCommit,
            ExcludeReachableFrom = baseCommit,
            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Reverse
        };

        var committerSet = committerFilter != null
            ? new HashSet<string>(committerFilter, StringComparer.OrdinalIgnoreCase)
            : null;
        var commitDiffs = new List<(Commit Commit, Commit? Parent, TreeChanges Changes)>();

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

            commitDiffs.Add((commit, parents.FirstOrDefault(), treeDiff));
        }

        return AggregateDiffFiles(commitDiffs);
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
        var (oldLines, newLines) = GetChangedLineNumbersDetailed(repoPath, baseCommitHash, targetCommitHash, filePath);
        return oldLines.Concat(newLines).Distinct().OrderBy(x => x).ToList();
    }

    public (IReadOnlyList<int> OldLines, IReadOnlyList<int> NewLines) GetChangedLineNumbersDetailed(
        string repoPath, string baseCommitHash, string targetCommitHash, string filePath)
    {
        using var repo = new Repository(repoPath);
        var baseCommit = repo.Lookup<Commit>(baseCommitHash);
        var targetCommit = repo.Lookup<Commit>(targetCommitHash);

        if (baseCommit == null || targetCommit == null)
            return ([], []);

        (baseCommit, targetCommit) = NormalizeCommitOrder(repo, baseCommit, targetCommit);

        var treeEntry = targetCommit[filePath];
        var baseEntry = baseCommit[filePath];

        if (baseEntry == null && treeEntry == null)
            return ([], []);

        if (baseEntry == null)
        {
            // File is new — all lines are changed
            var blob = (Blob)treeEntry.Target;
            using var reader = new StreamReader(blob.GetContentStream());
            var content = reader.ReadToEnd();
            var lineCount = content.Split('\n').Length;
            return ([], Enumerable.Range(1, lineCount).ToList());
        }

        if (treeEntry == null)
        {
            // File is deleted — all old lines are changed
            var blob = (Blob)baseEntry.Target;
            using var reader = new StreamReader(blob.GetContentStream());
            var content = reader.ReadToEnd();
            var lineCount = content.Split('\n').Length;
            return (Enumerable.Range(1, lineCount).ToList(), []);
        }

        // Get patch for this specific file
        var patch = repo.Diff.Compare<Patch>(baseCommit.Tree, targetCommit.Tree, [filePath]);
        var patchEntry = patch[filePath];
        if (patchEntry == null) return ([], []);

        return ParseChangedLineNumbersDetailed(patchEntry.Patch);
    }

    private static (IReadOnlyList<int> OldLines, IReadOnlyList<int> NewLines) ParseChangedLineNumbersDetailed(string patchText)
    {
        var oldChangedLines = new HashSet<int>();
        var newChangedLines = new HashSet<int>();
        var oldLine = 0;
        var newLine = 0;

        foreach (var line in patchText.Split('\n'))
        {
            var match = Regex.Match(line, @"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@");
            if (match.Success)
            {
                oldLine = int.Parse(match.Groups[1].Value);
                newLine = int.Parse(match.Groups[2].Value);
                continue;
            }

            if (oldLine == 0 && newLine == 0) continue;

            if (line.StartsWith('+'))
            {
                newChangedLines.Add(newLine);
                newLine++;
            }
            else if (line.StartsWith('-'))
            {
                oldChangedLines.Add(oldLine);
                oldLine++;
            }
            else
            {
                oldLine++;
                newLine++;
            }
        }

        return ([.. oldChangedLines.OrderBy(x => x)], [.. newChangedLines.OrderBy(x => x)]);
    }

    public IReadOnlyList<FileCommitInfo> GetFileCommitsBetween(string repoPath, string baseCommitHash, string targetCommitHash, string filePath)
    {
        using var repo = new Repository(repoPath);
        var baseCommit = repo.Lookup<Commit>(baseCommitHash);
        var targetCommit = repo.Lookup<Commit>(targetCommitHash);

        if (baseCommit == null || targetCommit == null)
            return [];

        (baseCommit, targetCommit) = NormalizeCommitOrder(repo, baseCommit, targetCommit);

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

        (oldCommit, newCommit) = NormalizeCommitOrder(repo, oldCommit, newCommit);

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

    public (IReadOnlyList<DiffFileInfo> Files, IReadOnlyList<string> NotFoundHashes) GetDiffFilesForCommits(string repoPath, IReadOnlyList<string> commitHashes)
    {
        return GetDiffFilesForCommitsCore(repoPath, commitHashes, committerFilter: null);
    }

    private (IReadOnlyList<DiffFileInfo> Files, IReadOnlyList<string> NotFoundHashes) GetDiffFilesForCommitsCore(string repoPath, IReadOnlyList<string> commitHashes, IReadOnlyList<string>? committerFilter)
    {
        using var repo = new Repository(repoPath);

        // Resolve each hash and pair with parent, then sort by ancestry.
        // Skip merge commits (multiple parents)
        var commits = new List<(Commit commit, Commit? parent)>();
        var notFoundHashes = new List<string>();
        foreach (var hash in commitHashes)
        {
            var commit = repo.Lookup<Commit>(hash);
            if (commit == null)
            {
                notFoundHashes.Add(hash);
                continue;
            }
            var parents = commit.Parents.ToList();
            if (parents.Count > 1)
                continue; // Skip merge commits
            var parent = parents.FirstOrDefault();
            commits.Add((commit, parent));
        }
        SortCommitsForAggregation(repo, commits);

        var committerSet = committerFilter != null
            ? new HashSet<string>(committerFilter, StringComparer.OrdinalIgnoreCase)
            : null;
        var commitDiffs = new List<(Commit Commit, Commit? Parent, TreeChanges Changes)>();

        foreach (var (commit, parent) in commits)
        {
            if (committerSet != null && !committerSet.Contains(commit.Author.Name))
                continue;

            TreeChanges treeDiff;
            if (parent == null)
                treeDiff = repo.Diff.Compare<TreeChanges>(null, commit.Tree);
            else
                treeDiff = repo.Diff.Compare<TreeChanges>(parent.Tree, commit.Tree);

            commitDiffs.Add((commit, parent, treeDiff));
        }

        return (AggregateDiffFiles(commitDiffs), notFoundHashes);
    }

    private static IReadOnlyList<DiffFileInfo> AggregateDiffFiles(IEnumerable<(Commit Commit, Commit? Parent, TreeChanges Changes)> commitDiffs)
    {
        var states = new List<AggregatedFileState>();
        var pathIndex = new Dictionary<string, AggregatedFileState>(StringComparer.OrdinalIgnoreCase);

        foreach (var (commit, parent, changes) in commitDiffs)
        {
            foreach (var change in changes)
            {
                var status = MapStatus(change.Status);
                var oldPath = change.OldPath != change.Path ? change.OldPath : null;

                var state = ResolveAggregatedState(states, pathIndex, parent, change.Path, oldPath, status);
                UpdateAggregatedState(state, pathIndex, commit, parent, change.Path, oldPath, status);
            }
        }

        return states
            .Select(BuildDiffFileInfo)
            .Where(file => file != null)
            .Select(file => file!)
            .ToList();
    }

    private static AggregatedFileState ResolveAggregatedState(
        ICollection<AggregatedFileState> states,
        IDictionary<string, AggregatedFileState> pathIndex,
        Commit? parent,
        string path,
        string? oldPath,
        ChangeStatus status)
    {
        if (pathIndex.TryGetValue(path, out var state))
            return state;

        if (!string.IsNullOrEmpty(oldPath) && pathIndex.TryGetValue(oldPath, out state))
            return state;

        var basePath = status is ChangeStatus.Added or ChangeStatus.Copied
            ? null
            : oldPath ?? path;

        state = new AggregatedFileState
        {
            BasePath = basePath,
            BaseBlobId = basePath == null ? null : GetBlobId(parent, basePath),
            BaseCommitHash = parent?.Sha ?? string.Empty,
            LookupPath = basePath ?? oldPath ?? path
        };

        states.Add(state);
        pathIndex[state.LookupPath] = state;
        return state;
    }

    private static void UpdateAggregatedState(
        AggregatedFileState state,
        IDictionary<string, AggregatedFileState> pathIndex,
        Commit commit,
        Commit? parent,
        string path,
        string? oldPath,
        ChangeStatus status)
    {
        if (!string.IsNullOrEmpty(state.LookupPath))
            pathIndex.Remove(state.LookupPath);

        if (status == ChangeStatus.Copied && !string.IsNullOrEmpty(oldPath))
        {
            state.SawCopy = true;
            state.CopySourcePath ??= oldPath;
        }

        state.CurrentPath = status == ChangeStatus.Deleted ? null : path;
        state.CurrentBlobId = status == ChangeStatus.Deleted ? null : GetBlobId(commit, path);
        state.LookupPath = state.CurrentPath ?? oldPath ?? path;
        state.SourceCommitHash = commit.Sha;
        state.AuthorName = commit.Author.Name;

        if (!string.IsNullOrEmpty(state.LookupPath))
            pathIndex[state.LookupPath] = state;
    }

    private static DiffFileInfo? BuildDiffFileInfo(AggregatedFileState state)
    {
        var baseExists = !string.IsNullOrEmpty(state.BaseBlobId);
        var currentExists = !string.IsNullOrEmpty(state.CurrentBlobId);

        if (!baseExists && !currentExists)
            return null;

        if (!baseExists && currentExists)
        {
            return new DiffFileInfo
            {
                FilePath = state.CurrentPath ?? state.LookupPath,
                OldPath = state.SawCopy ? state.CopySourcePath : null,
                Status = state.SawCopy ? ChangeStatus.Copied : ChangeStatus.Added,
                SourceCommitHash = state.SourceCommitHash,
                BaseCommitHash = state.BaseCommitHash,
                AuthorName = state.AuthorName
            };
        }

        if (baseExists && !currentExists)
        {
            return new DiffFileInfo
            {
                FilePath = state.BasePath ?? state.LookupPath,
                Status = ChangeStatus.Deleted,
                SourceCommitHash = state.SourceCommitHash,
                BaseCommitHash = state.BaseCommitHash,
                AuthorName = state.AuthorName
            };
        }

        var samePath = string.Equals(state.BasePath, state.CurrentPath, StringComparison.OrdinalIgnoreCase);
        var sameBlob = string.Equals(state.BaseBlobId, state.CurrentBlobId, StringComparison.Ordinal);

        if (samePath && sameBlob)
            return null;

        if (!samePath)
        {
            return new DiffFileInfo
            {
                FilePath = state.CurrentPath ?? state.LookupPath,
                OldPath = state.BasePath,
                Status = ChangeStatus.Renamed,
                SourceCommitHash = state.SourceCommitHash,
                BaseCommitHash = state.BaseCommitHash,
                AuthorName = state.AuthorName
            };
        }

        return new DiffFileInfo
        {
            FilePath = state.CurrentPath ?? state.LookupPath,
            Status = ChangeStatus.Modified,
            SourceCommitHash = state.SourceCommitHash,
            BaseCommitHash = state.BaseCommitHash,
            AuthorName = state.AuthorName
        };
    }

    private static string? GetBlobId(Commit? commit, string path)
    {
        var entry = commit?[path];
        return entry?.Target.Id.Sha;
    }

    private static void SortCommitsForAggregation(Repository repo, List<(Commit commit, Commit? parent)> commits)
    {
        var ordered = commits
            .Select((item, index) => new OrderedCommit(item.commit, item.parent, index))
            .ToList();

        var edges = Enumerable.Range(0, ordered.Count)
            .Select(_ => new List<int>())
            .ToList();
        var indegree = new int[ordered.Count];

        for (var i = 0; i < ordered.Count; i++)
        {
            for (var j = i + 1; j < ordered.Count; j++)
            {
                var relation = CompareCommitOrder(repo, ordered[i].Commit, ordered[j].Commit);
                if (relation < 0)
                {
                    edges[i].Add(j);
                    indegree[j]++;
                }
                else if (relation > 0)
                {
                    edges[j].Add(i);
                    indegree[i]++;
                }
            }
        }

        var queue = new PriorityQueue<int, (DateTimeOffset When, int Index)>();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (indegree[i] == 0)
                queue.Enqueue(i, (ordered[i].Commit.Author.When, ordered[i].Index));
        }

        var result = new List<(Commit commit, Commit? parent)>(ordered.Count);
        while (queue.Count > 0)
        {
            var index = queue.Dequeue();
            result.Add((ordered[index].Commit, ordered[index].Parent));

            foreach (var next in edges[index])
            {
                indegree[next]--;
                if (indegree[next] == 0)
                    queue.Enqueue(next, (ordered[next].Commit.Author.When, ordered[next].Index));
            }
        }

        if (result.Count != commits.Count)
        {
            var remaining = Enumerable.Range(0, ordered.Count)
                .Where(i => indegree[i] > 0)
                .OrderBy(i => ordered[i].Commit.Author.When)
                .ThenBy(i => ordered[i].Index)
                .Select(i => (ordered[i].Commit, ordered[i].Parent));
            result.AddRange(remaining);
        }

        commits.Clear();
        commits.AddRange(result);
    }

    private static int CompareCommitOrder(Repository repo, Commit left, Commit right)
    {
        if (left.Id == right.Id)
            return 0;

        var mergeBase = repo.ObjectDatabase.FindMergeBase(left, right);
        if (mergeBase?.Id == left.Id)
            return -1;
        if (mergeBase?.Id == right.Id)
            return 1;
        return 0;
    }

    private static (Commit Base, Commit Target) NormalizeCommitOrder(Repository repo, Commit baseCommit, Commit targetCommit)
    {
        // Sort by ancestry, not date: rebase/cherry-pick/amend can reverse commit timestamps.
        var mergeBase = repo.ObjectDatabase.FindMergeBase(baseCommit, targetCommit);
        return mergeBase?.Id == targetCommit.Id
            ? (targetCommit, baseCommit)
            : (baseCommit, targetCommit);
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

    public string? GetRemoteUrl(string repoPath)
    {
        using var repo = new Repository(repoPath);
        var remote = repo.Network.Remotes["origin"];
        return remote?.Url;
    }

    private sealed class AggregatedFileState
    {
        public string? BasePath { get; init; }
        public string? BaseBlobId { get; init; }
        public string BaseCommitHash { get; init; } = string.Empty;
        public string? CurrentPath { get; set; }
        public string? CurrentBlobId { get; set; }
        public string LookupPath { get; set; } = string.Empty;
        public string SourceCommitHash { get; set; } = string.Empty;
        public string? AuthorName { get; set; }
        public bool SawCopy { get; set; }
        public string? CopySourcePath { get; set; }
    }

    private sealed record OrderedCommit(Commit Commit, Commit? Parent, int Index);
}
