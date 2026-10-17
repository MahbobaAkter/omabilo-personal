
using Npgsql;

namespace Omabilo.Api.Storage;

/// <summary>
/// Creates the shared PostgreSQL table used by Omabilo's invoice
/// and finance stores. Safe to run more than once.
/// </summary>
public static class PostgresDatabaseInitializer
{
    public static async Task InitializeAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "A PostgreSQL connection string is required.",
                nameof(connectionString));
        }

        await using var connection =
            new NpgsqlConnection(connectionString);

        await connection.OpenAsync(cancellationToken);

        const string sql = """
            CREATE TABLE IF NOT EXISTS omabilo_owner_collection (
                owner_id   TEXT        NOT NULL,
                collection TEXT        NOT NULL,
                json_data  JSONB       NOT NULL,
                updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                PRIMARY KEY (owner_id, collection)
            );
            """;

        await using var command =
            new NpgsqlCommand(sql, connection);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
