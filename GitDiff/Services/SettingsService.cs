using System.IO;
using System.Text.Json;
using GitDiff.Models;

namespace GitDiff.Services;

public class SettingsService : ISettingsService
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GitDiff");

    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    private const int MaxRecentRepositories = 10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public AppSettings Load()
    {
        if (!File.Exists(SettingsFile))
            return new AppSettings();

        try
        {
            var json = File.ReadAllText(SettingsFile);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDir);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsFile, json);
    }

    public void AddRecentRepository(string path)
    {
        var settings = Load();

        settings.RecentRepositories.RemoveAll(p =>
            string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

        settings.RecentRepositories.Insert(0, path);

        if (settings.RecentRepositories.Count > MaxRecentRepositories)
            settings.RecentRepositories.RemoveRange(MaxRecentRepositories,
                settings.RecentRepositories.Count - MaxRecentRepositories);

        settings.LastRepositoryPath = path;
        Save(settings);
    }
}
