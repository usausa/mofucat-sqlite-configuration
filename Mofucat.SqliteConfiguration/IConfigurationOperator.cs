namespace Mofucat.SqliteConfiguration;

public interface IConfigurationOperator
{
    ValueTask UpdateAsync(string key, string? value, CancellationToken cancel = default);

    ValueTask UpdateAsync(string key, object? value, CancellationToken cancel = default);

    ValueTask BulkUpdateAsync(params KeyValuePair<string, object?>[] source);

    ValueTask BulkUpdateAsync(IEnumerable<KeyValuePair<string, object?>> source, CancellationToken cancel = default);

    ValueTask DeleteAsync(string key, CancellationToken cancel = default);

    ValueTask BulkDeleteAsync(params string[] keys);

    ValueTask BulkDeleteAsync(IEnumerable<string> keys, CancellationToken cancel = default);

    ValueTask ReloadAsync(CancellationToken cancel = default);
}
