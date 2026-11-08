using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FeatureRollout;
using FeatureRollout.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

public sealed class PublicationTests : IClassFixture<PostgresFixture>
{
    private const string TestToken = "integration-admin-token";
    private readonly PostgresFixture postgres;

    public PublicationTests(PostgresFixture postgres) => this.postgres = postgres;

    [Fact]
    public async Task ConcurrentWritersCommitOneSnapshotAndOneAuditRecord()
    {
        await using var app = await StartApi();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
        using var openApi = JsonDocument.Parse(await http.GetStringAsync("/openapi/v1.json"));
        var paths = openApi.RootElement.GetProperty("paths");
        Assert.True(paths.GetProperty("/v1/admin/environments/{name}").GetProperty("put")
            .GetProperty("security")[0].TryGetProperty("Bearer", out _));
        Assert.False(paths.GetProperty("/v1/config/{name}").GetProperty("get")
            .TryGetProperty("security", out _));
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await http.PutAsync("/v1/admin/environments/race", null)).StatusCode);

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestToken);
        Assert.Equal(HttpStatusCode.Created,
            (await http.PutAsync("/v1/admin/environments/race", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await http.PutAsync("/v1/admin/environments/race", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/v1/config/race")).StatusCode);

        string path = "/v1/admin/environments/race/publications";
        var statuses = await Task.WhenAll(Enumerable.Range(0, 10).Select(async percent =>
        {
            using var response = await http.PostAsJsonAsync(path, Request(0, percent));
            return response.StatusCode;
        }));
        Assert.Single(statuses, code => code == HttpStatusCode.Created);
        Assert.Equal(9, statuses.Count(code => code == HttpStatusCode.Conflict));

        var first = await http.GetStringAsync("/v1/config/race");
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync(path, Request(1, 101))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await http.PostAsJsonAsync(path, Request(1, 50))).StatusCode);
        Assert.Equal(first, await http.GetStringAsync(path + "/1"));
        var history = await http.GetFromJsonAsync<Publication[]>("/v1/admin/environments/race/history");
        Assert.NotNull(history);
        Assert.Equal(new long[] { 2, 1 }, history.Select(entry => entry.Revision));
        Assert.All(history, entry => Assert.Equal("test-operator", entry.Actor));
        var older = await http.GetFromJsonAsync<Publication[]>("/v1/admin/environments/race/history?before=2");
        Assert.Equal(1, Assert.Single(older!).Revision);

        Assert.Equal(HttpStatusCode.Created,
            (await http.PutAsync("/v1/admin/environments/other", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await http.PostAsJsonAsync("/v1/admin/environments/other/publications", Request(0, 100))).StatusCode);
        http.DefaultRequestHeaders.Authorization = null;
        var other = await http.GetFromJsonAsync<Configuration>("/v1/config/other");
        Assert.NotNull(other);
        Assert.Equal(1, other.Revision);
        Assert.Equal(100, other.Flags["checkout"].RolloutPercent);

        var current = await http.GetFromJsonAsync<Configuration>("/v1/config/race");
        Assert.Equal(2, current!.Revision);
        Assert.Equal(50, current.Flags["checkout"].RolloutPercent);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(path + "/1")).StatusCode);
    }

    [Fact]
    public async Task RejectedInsertRollsBackRevisionAndAllowsRetry()
    {
        await using var database = NpgsqlDataSource.Create(postgres.ConnectionString);
        var store = new PublicationStore(database);
        await store.Migrate(default);
        Assert.True(await store.CreateEnvironment("rollback", default));
        var config = new Configuration(1, "rollback", 1, Request(0, 10).Flags);

        await using var reject = database.CreateCommand("""
            ALTER TABLE flag_publications ADD CONSTRAINT reject_test_actor CHECK (actor <> 'fail-insert')
            """);
        await reject.ExecuteNonQueryAsync();

        var failure = await Assert.ThrowsAsync<PostgresException>(() => store.Publish(config, "fail-insert", default));
        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        Assert.Null(await store.Read("rollback", null, default));
        Assert.Empty(await store.History("rollback", null, default));
        Assert.Equal(PublishResult.Published, await store.Publish(config, "test-operator", default));
        Assert.NotNull(await store.Read("rollback", null, default));
        Assert.Equal("test-operator", Assert.Single(await store.History("rollback", null, default)).Actor);
    }

    private async Task<WebApplication> StartApi()
    {
        var app = FlagApi.Build([
            "--urls=http://127.0.0.1:0",
            $"--ConnectionStrings:Flags={postgres.ConnectionString}",
            $"--Admin:Token={TestToken}", "--Admin:Actor=test-operator"
        ]);
        try
        {
            await app.Services.GetRequiredService<PublicationStore>().Migrate(default);
            await app.StartAsync();
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private static PublishRequest Request(long expected, int percent) =>
        new(expected, new() { ["checkout"] = new(false, percent, [], []) });
}

public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? container;
    private string? externalConnection;
    private readonly string schema = "feature_rollout_test_" + Guid.NewGuid().ToString("N");

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        externalConnection = Environment.GetEnvironmentVariable("FEATURE_ROLLOUT_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(externalConnection))
        {
            container = new PostgreSqlBuilder("postgres:18-alpine").Build();
            await container.StartAsync();
            ConnectionString = container.GetConnectionString();
            return;
        }

        await ExecuteExternal($"CREATE SCHEMA {schema}");
        ConnectionString = new NpgsqlConnectionStringBuilder(externalConnection)
        {
            SearchPath = schema,
            Pooling = false
        }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (container is not null)
            await container.DisposeAsync();
        else if (!string.IsNullOrWhiteSpace(externalConnection))
            await ExecuteExternal($"DROP SCHEMA IF EXISTS {schema} CASCADE");
    }

    private async Task ExecuteExternal(string sql)
    {
        await using var connection = new NpgsqlConnection(externalConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
