
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Omabilo.Api.Storage;

namespace Omabilo.Api.Finance;

/// <summary>
/// PostgreSQL-backed finance storage. Each owner has a separate finance document.
/// </summary>
public sealed class PostgresFinanceStore : IFinanceStore
{
    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    private readonly OwnerLocks _locks = new();

    public PostgresFinanceStore(string connectionString, TimeProvider clock)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "A PostgreSQL connection string is required.",
                nameof(connectionString));
        }

        _connectionString = connectionString;
        _clock = clock;
    }

    public async Task<FinanceData> LoadAsync(
        string ownerId,
        CancellationToken ct = default)
    {
        ValidateOwner(ownerId);

        using var lease = await _locks.AcquireAsync(ownerId, ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT json_data
            FROM omabilo_owner_collection
            WHERE owner_id = @owner AND collection = 'finance'
            """;

        command.Parameters.AddWithValue("owner", ownerId);

        var value = await command.ExecuteScalarAsync(ct);

        if (value is string json)
        {
            return FinanceJson.Deserialize(json);
        }

        var seeded = FinnishDemoSeed.Create(
            ownerId,
            _clock.GetUtcNow().UtcDateTime);

        await SaveAsync(connection, ownerId, seeded, ct);
        return seeded;
    }

    public async Task<T> UpdateAsync<T>(
        string ownerId,
        Func<FinanceData, (FinanceData Next, T Result)> change,
        CancellationToken ct = default)
    {
        ValidateOwner(ownerId);

        using var lease = await _locks.AcquireAsync(ownerId, ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction =
            await connection.BeginTransactionAsync(ct);

        var current = await LoadWithinTransactionAsync(
            connection, transaction, ownerId, ct);

        var (next, result) = change(current);

        await using var command = new NpgsqlCommand("""
            INSERT INTO omabilo_owner_collection
                (owner_id, collection, json_data, updated_at)
            VALUES (@owner, 'finance', CAST(@json AS jsonb), NOW())
            ON CONFLICT (owner_id, collection)
            DO UPDATE SET
                json_data = EXCLUDED.json_data,
                updated_at = NOW()
            """, connection, transaction);

        command.Parameters.AddWithValue("owner", ownerId);
        command.Parameters.AddWithValue(
            "json", FinanceJson.Serialize(next));

        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);

        return result;
    }

    private async Task<FinanceData> LoadWithinTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string ownerId,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT json_data
            FROM omabilo_owner_collection
            WHERE owner_id = @owner AND collection = 'finance'
            FOR UPDATE
            """, connection, transaction);

        command.Parameters.AddWithValue("owner", ownerId);

        var value = await command.ExecuteScalarAsync(ct);

        if (value is string json)
        {
            return FinanceJson.Deserialize(json);
        }

        var seeded = FinnishDemoSeed.Create(
            ownerId,
            _clock.GetUtcNow().UtcDateTime);

        await using var insert = new NpgsqlCommand("""
            INSERT INTO omabilo_owner_collection
                (owner_id, collection, json_data, updated_at)
            VALUES (@owner, 'finance', CAST(@json AS jsonb), NOW())
            ON CONFLICT (owner_id, collection) DO NOTHING
            """, connection, transaction);

        insert.Parameters.AddWithValue("owner", ownerId);
        insert.Parameters.AddWithValue(
            "json", FinanceJson.Serialize(seeded));

        await insert.ExecuteNonQueryAsync(ct);

        // Read again in case another API instance inserted the owner's document.
        await using var reread = new NpgsqlCommand("""
            SELECT json_data
            FROM omabilo_owner_collection
            WHERE owner_id = @owner AND collection = 'finance'
            """, connection, transaction);

        reread.Parameters.AddWithValue("owner", ownerId);

        var stored = await reread.ExecuteScalarAsync(ct);

        return stored is string storedJson
            ? FinanceJson.Deserialize(storedJson)
            : seeded;
    }

    private async Task SaveAsync(
        NpgsqlConnection connection,
        string ownerId,
        FinanceData data,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO omabilo_owner_collection
                (owner_id, collection, json_data, updated_at)
            VALUES (@owner, 'finance', CAST(@json AS jsonb), NOW())
            ON CONFLICT (owner_id, collection)
            DO UPDATE SET
                json_data = EXCLUDED.json_data,
                updated_at = NOW()
            """, connection);

        command.Parameters.AddWithValue("owner", ownerId);
        command.Parameters.AddWithValue(
            "json", FinanceJson.Serialize(data));

        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static void ValidateOwner(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            throw new ArgumentException(
                "An authenticated owner ID is required.",
                nameof(ownerId));
        }
    }
}
