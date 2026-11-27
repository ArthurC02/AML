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

/// <summary>A clock that moves only when the test says so. Its timers (a deadline's CancellationTokenSource) fire in Advance.</summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }
    // The monotonic clock moves with Advance too (GetElapsedTime).
    public override long GetTimestamp() => GetUtcNow().UtcTicks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += by;
            due = [.. _timers.Where(t => t.Due <= _now)];
            _timers.RemoveAll(due.Contains);
        }
        foreach (var t in due) t.Fire();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    // ponytail: one-shot only (all CancellationTokenSource(delay, clock) needs); a period is ignored.
    private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset Due { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                clock._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan) return true;
                Due = clock._now + dueTime;
                clock._timers.Add(this);
            }
            if (dueTime <= TimeSpan.Zero) clock.Advance(TimeSpan.Zero);
            return true;
        }

        public void Dispose() => Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

public static class GoldenCaseFiles
{
    /// <summary>A Golden Case's .txt files by file name, as the upload API and the services take them.</summary>
    public static Dictionary<string, string> Read(string gc) =>
        Directory.GetFiles(GoldenCases.GoldenCaseTests.Folder(gc), "*.txt").ToDictionary(p => Path.GetFileName(p), File.ReadAllText);
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
