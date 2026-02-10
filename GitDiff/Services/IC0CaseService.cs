using GitDiff.Models;

namespace GitDiff.Services;

public interface IC0CaseService
{
    IReadOnlyList<C0TestCase> GenerateC0Cases(string fileContent, IReadOnlyList<int> changedLines);
}
