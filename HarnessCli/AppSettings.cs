using Serilog;
using System.Text.Json;

namespace HarnessCli;

/// <summary>
/// Tiny persistent user settings (currently only the last working folder),
/// stored in %LocalAppData%\HarnessCli\settings.json. All I/O is best-effort:
/// a missing or broken settings file simply falls back to the folder prompt.
/// </summary>
internal static class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HarnessCli",
        "settings.json");

    private sealed record Model(string LastWorkingFolder = "");

    /// <summary>Returns the last used working folder, or null when none was saved.</summary>
    public static string? LoadLastWorkingFolder()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var model = JsonSerializer.Deserialize<Model>(File.ReadAllText(FilePath));
                if (!string.IsNullOrWhiteSpace(model?.LastWorkingFolder))
                {
                    return model.LastWorkingFolder;
                }
            }
        }
        catch (Exception ex)
        {
            // Corrupt settings are not fatal — the folder picker takes over.
            Log.Warning(ex, "Failed to load settings from {Path}", FilePath);
        }

        return null;
    }

    public static void SaveLastWorkingFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Model(folder)));
        }
        catch (Exception ex)
        {
            // Saving is best-effort; the next launch just shows the picker again.
            Log.Warning(ex, "Failed to save settings to {Path}", FilePath);
        }
    }
}
