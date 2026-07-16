namespace Mofucat.SqliteConfiguration.Tests;

public sealed class SqliteConfigurationProviderTests
{
    [Fact]
    public void NewTableUsesCaseInsensitivePrimaryKey()
    {
        using var db = new TemporarySqliteDatabase();
        using var root = CreateConfiguration(db.Path);

        using var con = OpenConnection(db.Path);
        InsertRow(con, "MyKey", "1");

        var exception = Assert.Throws<SqliteException>(() => InsertRow(con, "myKey", "2"));

        Assert.Contains("UNIQUE", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateAndDeleteAsyncUpdatesDatabaseAndConfiguration()
    {
        using var db = new TemporarySqliteDatabase();
        using var root = CreateConfiguration(db.Path);
        var configurationOperator = root.GetConfigurationOperator();
        var cancel = TestContext.Current.CancellationToken;

        await configurationOperator.UpdateAsync("Dynamic:Value1", "Updated", cancel);

        Assert.Equal("Updated", root["dynamic:value1"]);
        Assert.Equal("Updated", GetValue(db.Path, "Dynamic:Value1"));

        await configurationOperator.DeleteAsync("Dynamic:Value1", cancel);

        Assert.Null(root["Dynamic:Value1"]);
        Assert.Null(GetValue(db.Path, "Dynamic:Value1"));
    }

    [Fact]
    public async Task BulkUpdateAndDeleteAsyncUpdatesDatabaseAndConfiguration()
    {
        using var db = new TemporarySqliteDatabase();
        using var root = CreateConfiguration(db.Path);
        var configurationOperator = root.GetConfigurationOperator();

        await configurationOperator.BulkUpdateAsync(
            new KeyValuePair<string, object?>("Feature:A", true),
            new KeyValuePair<string, object?>("Feature:B", false));

        Assert.Equal("True", root["Feature:A"]);
        Assert.Equal("False", root["Feature:B"]);

        await configurationOperator.BulkDeleteAsync("Feature:A", "Feature:B");

        Assert.Null(root["Feature:A"]);
        Assert.Null(root["Feature:B"]);
    }

    [Fact]
    public async Task BulkUpdateAndDeleteAsyncAcceptsLazy()
    {
        using var db = new TemporarySqliteDatabase();
        using var root = CreateConfiguration(db.Path);
        var configurationOperator = root.GetConfigurationOperator();
        var cancel = TestContext.Current.CancellationToken;

        await configurationOperator.BulkUpdateAsync(CreateEntries(), cancel);

        Assert.Equal("1", root["Lazy:A"]);
        Assert.Equal("2", root["Lazy:B"]);

        await configurationOperator.BulkDeleteAsync(CreateKeys(), cancel);

        Assert.Null(root["Lazy:A"]);
        Assert.Null(root["Lazy:B"]);
    }

    [Fact]
    public async Task ReloadAsyncReflectsExternalDatabaseChanges()
    {
        using var db = new TemporarySqliteDatabase();
        using var root = CreateConfiguration(db.Path);
        var configurationOperator = root.GetConfigurationOperator();
        var cancel = TestContext.Current.CancellationToken;

        await using (var con = OpenConnection(db.Path))
        {
            InsertRow(con, "External:Value", "42");
        }

        Assert.Null(root["External:Value"]);

        await configurationOperator.ReloadAsync(cancel);

        Assert.Equal("42", root["External:Value"]);
    }

    [Fact]
    public async Task ConcurrentReadAndUpdateDoesNotThrow()
    {
        using var db = new TemporarySqliteDatabase();
        using var root = CreateConfiguration(db.Path);
        var configurationOperator = root.GetConfigurationOperator();
        var cancel = TestContext.Current.CancellationToken;

        await configurationOperator.UpdateAsync("Stress:Key", "initial", cancel);

        var readerTask = Task.Run(
            async () =>
            {
                for (var i = 0; i < 2000; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    // ReSharper disable once AccessToDisposedClosure
                    _ = root["Stress:Key"];
                    await Task.Yield();
                }
            },
            cancel);

        var writerTask = Task.Run(
            async () =>
            {
                for (var i = 0; i < 100; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    await configurationOperator.UpdateAsync("Stress:Key", i.ToString(CultureInfo.InvariantCulture), cancel).ConfigureAwait(true);
                }
            },
            cancel);

        await Task.WhenAll(readerTask, writerTask).ConfigureAwait(true);

        Assert.Equal("99", root["Stress:Key"]);
    }

    [Fact]
    public async Task UpdateAsyncWithObjectUsesInvariantCultureForDouble()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            using var db = new TemporarySqliteDatabase();
            using var root = CreateConfiguration(db.Path);
            var configurationOperator = root.GetConfigurationOperator();
            var cancel = TestContext.Current.CancellationToken;

            await configurationOperator.UpdateAsync("Culture:Double", 3.14, cancel);

            var stored = (string?)GetValue(db.Path, "Culture:Double");
            Assert.Equal("3.14", stored);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public async Task UpdateAsyncWithObjectUsesInvariantCultureForDateTime()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            using var db = new TemporarySqliteDatabase();
            using var root = CreateConfiguration(db.Path);
            var configurationOperator = root.GetConfigurationOperator();
            var cancel = TestContext.Current.CancellationToken;

            var dt = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);
            await configurationOperator.UpdateAsync("Culture:DateTime", dt, cancel);

            await configurationOperator.ReloadAsync(cancel);

            var stored = root["Culture:DateTime"];
            Assert.NotNull(stored);
            Assert.DoesNotContain(".", stored, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public async Task UpdateAsyncWithNullObjectPreservesNull()
    {
        using var db = new TemporarySqliteDatabase();
        using var root = CreateConfiguration(db.Path);
        var configurationOperator = root.GetConfigurationOperator();
        var cancel = TestContext.Current.CancellationToken;

        await configurationOperator.UpdateAsync("Null:Key", "initial", cancel);
        Assert.Equal("initial", root["Null:Key"]);

        await configurationOperator.UpdateAsync("Null:Key", (object?)null, cancel);

        var stored = GetValue(db.Path, "Null:Key");
        Assert.Equal(DBNull.Value, stored);

        await configurationOperator.ReloadAsync(cancel);

        Assert.Null(root["Null:Key"]);
    }

    [Fact]
    public async Task UpdateAsyncWithCanceledTokenThrows()
    {
        using var db = new TemporarySqliteDatabase();
        using var root = CreateConfiguration(db.Path);
        var configurationOperator = root.GetConfigurationOperator();

        using var cancels = new CancellationTokenSource();
        await cancels.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => configurationOperator.UpdateAsync("Cancel:Key", "value", cancels.Token).AsTask());
    }

    //--------------------------------------------------------------------------------
    // Helper
    //--------------------------------------------------------------------------------

    private static ConfigurationRoot CreateConfiguration(string path, Action<SqliteConfigurationOptions>? configure = null) =>
        (ConfigurationRoot)new ConfigurationBuilder()
            .AddSqliteConfiguration(options =>
            {
                options.Path = path;
                options.Table = "setting";
                configure?.Invoke(options);
            })
            .Build();

    private static IEnumerable<KeyValuePair<string, object?>> CreateEntries()
    {
        yield return new KeyValuePair<string, object?>("Lazy:A", 1);
        yield return new KeyValuePair<string, object?>("Lazy:B", 2);
    }

    private static IEnumerable<string> CreateKeys()
    {
        yield return "Lazy:A";
        yield return "Lazy:B";
    }

    private static object? GetValue(string path, string key)
    {
        using var con = OpenConnection(path);
        using var command = con.CreateCommand();
        command.CommandText = "SELECT Value FROM setting WHERE Key = @Key";
        AddParameter(command, "Key", key);

        return command.ExecuteScalar();
    }

    private static void InsertRow(SqliteConnection con, string key, string? value)
    {
        using var command = con.CreateCommand();
        command.CommandText = "INSERT INTO setting(Key, Value) VALUES (@Key, @Value)";
        AddParameter(command, "Key", key);
        AddParameter(command, "Value", value);
        _ = command.ExecuteNonQuery();
    }

    private static SqliteConnection OpenConnection(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            Cache = SqliteCacheMode.Shared,
            DataSource = path,
            Pooling = true
        };

        var con = new SqliteConnection(builder.ConnectionString);
        con.Open();
        return con;
    }

    private static void AddParameter(SqliteCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        _ = command.Parameters.Add(parameter);
    }

    private sealed class TemporarySqliteDatabase : IDisposable
    {
        public TemporarySqliteDatabase()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        }

        public string Path { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();

            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }
}
