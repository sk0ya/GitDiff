using GitDiff.Models;

namespace GitDiff.Services;

public interface IFileExportService
{
    int ExportDiffFiles(string repoPath, string commitHash, IEnumerable<DiffFileInfo> diffFiles, string outputPath);
}
