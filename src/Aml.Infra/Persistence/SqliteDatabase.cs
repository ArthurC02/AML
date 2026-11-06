using System.Collections.Concurrent;
using Aml.Core.Cases;
using Dapper;
using DbUp;
using Microsoft.Data.Sqlite;

namespace Aml.Infra.Persistence;

/// <summary>ponytail: concrete SQLite only; extract IDbConnectionFactory / ISqlDialect when SQL Server or Oracle is chosen.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The writer slot lives as long as the database and never creates a wait handle.")]
public sealed class SqliteDatabase
{
    public const int SqliteUniqueViolation = 2067;
    public const int SqlitePrimaryKeyViolation = 1555;

    private const string WriteSavepoint = "write";

    private readonly string _connectionString;
    private readonly ConcurrentQueue<PendingWrite> _pending = new();
    private readonly SemaphoreSlim _writer = new(1, 1);

    public SqliteDatabase(string connectionString)
    {
        _connectionString = connectionString;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public SqlCatalog Sql { get; } = SqlCatalog.Load("sqlite");

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var c = new SqliteConnection(_connectionString);
        await c.OpenAsync(ct).ConfigureAwait(false);
        // temp_store = MEMORY: a write's savepoint keeps a sub-journal of the pages it changes; past 64 KiB SQLite otherwise
        // spills it to a temporary file. It holds no durable data and is freed when the write's savepoint is released.
        await c.ExecuteAsync("PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA temp_store = MEMORY;").ConfigureAwait(false);
        return c;
    }

    /// <summary>
    /// Runs <paramref name="write"/> as its own savepoint inside a shared transaction and returns once that transaction has
    /// committed: group commit. Writes that arrive while another batch commits wait and then commit together, so concurrent
    /// jobs share one log sync instead of paying one each. The batching follows the LMAX Disruptor's smart batching under a
    /// single writer: whoever holds the writer slot drains every pending write, so batches grow with load and add no delay
    /// when idle. Each write sees the writes queued before it and is undone alone if it throws; an error after which SQLite
    /// rolls back the whole transaction, or a failed commit, fails every write of its batch with that error. A write must not call back into this method.
    /// </summary>
    public async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> write, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        T result = default!;
        var pending = new PendingWrite(async (c, tx) => result = await write(c, tx).ConfigureAwait(false));
        _pending.Enqueue(pending);
        // No token: a queued write runs in some batch anyway, so its caller waits for that outcome.
        await _writer.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!pending.Done.Task.IsCompleted) await CommitPendingAsync().ConfigureAwait(false);
        }
        finally
        {
            _writer.Release();
        }
        await pending.Done.Task.ConfigureAwait(false);
        return result;
    }

    private sealed class PendingWrite(Func<SqliteConnection, SqliteTransaction, Task> write)
    {
        public Func<SqliteConnection, SqliteTransaction, Task> Write { get; } = write;

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task CommitPendingAsync()
    {
        var batch = new List<PendingWrite>();
        while (_pending.TryDequeue(out var next)) batch.Add(next);
        var failures = new Exception?[batch.Count];
        try
        {
            await using var c = await OpenAsync(CancellationToken.None).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);
            for (var i = 0; i < batch.Count; i++)
            {
                tx.Save(WriteSavepoint);
                try
                {
                    await batch[i].Write(c, tx).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // the exception belongs to that write's caller, not to the batch
                catch (Exception e)
#pragma warning restore CA1031
                {
                    failures[i] = e;
                    try
                    {
                        tx.Rollback(WriteSavepoint);
                    }
                    catch (SqliteException)
                    {
                        // SQLite already rolled the whole transaction back (a full disk, an I/O error): the batch fails with the cause.
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(e);
                    }
                }
                tx.Release(WriteSavepoint);
            }
            await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // nothing of the batch was stored: every write fails with the cause
        catch (Exception e)
#pragma warning restore CA1031
        {
            foreach (var w in batch) w.Done.TrySetException(e);
            return;
        }
        for (var i = 0; i < batch.Count; i++)
            if (failures[i] is { } e) batch[i].Done.TrySetException(e);
            else batch[i].Done.TrySetResult();
    }

    /// <summary>Creates or upgrades the schema in one transaction (format rebuild: operations runbook).</summary>
    public void Upgrade()
    {
        RefuseDatabaseOfAnotherFormat();
        var result = DeployChanges.To.SqliteDatabase(_connectionString)
            .WithScriptsEmbeddedInAssembly(typeof(SqliteDatabase).Assembly, n => n.Contains(".Scripts.Sqlite.", StringComparison.Ordinal))
            .WithTransaction()
            .LogToNowhere()
            .Build()
            .PerformUpgrade();
        if (!result.Successful) throw new InvalidOperationException("Database upgrade failed", result.Error);
        EnableWriteAheadLog();
    }

    // The schema script records the format as PRAGMA user_version.
    private void RefuseDatabaseOfAnotherFormat()
    {
        using var existing = new SqliteConnection(_connectionString);
        existing.Open();
        var version = existing.ExecuteScalar<long>("PRAGMA user_version;");
        var used = existing.ExecuteScalar<long>("SELECT count(*) FROM sqlite_master WHERE type = 'table';") > 0;
        if (used && version != CaseJson.SchemaVersion)
            throw new InvalidOperationException(
                $"The database holds format {version}; this release reads only format {CaseJson.SchemaVersion}. Delete the database file with its -wal and -shm files and restart.");
    }

    // Readers do not wait for the writer; the mode is stored in the file, so setting it once is enough.
    private void EnableWriteAheadLog()
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        c.Execute("PRAGMA journal_mode = WAL;");
    }
}
