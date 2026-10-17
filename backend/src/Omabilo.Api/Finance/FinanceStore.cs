using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Omabilo.Api.Storage;

namespace Omabilo.Api.Finance;

/// <summary>
/// Where each person's finance records live: one document per owner. A new owner starts with
/// the fictional Finnish demo data. Changes for one owner run one at a time.
/// </summary>
public interface IFinanceStore
{
    Task<FinanceData> LoadAsync(string ownerId, CancellationToken ct = default);

    /// <summary>Applies a change and saves the result. The change returns the new data and a result.</summary>
    Task<T> UpdateAsync<T>(string ownerId, Func<FinanceData, (FinanceData Next, T Result)> change, CancellationToken ct = default);
}

public sealed class InMemoryFinanceStore(TimeProvider clock) : IFinanceStore
{
    private readonly Dictionary<string, string> _docs = new();
    private readonly OwnerLocks _locks = new();
    private readonly object _sync = new();

    public async Task<FinanceData> LoadAsync(string ownerId, CancellationToken ct = default)
    {
        using var _ = await _locks.AcquireAsync(ownerId, ct);
        return ReadOrSeed(ownerId);
    }

    public async Task<T> UpdateAsync<T>(string ownerId, Func<FinanceData, (FinanceData Next, T Result)> change, CancellationToken ct = default)
    {
        using var _ = await _locks.AcquireAsync(ownerId, ct);
        var (next, result) = change(ReadOrSeed(ownerId));

        lock (_sync)
        {
            _docs[ownerId] = FinanceJson.Serialize(next);
        }

        return result;
    }

    // Stored as JSON text so a caller can never mutate the stored copy by accident
    private FinanceData ReadOrSeed(string ownerId)
    {
        lock (_sync)
        {
            if (!_docs.TryGetValue(ownerId, out var json))
            {
                json = FinanceJson.Serialize(FinnishDemoSeed.Create(ownerId, clock.GetUtcNow().UtcDateTime));
                _docs[ownerId] = json;
            }

            return FinanceJson.Deserialize(json);
        }
    }
}

/// <summary>Saves finance documents in the same local SQLite file as invoices, in the
/// collection "finance". For development; never committed (see .gitignore).</summary>
public sealed class SqliteFinanceStore : IFinanceStore
{
    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    private readonly OwnerLocks _locks = new();

    public SqliteFinanceStore(string databasePath, TimeProvider clock)
    {
        _clock = clock;
        var dir = Path.GetDirectoryName(Path.GetFullPath(databasePath));

        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS owner_collection (
                owner_id TEXT NOT NULL, collection TEXT NOT NULL, json TEXT NOT NULL, updated_at TEXT NOT NULL,
                PRIMARY KEY (owner_id, collection));
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task<FinanceData> LoadAsync(string ownerId, CancellationToken ct = default)
    {
        using var _ = await _locks.AcquireAsync(ownerId, ct);
        return ReadOrSeed(ownerId);
    }

    public async Task<T> UpdateAsync<T>(string ownerId, Func<FinanceData, (FinanceData Next, T Result)> change, CancellationToken ct = default)
    {
        using var _ = await _locks.AcquireAsync(ownerId, ct);
        var (next, result) = change(ReadOrSeed(ownerId));
        Write(ownerId, next);
        return result;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private FinanceData ReadOrSeed(string ownerId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT json FROM owner_collection WHERE owner_id = $owner AND collection = 'finance'";
        cmd.Parameters.AddWithValue("$owner", ownerId);

        if (cmd.ExecuteScalar() is string json)
        {
            return FinanceJson.Deserialize(json);
        }

        var seeded = FinnishDemoSeed.Create(ownerId, _clock.GetUtcNow().UtcDateTime);
        Write(ownerId, seeded);
        return seeded;
    }

    private void Write(string ownerId, FinanceData data)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO owner_collection (owner_id, collection, json, updated_at) VALUES ($owner, 'finance', $json, $at)
            ON CONFLICT (owner_id, collection) DO UPDATE SET json = excluded.json, updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$owner", ownerId);
        cmd.Parameters.AddWithValue("$json", FinanceJson.Serialize(data));
        cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }
}

internal static class FinanceJson
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    public static string Serialize(FinanceData d) => JsonSerializer.Serialize(d, Options);

    public static FinanceData Deserialize(string json) =>
        JsonSerializer.Deserialize<FinanceData>(json, Options) ?? throw new InvalidDataException("Stored finance data is unreadable.");
}
