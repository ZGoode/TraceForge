using System.Text.Json;
using TraceForge.Application;

namespace TraceForge.Infrastructure;

/// <summary>Append-only JSON-lines diagnostic log. Failures to log are swallowed by design.</summary>
public sealed class FileDiagnosticLog(string path) : IDiagnosticLog
{
    private readonly object _gate = new();

    public void Write(string level, string eventName, string message, string? correlationId = null)
    {
        try
        {
            var line = JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow.ToString("O"), level, @event = eventName, correlationId, message = Redact(message) });
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch { /* diagnostics must never break project operations */ }
    }

    public static string Redact(string value)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) || home.Length < 2 ? value : value.Replace(home, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }
}
