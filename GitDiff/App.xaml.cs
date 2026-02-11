using System.Windows;

namespace GitDiff;

public partial class App : Application
{
    internal static string? StartupRepositoryPath { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length > 0 && !string.IsNullOrWhiteSpace(e.Args[0]))
        {
            StartupRepositoryPath = e.Args[0];
        }
    }
}
