namespace Mofucat.SqliteConfiguration;

using System.Data.Common;
using System.Globalization;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

#pragma warning disable CA2100
internal sealed class SqliteConfigurationProvider : ConfigurationProvider, IConfigurationOperator
{
#if NET9_0_OR_GREATER
    private readonly Lock sync = new();
#else
    private readonly object sync = new();
#endif

    private readonly string connectionString;

    private readonly string quotedTableName;

    private readonly string selectSql;
    private readonly string updateSql;
    private readonly string deleteSql;

    //--------------------------------------------------------------------------------
    // Constructor
    //--------------------------------------------------------------------------------

    public SqliteConfigurationProvider(SqliteConfigurationOptions options)
    {
        quotedTableName = $"\"{options.Table.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = options.Path,
            Pooling = true,
            Cache = SqliteCacheMode.Shared
        };
        connectionString = builder.ConnectionString;

        selectSql = $"SELECT Key, Value FROM {quotedTableName} ORDER BY Key";
        updateSql = $"INSERT INTO {quotedTableName} (Key, Value) VALUES (@Key, @Value) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value";
        deleteSql = $"DELETE FROM {quotedTableName} WHERE Key = @Key";
    }

    //--------------------------------------------------------------------------------
    // Override
    //--------------------------------------------------------------------------------

    public override void Load()
    {
        InitializeDatabase();

        var data = LoadData();

        lock (sync)
        {
            Data = data;
        }
    }

    //--------------------------------------------------------------------------------
    // Operator
    //--------------------------------------------------------------------------------

    public ValueTask UpdateAsync(string key, object? value, CancellationToken cancel = default) =>
        UpdateAsync(key, ConvertValue(value), cancel);

    public async ValueTask UpdateAsync(string key, string? value, CancellationToken cancel = default)
    {
#pragma warning disable CA2007
        await using var con = new SqliteConnection(connectionString);
#pragma warning restore CA2007
        await con.OpenAsync(cancel).ConfigureAwait(false);

        await ExecuteUpdateAsync(con, null, key, value, cancel).ConfigureAwait(false);

        lock (sync)
        {
            Data[key] = value;
        }

        OnReload();
    }

    public ValueTask BulkUpdateAsync(params KeyValuePair<string, object?>[] source) =>
        BulkUpdateAsync((IEnumerable<KeyValuePair<string, object?>>)source);

    public async ValueTask BulkUpdateAsync(IEnumerable<KeyValuePair<string, object?>> source, CancellationToken cancel = default)
    {
#pragma warning disable CA2007
        await using var con = new SqliteConnection(connectionString);
#pragma warning restore CA2007
        await con.OpenAsync(cancel).ConfigureAwait(false);
#pragma warning disable CA2007
        await using var tx = await con.BeginTransactionAsync(cancel).ConfigureAwait(false);
#pragma warning restore CA2007

        var applied = new List<KeyValuePair<string, string?>>();
        foreach (var pair in source)
        {
            var value = ConvertValue(pair.Value);
            await ExecuteUpdateAsync(con, tx, pair.Key, value, cancel).ConfigureAwait(false);
            applied.Add(new(pair.Key, value));
        }

        await tx.CommitAsync(cancel).ConfigureAwait(false);

        lock (sync)
        {
            foreach (var pair in applied)
            {
                Data[pair.Key] = pair.Value;
            }
        }

        OnReload();
    }

    public async ValueTask DeleteAsync(string key, CancellationToken cancel = default)
    {
#pragma warning disable CA2007
        await using var con = new SqliteConnection(connectionString);
#pragma warning restore CA2007
        await con.OpenAsync(cancel).ConfigureAwait(false);

        await ExecuteDeleteAsync(con, null, key, cancel).ConfigureAwait(false);

        lock (sync)
        {
            Data.Remove(key);
        }

        OnReload();
    }

    public ValueTask BulkDeleteAsync(params string[] keys) =>
        BulkDeleteAsync((IEnumerable<string>)keys);

    public async ValueTask BulkDeleteAsync(IEnumerable<string> keys, CancellationToken cancel = default)
    {
#pragma warning disable CA2007
        await using var con = new SqliteConnection(connectionString);
#pragma warning restore CA2007
        await con.OpenAsync(cancel).ConfigureAwait(false);
#pragma warning disable CA2007
        await using var tx = await con.BeginTransactionAsync(cancel).ConfigureAwait(false);
#pragma warning restore CA2007

        var applied = new List<string>();
        foreach (var key in keys)
        {
            await ExecuteDeleteAsync(con, tx, key, cancel).ConfigureAwait(false);
            applied.Add(key);
        }

        await tx.CommitAsync(cancel).ConfigureAwait(false);

        lock (sync)
        {
            foreach (var key in applied)
            {
                Data.Remove(key);
            }
        }

        OnReload();
    }

    public async ValueTask ReloadAsync(CancellationToken cancel = default)
    {
        var data = await LoadDataAsync(cancel).ConfigureAwait(false);

        lock (sync)
        {
            Data = data;
        }

        OnReload();
    }

    //--------------------------------------------------------------------------------
    // Helper
    //--------------------------------------------------------------------------------

    private static string? ConvertValue(object? value) =>
        value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        _ = command.Parameters.Add(parameter);
    }

    private void InitializeDatabase()
    {
        using var con = new SqliteConnection(connectionString);
        con.Open();

        using var cmd = con.CreateCommand();
        cmd.CommandText = $"CREATE TABLE IF NOT EXISTS {quotedTableName} (Key TEXT NOT NULL COLLATE NOCASE PRIMARY KEY, Value TEXT)";
        _ = cmd.ExecuteNonQuery();
    }

    private Dictionary<string, string?> LoadData()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        using var con = new SqliteConnection(connectionString);
        con.Open();

        using var cmd = con.CreateCommand();
        cmd.CommandText = selectSql;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            data[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        return data;
    }

    private async ValueTask<Dictionary<string, string?>> LoadDataAsync(CancellationToken cancel)
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

#pragma warning disable CA2007
        await using var con = new SqliteConnection(connectionString);
#pragma warning restore CA2007
        await con.OpenAsync(cancel).ConfigureAwait(false);

#pragma warning disable CA2007
        await using var cmd = con.CreateCommand();
#pragma warning restore CA2007
        cmd.CommandText = selectSql;

#pragma warning disable CA2007
        await using var reader = await cmd.ExecuteReaderAsync(cancel).ConfigureAwait(false);
#pragma warning restore CA2007
        while (await reader.ReadAsync(cancel).ConfigureAwait(false))
        {
#pragma warning disable CA1849
            data[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
#pragma warning restore CA1849
        }

        return data;
    }

    private async ValueTask ExecuteUpdateAsync(DbConnection connection, DbTransaction? transaction, string key, string? value, CancellationToken cancel)
    {
#pragma warning disable CA2007
        await using var cmd = connection.CreateCommand();
#pragma warning restore CA2007
        cmd.CommandText = updateSql;
        cmd.Transaction = transaction;
        AddParameter(cmd, "Key", key);
        AddParameter(cmd, "Value", value);
        _ = await cmd.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
    }

    private async ValueTask ExecuteDeleteAsync(DbConnection connection, DbTransaction? transaction, string key, CancellationToken cancel)
    {
#pragma warning disable CA2007
        await using var cmd = connection.CreateCommand();
#pragma warning restore CA2007
        cmd.CommandText = deleteSql;
        cmd.Transaction = transaction;
        AddParameter(cmd, "Key", key);
        _ = await cmd.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);
    }
}
#pragma warning restore CA2100
