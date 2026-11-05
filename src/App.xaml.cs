using System.IO;
using System.Windows.Threading;

namespace TraceForge.Desktop;

// Fully qualified: inside this namespace "Application" would otherwise resolve to the TraceForge.Application namespace.
public partial class App : System.Windows.Application
{
    public App()
    {
        // A desktop app started with `dotnet run` has no console, so a crash at start-up would otherwise just vanish.
        // Show the error and keep a copy in %LocalAppData%\TraceForge\crash.log.
        DispatcherUnhandledException += (_, e) => { Report(e.Exception); e.Handled = true; Shutdown(1); };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Report(e.Exception); e.SetObserved(); };
    }

    private static void Report(Exception? ex)
    {
        var text = ex?.ToString() ?? "Unknown error";
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TraceForge");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), $"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{text}\n\n");
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { /* showing the message is what matters */ }
        System.Windows.MessageBox.Show(text, "TraceForge stopped unexpectedly", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
    }
}
