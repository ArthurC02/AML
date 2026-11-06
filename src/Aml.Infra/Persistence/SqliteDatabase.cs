using Aml.Core.Cases;
using Dapper;
using DbUp;
using Microsoft.Data.Sqlite;

namespace Aml.Infra.Persistence;

/// <summary>
/// Connection factory and schema upgrade for SQLite.
/// ponytail: concrete SQLite only; extract IDbConnectionFactory / ISqlDialect when SQL Server or Oracle is chosen.
/// </summary>
public sealed class SqliteDatabase
{
    /// <summary>SQLite extended result codes: a UNIQUE or a PRIMARY KEY constraint failed.</summary>
    public const int ConstraintUnique = 2067;
    public const int ConstraintPrimaryKey = 1555;

    private readonly string _connectionString;

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
        await c.ExecuteAsync("PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;").ConfigureAwait(false);
        return c;
    }

    /// <summary>
    /// Creates or upgrades the schema in one transaction. The schema script records the format as <c>PRAGMA user_version</c>,
    /// which must equal <see cref="CaseJson.SchemaVersion"/>;
    /// a database written by another format is refused, never read (rebuild it: operations runbook).
    /// </summary>
    public void Upgrade()
    {
        using (var existing = new SqliteConnection(_connectionString))
        {
            existing.Open();
            var version = existing.ExecuteScalar<long>("PRAGMA user_version;");
            var used = existing.ExecuteScalar<long>("SELECT count(*) FROM sqlite_master WHERE type = 'table';") > 0;
            if (used && version != CaseJson.SchemaVersion)
                throw new InvalidOperationException(
                    $"The database holds format {version}; this release reads only format {CaseJson.SchemaVersion}. Delete the database file with its -wal and -shm files and restart.");
        }

        var result = DeployChanges.To.SqliteDatabase(_connectionString)
            .WithScriptsEmbeddedInAssembly(typeof(SqliteDatabase).Assembly, n => n.Contains(".Scripts.Sqlite.", StringComparison.Ordinal))
            .WithTransaction()
            .LogToNowhere()
            .Build()
            .PerformUpgrade();
        if (!result.Successful) throw new InvalidOperationException("Database upgrade failed", result.Error);

        // Write-ahead log: readers do not wait for the writer. The mode is stored in the file, so setting it once is enough.
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        c.Execute("PRAGMA journal_mode = WAL;");
    }
}
