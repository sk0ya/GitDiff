using System.IO;
using GitDiff.Models;

namespace GitDiff.Services;

public class FileExportService : IFileExportService
{
    private readonly IGitService _gitService;

    public FileExportService(IGitService gitService)
    {
        _gitService = gitService;
    }

    public int ExportDiffFiles(string repoPath, string baseCommitHash, string targetCommitHash, IEnumerable<DiffFileInfo> diffFiles, string outputPath)
    {
        int exportedCount = 0;
        var beforePath = Path.Combine(outputPath, "before");
        var afterPath = Path.Combine(outputPath, "after");

        foreach (var file in diffFiles)
        {
            var afterCommit = file.SourceCommitHash ?? targetCommitHash;
            var beforeCommit = file.BaseCommitHash ?? baseCommitHash;

            // Export to "after" folder (Added, Modified, Renamed, Copied)
            if (file.Status != ChangeStatus.Deleted)
            {
                var content = _gitService.GetFileContent(repoPath, afterCommit, file.FilePath);
                if (content != null)
                {
                    var destPath = Path.Combine(afterPath, file.FilePath.Replace('/', Path.DirectorySeparatorChar));
                    var destDir = Path.GetDirectoryName(destPath);
                    if (destDir != null)
                        Directory.CreateDirectory(destDir);

                    File.WriteAllBytes(destPath, content);
                    exportedCount++;
                }
            }

            // Export to "before" folder (Modified, Deleted, Renamed)
            if (file.Status is ChangeStatus.Modified or ChangeStatus.Deleted or ChangeStatus.Renamed)
            {
                var beforeFilePath = file.OldPath ?? file.FilePath;
                var content = _gitService.GetFileContent(repoPath, beforeCommit, beforeFilePath);
                if (content != null)
                {
                    var destPath = Path.Combine(beforePath, beforeFilePath.Replace('/', Path.DirectorySeparatorChar));
                    var destDir = Path.GetDirectoryName(destPath);
                    if (destDir != null)
                        Directory.CreateDirectory(destDir);

                    File.WriteAllBytes(destPath, content);
                    exportedCount++;
                }
            }
        }

        return exportedCount;
    }
}
