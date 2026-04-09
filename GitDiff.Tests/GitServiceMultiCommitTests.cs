using GitDiff.Models;
using GitDiff.Services;
using LibGit2Sharp;

namespace GitDiff.Tests;

public sealed class GitServiceMultiCommitTests
{
    private readonly GitService _service = new();

    [Fact]
    public void GetDiffFilesForCommits_DeleteThenReAddSamePath_ReturnsModified()
    {
        using var repo = new TestRepository();

        var initial = repo.Commit(
            new Dictionary<string, string?> { ["sample.txt"] = "line1\nline2\n" },
            "initial",
            At(2026, 4, 10, 10, 0));
        var deleted = repo.Commit(
            new Dictionary<string, string?> { ["sample.txt"] = null },
            "delete sample",
            At(2026, 4, 10, 11, 0));
        var readded = repo.Commit(
            new Dictionary<string, string?> { ["sample.txt"] = "new1\nnew2\n" },
            "readd sample",
            At(2026, 4, 10, 12, 0));

        var (files, notFoundHashes) = _service.GetDiffFilesForCommits(repo.RepoPath, [deleted, readded]);

        Assert.Empty(notFoundHashes);
        var file = Assert.Single(files);
        Assert.Equal(ChangeStatus.Modified, file.Status);
        Assert.Equal("sample.txt", file.FilePath);
        Assert.Equal(initial, file.BaseCommitHash);
        Assert.Equal(readded, file.SourceCommitHash);
    }

    [Fact]
    public void GetDiffFilesForCommits_MultipleCommitsWithDeleteAndAddInSameRegion_PreservesBothSides()
    {
        using var repo = new TestRepository();

        var initial = repo.Commit(
            new Dictionary<string, string?> { ["sample.txt"] = "line1\nline2\nline3\nline4\n" },
            "initial",
            At(2026, 4, 10, 10, 0));
        var first = repo.Commit(
            new Dictionary<string, string?> { ["sample.txt"] = "line1\nline2-mod\nline3\nline4\n" },
            "modify line2",
            At(2026, 4, 10, 11, 0));
        var second = repo.Commit(
            new Dictionary<string, string?> { ["sample.txt"] = "line1\nline2-mod\nline4\n" },
            "delete line3",
            At(2026, 4, 10, 12, 0));

        var (files, notFoundHashes) = _service.GetDiffFilesForCommits(repo.RepoPath, [first, second]);

        Assert.Empty(notFoundHashes);
        var file = Assert.Single(files);
        Assert.Equal(ChangeStatus.Modified, file.Status);
        Assert.Equal(initial, file.BaseCommitHash);
        Assert.Equal(second, file.SourceCommitHash);

        var (oldLines, newLines) = _service.GetChangedLineNumbersDetailed(
            repo.RepoPath,
            file.BaseCommitHash!,
            file.SourceCommitHash!,
            file.FilePath);

        Assert.Equal([2, 3], oldLines);
        Assert.Equal([2], newLines);

        var diff = _service.GetFileDiff(repo.RepoPath, file.BaseCommitHash!, file.SourceCommitHash!, file.FilePath);
        Assert.Contains("-line2", diff.RawPatch);
        Assert.Contains("-line3", diff.RawPatch);
        Assert.Contains("+line2-mod", diff.RawPatch);
    }

    [Fact]
    public void GetDiffFilesForCommits_UsesAncestryOrderWhenCommitTimestampsAreReversed()
    {
        using var repo = new TestRepository();

        var initial = repo.Commit(
            new Dictionary<string, string?> { ["sample.txt"] = "line1\nline2\nline3\nline4\n" },
            "initial",
            At(2026, 4, 10, 10, 0));
        var first = repo.Commit(
            new Dictionary<string, string?> { ["sample.txt"] = "line1\nline2-mod\nline3\nline4\n" },
            "modify line2",
            At(2026, 4, 10, 11, 0));
        var second = repo.Commit(
            new Dictionary<string, string?> { ["sample.txt"] = "line1\nline2-mod\nline4\n" },
            "delete line3",
            At(2026, 4, 10, 9, 0));

        var (files, _) = _service.GetDiffFilesForCommits(repo.RepoPath, [first, second]);

        var file = Assert.Single(files);
        Assert.Equal(ChangeStatus.Modified, file.Status);
        Assert.Equal(initial, file.BaseCommitHash);
        Assert.Equal(second, file.SourceCommitHash);

        var (oldLines, newLines) = _service.GetChangedLineNumbersDetailed(
            repo.RepoPath,
            file.BaseCommitHash!,
            file.SourceCommitHash!,
            file.FilePath);

        Assert.Equal([2, 3], oldLines);
        Assert.Equal([2], newLines);
    }

    private static DateTimeOffset At(int year, int month, int day, int hour, int minute)
    {
        return new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.FromHours(9));
    }

    private sealed class TestRepository : IDisposable
    {
        private readonly Repository _repository;

        public TestRepository()
        {
            RepoPath = Path.Combine(Path.GetTempPath(), $"gitdiff-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(RepoPath);
            Repository.Init(RepoPath);
            _repository = new Repository(RepoPath);
        }

        public string RepoPath { get; }

        public string Commit(IReadOnlyDictionary<string, string?> files, string message, DateTimeOffset when)
        {
            foreach (var (path, content) in files)
            {
                var fullPath = Path.Combine(RepoPath, path.Replace('/', Path.DirectorySeparatorChar));
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                if (content == null)
                {
                    if (File.Exists(fullPath))
                        File.Delete(fullPath);
                }
                else
                {
                    File.WriteAllText(fullPath, content);
                }

                Commands.Stage(_repository, path);
            }

            var signature = new Signature("Test User", "test@example.com", when);
            return _repository.Commit(message, signature, signature).Sha;
        }

        public void Dispose()
        {
            _repository.Dispose();
            if (Directory.Exists(RepoPath))
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(RepoPath, "*", SearchOption.AllDirectories))
                    File.SetAttributes(path, FileAttributes.Normal);

                Directory.Delete(RepoPath, recursive: true);
            }
        }
    }
}
