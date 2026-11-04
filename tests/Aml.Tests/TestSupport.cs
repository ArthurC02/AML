using Aml.Infra.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Aml.Tests;

/// <summary>
/// One SQLite file per test. The schema is migrated once into a template and copied, so a test pays for a file copy,
/// not for the migrations. Every test still gets its own file.
/// </summary>
public sealed class TestDatabase : IDisposable
{
    private static readonly Lazy<string> Template = new(() =>
    {
        var path = NewPath();
        new SqliteDatabase(ConnectionStringFor(path)).Upgrade();
        using (var c = new SqliteConnection(ConnectionStringFor(path))) SqliteConnection.ClearPool(c);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => File.Delete(path);
        return path;
    });

    public TestDatabase()
    {
        File.Copy(Template.Value, Path);
        Db = new SqliteDatabase(ConnectionString);
    }

    public string Path { get; } = NewPath();
    public string ConnectionString => ConnectionStringFor(Path);
    public SqliteDatabase Db { get; }

    public static string ConnectionStringFor(string path) => $"Data Source={path}";

    public static string NewPath() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"aml-test-{Guid.NewGuid():N}.db");

    /// <summary>Closes this file's pooled connections so the file can be deleted.</summary>
    public void Dispose()
    {
        using (var c = new SqliteConnection(ConnectionString)) SqliteConnection.ClearPool(c);
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(Path + suffix);
    }
}

/// <summary>The API on its own database, shared by the tests of one class; the job worker polls every 20 ms; only warnings are logged.</summary>
public sealed class ApiHost : WebApplicationFactory<Program>
{
    private readonly TestDatabase _db = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Database:ConnectionString", _db.ConnectionString);
        builder.UseSetting("Batch:PollInterval", "00:00:00.020");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _db.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _db.Dispose();
    }
}

/// <summary>A clock that moves only when the test says so.</summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

public static class Wait
{
    /// <summary>Polls until <paramref name="done"/> holds; fails with <paramref name="what"/> instead of timing out silently.</summary>
    public static async Task<T> UntilAsync<T>(Func<Task<T>> read, Func<T, bool> done, string what, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var value = await read();
            if (done(value)) return value;
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting for {what}");
            await Task.Delay(20, ct);
        }
    }
}
