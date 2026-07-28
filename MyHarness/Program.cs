using Harness.Core;
using MyHarnessWin.UI;
using Serilog;

namespace MyHarnessWin;

internal static class Program
{
    /// <summary>
    /// Entry point: reuse the last saved working folder when it still exists, otherwise
    /// ask for one (folder-picker dialog), then run the chat window. The window creates
    /// and owns one harness agent per working folder (more folders can be added later
    /// via the "📁 Папка…" button).
    /// </summary>
    [STAThread]
    private static void Main()
    {
        // Rolling file log in logs\ next to the exe; every catch block reports here.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(AppContext.BaseDirectory, "logs", "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30)
            .CreateLogger();

        try
        {
            Log.Information("Application starting");
            ApplicationConfiguration.Initialize();

            string? folder = AppSettings.LoadLastWorkingFolder();
            if (folder is null || !Directory.Exists(folder))
            {
                using var folderDialog = new FolderBrowserDialog
                {
                    Description = "Выберите рабочую папку агента (file_access, поиск и команды будут работать в ней)",
                    UseDescriptionForTitle = true,
                    ShowNewFolderButton = true,
                    SelectedPath = folder ?? string.Empty,
                };

                if (folderDialog.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(folderDialog.SelectedPath))
                {
                    return; // The working folder is mandatory — without it the agent has nothing to operate on.
                }

                folder = folderDialog.SelectedPath;
            }

            Application.Run(new MainForm(folder));
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
