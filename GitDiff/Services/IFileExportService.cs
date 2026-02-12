using GitDiff.Models;

namespace GitDiff.Services;

public interface IFileExportService
{
    int ExportDiffFiles(string repoPath, string baseCommitHash, string targetCommitHash, IEnumerable<DiffFileInfo> diffFiles, string outputPath);
}
