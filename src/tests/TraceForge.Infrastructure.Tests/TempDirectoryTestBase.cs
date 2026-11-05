using Microsoft.Data.Sqlite;
using Xunit;

namespace TraceForge.Infrastructure.Tests;

/// <summary>Gives each test class its own empty temporary directory (<c>_root</c>), deleted after the tests run.</summary>
public abstract class TempDirectoryTestBase : IAsyncLifetime
{
    protected readonly string _root = Path.Combine(Path.GetTempPath(), "traceforge-tests", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() { Directory.CreateDirectory(_root); return Task.CompletedTask; }
    public Task DisposeAsync() { if (Directory.Exists(_root)) Directory.Delete(_root, true); return Task.CompletedTask; }

    /// <summary>Runs SQL directly against a project database, bypassing the store (to simulate tampering or damage).</summary>
    protected static async Task RawAsync(string dbPath, params string[] statements)
    {
        await using var raw = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await raw.OpenAsync();
        foreach (var sql in statements) { await using var cmd = raw.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(); }
    }
}
