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
        updateSql = $"REPLACE INTO {quotedTableName} (Key, Value) VALUES (@Key, @Value)";
        deleteSql = $"DELETE FROM {quotedTableName} WHERE Key = @Key";

        InitializeDatabase();
    }

    //--------------------------------------------------------------------------------
    // Override
    //--------------------------------------------------------------------------------

    public override void Load()
    {
        var data = LoadData();

        lock (sync)
        {
            Data = data;
        }
    }

    //--------------------------------------------------------------------------------
    // Operator
    //--------------------------------------------------------------------------------

    public ValueTask UpdateAsync(string key, object? value) =>
        UpdateAsync(key, value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture));

    public async ValueTask UpdateAsync(string key, string? value)
    {
#pragma warning disable CA2007
        await using var con = new SqliteConnection(connectionString);
#pragma warning restore CA2007
        await con.OpenAsync().ConfigureAwait(false);

        await ExecuteUpdateAsync(con, null, key, value).ConfigureAwait(false);

        lock (sync)
        {
            Data[key] = value;
        }

        OnReload();
    }

    public ValueTask BulkUpdateAsync(params KeyValuePair<string, object?>[] source) =>
        BulkUpdateAsync((IEnumerable<KeyValuePair<string, object?>>)source);

    public async ValueTask BulkUpdateAsync(IEnumerable<KeyValuePair<string, object?>> source)
    {
#pragma warning disable CA2007
        await using var con = new SqliteConnection(connectionString);
#pragma warning restore CA2007
        await con.OpenAsync().ConfigureAwait(false);
#pragma warning disable CA2007
        await using var tx = await con.BeginTransactionAsync().ConfigureAwait(false);
#pragma warning restore CA2007

        var updated = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in source)
        {
            var stringValue = pair.Value is null ? null : Convert.ToString(pair.Value, CultureInfo.InvariantCulture);
            await ExecuteUpdateAsync(con, tx, pair.Key, stringValue).ConfigureAwait(false);
            updated[pair.Key] = stringValue;
        }

        await tx.CommitAsync().ConfigureAwait(false);

        lock (sync)
        {
            foreach (var pair in updated)
            {
                Data[pair.Key] = pair.Value;
            }
        }

        OnReload();
    }

    public async ValueTask DeleteAsync(string key)
    {
#pragma warning disable CA2007
        await using var con = new SqliteConnection(connectionString);
#pragma warning restore CA2007
        await con.OpenAsync().ConfigureAwait(false);

        await ExecuteDeleteAsync(con, null, key).ConfigureAwait(false);

        lock (sync)
        {
            Data.Remove(key);
        }

        OnReload();
    }

    public ValueTask BulkDeleteAsync(params string[] keys) =>
        BulkDeleteAsync((IEnumerable<string>)keys);

    public async ValueTask BulkDeleteAsync(IEnumerable<string> keys)
    {
#pragma warning disable CA2007
        await using var con = new SqliteConnection(connectionString);
#pragma warning restore CA2007
        await con.OpenAsync().ConfigureAwait(false);
#pragma warning disable CA2007
        await using var tx = await con.BeginTransactionAsync().ConfigureAwait(false);
#pragma warning restore CA2007

        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in keys)
        {
            await ExecuteDeleteAsync(con, tx, key).ConfigureAwait(false);
            _ = deleted.Add(key);
        }

        if (deleted.Count == 0)
        {
            return;
        }

        await tx.CommitAsync().ConfigureAwait(false);

        lock (sync)
        {
            foreach (var key in deleted)
            {
                Data.Remove(key);
            }
        }

        OnReload();
    }

    public async ValueTask ReloadAsync()
    {
        var data = await LoadDataAsync().ConfigureAwait(false);

        lock (sync)
        {
            Data = data;
        }

        OnReload();
    }

    //--------------------------------------------------------------------------------
    // Helper
    //--------------------------------------------------------------------------------

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

    private async ValueTask<Dictionary<string, string?>> LoadDataAsync()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

#pragma warning disable CA2007
        await using var con = new SqliteConnection(connectionString);
#pragma warning restore CA2007
        await con.OpenAsync().ConfigureAwait(false);

#pragma warning disable CA2007
        await using var cmd = con.CreateCommand();
#pragma warning restore CA2007
        cmd.CommandText = selectSql;

#pragma warning disable CA2007
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
#pragma warning restore CA2007
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
#pragma warning disable CA1849
            data[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
#pragma warning restore CA1849
        }

        return data;
    }

    private async ValueTask ExecuteUpdateAsync(DbConnection connection, DbTransaction? transaction, string key, string? value)
    {
#pragma warning disable CA2007
        await using var cmd = connection.CreateCommand();
#pragma warning restore CA2007
        cmd.CommandText = updateSql;
        cmd.Transaction = transaction;
        AddParameter(cmd, "Key", key);
        AddParameter(cmd, "Value", value);
        _ = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async ValueTask ExecuteDeleteAsync(DbConnection connection, DbTransaction? transaction, string key)
    {
#pragma warning disable CA2007
        await using var cmd = connection.CreateCommand();
#pragma warning restore CA2007
        cmd.CommandText = deleteSql;
        cmd.Transaction = transaction;
        AddParameter(cmd, "Key", key);
        _ = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
#pragma warning restore CA2100
