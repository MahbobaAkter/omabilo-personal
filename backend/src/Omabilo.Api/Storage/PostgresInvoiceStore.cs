
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

namespace Omabilo.Api.Storage;

/// <summary>
/// PostgreSQL invoice storage. Each owner has separate invoice, source and audit collections.
/// </summary>
public sealed class PostgresInvoiceStore : IInvoiceStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _connectionString;
    private readonly OwnerLocks _locks = new();

    public PostgresInvoiceStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "A PostgreSQL connection string is required.",
                nameof(connectionString));
        }

        _connectionString = connectionString;
    }

    public async Task<InvoiceBook> LoadAsync(
        string ownerId,
        CancellationToken ct = default)
    {
        ValidateOwner(ownerId);

        using var lease = await _locks.AcquireAsync(ownerId, ct);
        await using var connection = await OpenAsync(ct);

        return new InvoiceBook(
            await ReadListAsync<CanonicalInvoice>(connection, ownerId, "invoices", ct),
            await ReadListAsync<InvoiceSourceRecord>(connection, ownerId, "sources", ct),
            await ReadListAsync<InvoiceAuditEvent>(connection, ownerId, "audit", ct));
    }

    public async Task<T> UpdateAsync<T>(
        string ownerId,
        Func<InvoiceBook, T> change,
        CancellationToken ct = default)
    {
        ValidateOwner(ownerId);

        using var lease = await _locks.AcquireAsync(ownerId, ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var book = new InvoiceBook(
            await ReadListAsync<CanonicalInvoice>(connection, ownerId, "invoices", ct, transaction),
            await ReadListAsync<InvoiceSourceRecord>(connection, ownerId, "sources", ct, transaction),
            await ReadListAsync<InvoiceAuditEvent>(connection, ownerId, "audit", ct, transaction));

        var result = change(book);

        await WriteAsync(connection, transaction, ownerId, "invoices",
            book.AllInvoices.Where(x => x.OwnerId == ownerId).ToList(), ct);

        await WriteAsync(connection, transaction, ownerId, "sources",
            book.AllSources.Where(x => x.OwnerId == ownerId).ToList(), ct);

        await WriteAsync(connection, transaction, ownerId, "audit",
            book.AllAudit.Where(x => x.OwnerId == ownerId).ToList(), ct);

        await transaction.CommitAsync(ct);
        return result;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task<List<T>> ReadListAsync<T>(
        NpgsqlConnection connection,
        string ownerId,
        string collection,
        CancellationToken ct,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand("""
            SELECT json_data
            FROM omabilo_owner_collection
            WHERE owner_id = @owner AND collection = @collection
            """, connection, transaction);

        command.Parameters.AddWithValue("owner", ownerId);
        command.Parameters.AddWithValue("collection", collection);

        var value = await command.ExecuteScalarAsync(ct);

        if (value is null or DBNull)
        {
            return [];
        }

        var json = Convert.ToString(value)!;

        return JsonSerializer.Deserialize<List<T>>(json, Json) ?? [];
    }

    private static async Task WriteAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string ownerId,
        string collection,
        List<T> items,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO omabilo_owner_collection
                (owner_id, collection, json_data, updated_at)
            VALUES (@owner, @collection, CAST(@json AS jsonb), NOW())
            ON CONFLICT (owner_id, collection)
            DO UPDATE SET
                json_data = EXCLUDED.json_data,
                updated_at = NOW()
            """, connection, transaction);

        command.Parameters.AddWithValue("owner", ownerId);
        command.Parameters.AddWithValue("collection", collection);
        command.Parameters.AddWithValue(
            "json", JsonSerializer.Serialize(items, Json));

        await command.ExecuteNonQueryAsync(ct);
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
