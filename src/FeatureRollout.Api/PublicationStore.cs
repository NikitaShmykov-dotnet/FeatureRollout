using System.Reflection;
using System.Text.Json;
using FeatureRollout;
using Npgsql;

namespace FeatureRollout.Api;

public sealed record Publication(long Revision, string Actor, DateTime PublishedAt);
public enum PublishResult { Published, MissingEnvironment, Conflict }

public sealed class PublicationStore(NpgsqlDataSource database)
{
    public async Task Migrate(CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(48193021)", connection, transaction);
        await command.ExecuteNonQueryAsync(ct);

        using var script = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("FeatureRollout.Api.Migrations.001_initial.sql")!;
        using var reader = new StreamReader(script);
        command.CommandText = await reader.ReadToEndAsync(ct);
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<bool> CreateEnvironment(string name, CancellationToken ct)
    {
        await using var command = database.CreateCommand(
            "INSERT INTO flag_environments(name) VALUES ($1) ON CONFLICT DO NOTHING");
        command.Parameters.AddWithValue(name);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<PublishResult> Publish(Configuration snapshot, string actor, CancellationToken ct)
    {
        snapshot.Validate();
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(snapshot, Configuration.Json);
        if (json.Length > Configuration.MaxBytes)
            throw new InvalidDataException("Snapshot exceeds size limit.");

        await using var connection = await database.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // PostgreSQL rechecks the revision after waiting for a competing writer.
        await using var advance = new NpgsqlCommand("""
            UPDATE flag_environments SET revision = $2
            WHERE name = $1 AND revision = $2 - 1
            """, connection, transaction);
        advance.Parameters.AddWithValue(snapshot.Environment);
        advance.Parameters.AddWithValue(snapshot.Revision);
        if (await advance.ExecuteNonQueryAsync(ct) == 0)
        {
            await using var exists = new NpgsqlCommand(
                "SELECT 1 FROM flag_environments WHERE name = $1", connection, transaction);
            exists.Parameters.AddWithValue(snapshot.Environment);
            return await exists.ExecuteScalarAsync(ct) is null
                ? PublishResult.MissingEnvironment : PublishResult.Conflict;
        }

        await using var insert = new NpgsqlCommand("""
            INSERT INTO flag_publications(environment, revision, snapshot, actor)
            VALUES ($1, $2, $3, $4)
            """, connection, transaction);
        insert.Parameters.AddWithValue(snapshot.Environment);
        insert.Parameters.AddWithValue(snapshot.Revision);
        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Json, Value = json });
        insert.Parameters.AddWithValue(actor);
        await insert.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return PublishResult.Published;
    }

    public async Task<string?> Read(string name, long? revision, CancellationToken ct)
    {
        await using var command = database.CreateCommand(revision.HasValue
            ? "SELECT snapshot::text FROM flag_publications WHERE environment = $1 AND revision = $2"
            : """
              SELECT p.snapshot::text
              FROM flag_environments e JOIN flag_publications p
                  ON p.environment = e.name AND p.revision = e.revision
              WHERE e.name = $1
              """);
        command.Parameters.AddWithValue(name);
        if (revision.HasValue)
            command.Parameters.AddWithValue(revision.Value);

        return await command.ExecuteScalarAsync(ct) as string;
    }

    public async Task<List<Publication>> History(string name, long? before, CancellationToken ct)
    {
        await using var command = database.CreateCommand("""
            SELECT revision, actor, published_at FROM flag_publications
            WHERE environment = $1 AND ($2 IS NULL OR revision < $2)
            ORDER BY revision DESC LIMIT 50
            """);
        command.Parameters.AddWithValue(name);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint,
            Value = (object?)before ?? DBNull.Value
        });
        await using var reader = await command.ExecuteReaderAsync(ct);
        var history = new List<Publication>();
        while (await reader.ReadAsync(ct))
            history.Add(new Publication(reader.GetInt64(0), reader.GetString(1), reader.GetDateTime(2)));

        return history;
    }

    public async Task CheckHealth(CancellationToken ct)
    {
        await using var command = database.CreateCommand("SELECT 1 FROM flag_environments LIMIT 1");
        await command.ExecuteScalarAsync(ct);
    }
}
