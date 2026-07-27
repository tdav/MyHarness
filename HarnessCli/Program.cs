using HarnessCli.UI;
using Serilog;
using Terminal.Gui.App;

namespace HarnessCli;

internal static class Program
{
    /// <summary>
    /// Entry point: take the working folder from the command line, otherwise reuse the last
    /// saved one when it still exists, otherwise ask for one (Terminal.Gui directory
    /// picker), then run the chat window. The window creates and owns one harness agent per
    /// working folder (more folders can be added later via «Сессия → Рабочая папка…»).
    /// </summary>
    private static int Main(string[] args)
    {
        if (args.Length > 0 && (args[0] is "--help" or "-h" or "/?"))
        {
            Console.WriteLine("HarnessCli — консольный ReAct-агент (Terminal.Gui).");
            Console.WriteLine("Использование: HarnessCli [рабочая-папка]");
            Console.WriteLine("Без аргумента берётся последняя использованная папка, иначе спрашивается.");
            Console.WriteLine("  --selfcheck   проверить рендеринг Markdown и показать цветовую схему");
            return 0;
        }

        if (args.Length > 0 && args[0] == "--selfcheck")
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            return RenderSelfCheck.Run();
        }

        // Rolling file log in logs\ next to the exe; every catch block reports here.
        // The console itself belongs to Terminal.Gui, so nothing is written to stdout.
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

            using IApplication app = Application.Create().Init();
            Theme.Register();

            string? folder = ResolveWorkingFolder(app, args);
            if (folder is null)
            {
                return 1; // The working folder is mandatory — without it the agent has nothing to operate on.
            }

            var window = new MainWindow(app, folder);
            try
            {
                app.Run(window);
                window.DisposeHostsAsync().AsTask().GetAwaiter().GetResult();
            }
            finally
            {
                window.Dispose();
            }

            return 0;
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

    private static string? ResolveWorkingFolder(IApplication app, string[] args)
    {
        if (args.Length > 0 && Directory.Exists(args[0]))
        {
            return Path.GetFullPath(args[0]);
        }

        string? saved = AppSettings.LoadLastWorkingFolder();
        if (saved is not null && Directory.Exists(saved))
        {
            return saved;
        }

        return Dialogs.PickFolder(
            app,
            "Выберите рабочую папку агента (file_access, поиск и команды будут работать в ней)",
            saved);
    }
}
