using HarnessCli.UI;
using Serilog;
using SharpConsoleUI;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Drivers;

namespace HarnessCli;

internal static class Program
{
    /// <summary>
    /// Entry point: take the working folder from the command line, otherwise reuse the last
    /// saved one when it still exists, otherwise let the window ask for one (the folder
    /// picker is itself a window, so it can only open once the main loop runs). The window
    /// creates and owns one harness agent per working folder (more folders can be added
    /// later via «Сессия → Рабочая папка…»).
    /// </summary>
    private static int Main(string[] args)
    {
        if (args.Length > 0 && (args[0] is "--help" or "-h" or "/?"))
        {
            Console.WriteLine("HarnessCli — консольный ReAct-агент (SharpConsoleUI).");
            Console.WriteLine("Использование: HarnessCli [рабочая-папка]");
            Console.WriteLine("Без аргумента берётся последняя использованная папка, иначе спрашивается.");
            return 0;
        }

        // Rolling file log in logs\ next to the exe; every catch block reports here.
        // The console itself belongs to SharpConsoleUI, so nothing is written to stdout.
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

            // The app draws its own menu and status bar inside the window, so the window
            // system's built-in top/bottom desktop panels would only double them up.
            var windowSystem = new ConsoleWindowSystem(
                new NetConsoleDriver(RenderMode.Buffer),
                options: new ConsoleWindowSystemOptions(
                    ShowTopPanel: false,
                    ShowBottomPanel: false));

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                windowSystem.Shutdown(0);
            };

            var window = new MainWindow(windowSystem, ResolveWorkingFolder(args));
            int exitCode = windowSystem.Run();
            window.DisposeHostsAsync().AsTask().GetAwaiter().GetResult();

            return exitCode;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
            Console.Error.WriteLine($"HarnessCli: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// Working folder from the command line or the saved settings, or <see langword="null"/>
    /// when neither resolves — the window then asks for one.
    /// </summary>
    private static string? ResolveWorkingFolder(string[] args)
    {
        if (args.Length > 0 && Directory.Exists(args[0]))
        {
            return Path.GetFullPath(args[0]);
        }

        string? saved = AppSettings.LoadLastWorkingFolder();
        return saved is not null && Directory.Exists(saved) ? saved : null;
    }
}
