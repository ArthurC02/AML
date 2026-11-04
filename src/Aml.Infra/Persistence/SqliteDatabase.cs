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

    public void Upgrade()
    {
        var result = DeployChanges.To.SqliteDatabase(_connectionString)
            .WithScriptsEmbeddedInAssembly(typeof(SqliteDatabase).Assembly, n => n.Contains(".Scripts.Sqlite.", StringComparison.Ordinal))
            .LogToNowhere()
            .Build()
            .PerformUpgrade();
        if (!result.Successful) throw new InvalidOperationException("Database upgrade failed", result.Error);

        // Write-ahead log: readers no longer wait for the writer. The mode is stored in the file, so setting it once is enough.
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        c.Execute("PRAGMA journal_mode = WAL;");
    }
}
