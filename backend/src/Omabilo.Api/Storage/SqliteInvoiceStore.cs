using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Omabilo.Api.Storage;

/// <summary>
/// Saves records in a local SQLite file, one row per owner and collection, so they survive a
/// restart. SQLite is a free, file-based database that needs no server; it suits local
/// development. A hosted deployment would swap in Postgres behind the same interface.
/// The file holds whatever people enter, so it must never be committed (see .gitignore).
/// </summary>
public sealed class SqliteInvoiceStore : IInvoiceStore
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly string _connectionString;
    private readonly OwnerLocks _locks = new();

    public SqliteInvoiceStore(string databasePath)
    {
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
                owner_id   TEXT NOT NULL,
                collection TEXT NOT NULL,
                json       TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (owner_id, collection)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task<InvoiceBook> LoadAsync(string ownerId, CancellationToken ct = default)
    {
        using var _ = await _locks.AcquireAsync(ownerId, ct);
        return Read(ownerId);
    }

    public async Task<T> UpdateAsync<T>(string ownerId, Func<InvoiceBook, T> change, CancellationToken ct = default)
    {
        using var _ = await _locks.AcquireAsync(ownerId, ct);
        var book = Read(ownerId);
        var result = change(book);

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        Write(conn, tx, ownerId, "invoices", book.AllInvoices.Where(i => i.OwnerId == ownerId).ToList());
        Write(conn, tx, ownerId, "sources", book.AllSources.Where(s => s.OwnerId == ownerId).ToList());
        Write(conn, tx, ownerId, "audit", book.AllAudit.Where(a => a.OwnerId == ownerId).ToList());
        tx.Commit();
        return result;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private InvoiceBook Read(string ownerId)
    {
        using var conn = Open();
        return new InvoiceBook(
            ReadList<CanonicalInvoice>(conn, ownerId, "invoices"),
            ReadList<InvoiceSourceRecord>(conn, ownerId, "sources"),
            ReadList<InvoiceAuditEvent>(conn, ownerId, "audit"));
    }

    private static List<T> ReadList<T>(SqliteConnection conn, string ownerId, string collection)
    {
        using var cmd = conn.CreateCommand();
        // Parameters, never string concatenation, so an owner id can't change the query
        cmd.CommandText = "SELECT json FROM owner_collection WHERE owner_id = $owner AND collection = $collection";
        cmd.Parameters.AddWithValue("$owner", ownerId);
        cmd.Parameters.AddWithValue("$collection", collection);
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<List<T>>(json, Json) ?? [] : [];
    }

    private static void Write<T>(SqliteConnection conn, SqliteTransaction tx, string ownerId, string collection, List<T> items)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO owner_collection (owner_id, collection, json, updated_at)
            VALUES ($owner, $collection, $json, $at)
            ON CONFLICT (owner_id, collection) DO UPDATE SET json = excluded.json, updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$owner", ownerId);
        cmd.Parameters.AddWithValue("$collection", collection);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(items, Json));
        cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }
}
