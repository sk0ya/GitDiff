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

    public int ExportDiffFiles(string repoPath, string commitHash, IEnumerable<DiffFileInfo> diffFiles, string outputPath)
    {
        int exportedCount = 0;

        foreach (var file in diffFiles)
        {
            if (file.Status == ChangeStatus.Deleted)
                continue;

            var content = _gitService.GetFileContent(repoPath, file.SourceCommitHash ?? commitHash, file.FilePath);
            if (content == null)
                continue;

            var destPath = Path.Combine(outputPath, file.FilePath.Replace('/', Path.DirectorySeparatorChar));
            var destDir = Path.GetDirectoryName(destPath);
            if (destDir != null)
                Directory.CreateDirectory(destDir);

            File.WriteAllBytes(destPath, content);
            exportedCount++;
        }

        return exportedCount;
    }
}
