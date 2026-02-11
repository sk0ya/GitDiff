using GitDiff.Models;

namespace GitDiff.Services;

public interface ISettingsService
{
    AppSettings Load();
    void Save(AppSettings settings);
    void AddRecentRepository(string path);
}
