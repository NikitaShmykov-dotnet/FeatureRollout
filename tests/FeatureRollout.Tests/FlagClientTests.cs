using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using FeatureRollout;
using Microsoft.Extensions.Time.Testing;

public sealed class FlagClientTests
{
    private static readonly FlagUser Visitor = new("visitor");

    [Fact]
    public async Task ThrowingFailureObserverDoesNotStopRefreshOrFaultDisposal()
    {
        using var server = new ScriptedServer();
        using var http = server.CreateClient();
        var clock = new FakeTimeProvider();
        var reported = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var interval = TimeSpan.FromMinutes(1);
        await using var client = new FlagClient(http, "demo", interval, TimeSpan.FromSeconds(5), message =>
        {
            reported.TrySetResult(message);
            throw new InvalidOperationException("reporter failed");
        }, clock);

        (await server.NextRequest()).SetResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        Assert.Equal("Snapshot refresh failed.", await reported.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(new Evaluation(null, false), client.EvaluateWithRevision("checkout", Visitor, false));

        clock.Advance(interval);
        (await server.NextRequest()).SetResult(ScriptedServer.Snapshot(1, 100));
        await ScriptedServer.Until(() => client.EvaluateWithRevision("checkout", Visitor, false) ==
            new Evaluation(1, true));
        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task RequestDeadlineAndRefreshTicksFollowSuppliedClock()
    {
        using var server = new ScriptedServer();
        using var http = server.CreateClient();
        var clock = new FakeTimeProvider();
        var reported = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var interval = TimeSpan.FromSeconds(20);
        await using var client = new FlagClient(http, "demo", interval, TimeSpan.FromSeconds(5),
            message => reported.TrySetResult(message), clock);

        (await server.NextRequest()).SetResult(ScriptedServer.Snapshot(1, 100));
        await ScriptedServer.Until(() => client.EvaluateWithRevision("checkout", Visitor, false) ==
            new Evaluation(1, true));
        clock.Advance(interval);
        using var body = new StalledBody();
        (await server.NextRequest()).SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(body)
        });
        await body.Reading.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(reported.Task.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("Snapshot request timed out.", await reported.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(new Evaluation(1, true), client.EvaluateWithRevision("checkout", Visitor, false));
        Assert.False(server.HasPendingRequest);

        clock.Advance(TimeSpan.FromSeconds(14));
        Assert.False(server.HasPendingRequest);
        clock.Advance(TimeSpan.FromSeconds(1));
        (await server.NextRequest()).SetResult(ScriptedServer.Snapshot(2, 0));
        await ScriptedServer.Until(() => client.EvaluateWithRevision("checkout", Visitor, true) ==
            new Evaluation(2, false));
        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(interval);
        Assert.False(server.HasPendingRequest);
    }

    [Fact]
    public void UserIdMustBePresentAndWellFormed()
    {
        Assert.Throws<ArgumentNullException>(() => new FlagUser(null!));
        Assert.Throws<ArgumentException>(() => new FlagUser(""));
        Assert.Throws<ArgumentException>(() => new FlagUser("\uD800"));
        Assert.Equal("пользователь-😀", new FlagUser("пользователь-😀").Id);
    }

    [Fact]
    public async Task UsesOverridesThenFirstMatchingRuleThenRolloutThenDefault()
    {
        using var server = new ScriptedServer();
        using var http = server.CreateClient();
        await using var client = server.StartClient(http);
        Assert.True(client.Evaluate("checkout", Visitor, true));
        Assert.False(client.Evaluate("checkout", Visitor, false));

        var flags = new Dictionary<string, FlagDefinition>
        {
            ["checkout"] = new(false, 100, new() { ["forced"] = true },
                [new("region", "eu", false), new("tier", "staff", true)]),
            ["default"] = new(true, 0, [], [])
        };
        (await server.NextRequest()).SetResult(ScriptedServer.Snapshot(new(1, "demo", 1, flags)));
        await ScriptedServer.Until(() => client.EvaluateWithRevision("checkout", Visitor, false).Revision == 1);

        var attributes = new Dictionary<string, string> { ["region"] = "eu", ["tier"] = "staff" };
        Assert.True(client.Evaluate("checkout", new("forced", attributes), false));
        Assert.False(client.Evaluate("checkout", new("other", attributes), true));
        Assert.True(client.Evaluate("checkout", Visitor, false));
        Assert.True(client.Evaluate("default", Visitor, false));
        Assert.True(client.Evaluate("unknown", Visitor, true));
    }

    [Fact]
    public async Task FailedRefreshesKeepLastSnapshotAndLaterRefreshRecovers()
    {
        using var server = new ScriptedServer();
        using var http = server.CreateClient();
        var failures = Channel.CreateUnbounded<string>();
        await using var client = server.StartClient(http, message => failures.Writer.TryWrite(message));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        (await server.NextRequest()).SetResult(ScriptedServer.Snapshot(1, 100));
        await ScriptedServer.Until(() => client.Evaluate("checkout", Visitor, false));

        var validJson = JsonSerializer.Serialize(new Configuration(1, "demo", 2, new()
        {
            ["checkout"] = new(false, 0, [], [])
        }), Configuration.Json);
        var partial = new StringContent(validJson);
        partial.Headers.ContentLength += 10;
        var tooLarge = new ByteArrayContent(new byte[Configuration.MaxBytes + 1]);
        foreach (var badResponse in new[]
        {
            new HttpResponseMessage(HttpStatusCode.OK) { Content = partial },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{") },
            ScriptedServer.Snapshot(2, 0, schema: 99),
            ScriptedServer.Snapshot(2, 0, environment: "prod"),
            ScriptedServer.Snapshot(2, 101),
            new HttpResponseMessage(HttpStatusCode.OK) { Content = tooLarge },
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        })
        {
            var request = await server.NextRequest();
            Assert.Equal(new Evaluation(1, true), client.EvaluateWithRevision("checkout", Visitor, false));
            request.SetResult(badResponse);
            Assert.Equal("Snapshot refresh failed.", await failures.Reader.ReadAsync(deadline.Token));
            Assert.Equal(new Evaluation(1, true), client.EvaluateWithRevision("checkout", Visitor, false));
        }

        (await server.NextRequest()).SetException(new HttpRequestException("Connection refused"));
        await failures.Reader.ReadAsync(deadline.Token);
        Assert.Equal(new Evaluation(1, true), client.EvaluateWithRevision("checkout", Visitor, false));

        (await server.NextRequest()).SetResult(ScriptedServer.Snapshot(2, 0));
        await ScriptedServer.Until(() => client.EvaluateWithRevision("checkout", Visitor, true) == new Evaluation(2, false));

        (await server.NextRequest()).SetResult(ScriptedServer.Snapshot(1, 100));
        await server.NextRequest();
        Assert.Equal(new Evaluation(2, false), client.EvaluateWithRevision("checkout", Visitor, true));
    }

    [Fact]
    public async Task ConcurrentEvaluationsObserveMatchingRevisionAndFlagValue()
    {
        using var server = new ScriptedServer();
        using var http = server.CreateClient();
        await using var client = server.StartClient(http);

        (await server.NextRequest()).SetResult(ScriptedServer.Snapshot(1, 0));
        await ScriptedServer.Until(() => client.EvaluateWithRevision("checkout", Visitor, true).Revision == 1);
        using var done = new CancellationTokenSource();
        var ready = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var finalSeen = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var readers = ready.Select((started, readerIndex) => Task.Run(async () =>
        {
            var revisions = new HashSet<long>();
            while (!done.IsCancellationRequested)
            {
                for (int i = 0; i < 128; i++)
                {
                    var result = client.EvaluateWithRevision("checkout", Visitor, true);
                    long revision = Assert.IsType<long>(result.Revision);
                    Assert.Equal(revision % 2 == 0, result.Enabled);
                    revisions.Add(revision);
                    started.TrySetResult();
                    if (revision == 24)
                        finalSeen[readerIndex].TrySetResult();
                }
                await Task.Yield();
            }
            return revisions;
        })).ToArray();

        try
        {
            await Task.WhenAll(ready.Select(signal => signal.Task)).WaitAsync(TimeSpan.FromSeconds(10));
            for (long revision = 2; revision <= 24; revision++)
                (await server.NextRequest()).SetResult(ScriptedServer.Snapshot(revision, revision % 2 == 0 ? 100 : 0));

            await Task.WhenAll(finalSeen.Select(signal => signal.Task)).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await done.CancelAsync();
            var observations = await Task.WhenAll(readers);
            Assert.All(observations, revisions => Assert.True(revisions.Count > 1));
        }
    }

    [Fact]
    public async Task DisposalCancelsOutstandingRequestAndCanBeRepeated()
    {
        using var server = new ScriptedServer();
        using var http = server.CreateClient();
        var clock = new FakeTimeProvider();
        var interval = TimeSpan.FromMinutes(1);
        await using var client = new FlagClient(http, "demo", interval, TimeSpan.FromSeconds(5),
            timeProvider: clock);
        await server.NextRequest();
        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(interval);
        Assert.False(server.HasPendingRequest);
        Assert.False(client.Evaluate("checkout", Visitor, false));
    }

    private sealed class StalledBody : MemoryStream
    {
        private readonly TaskCompletionSource reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Reading => reading.Task;
        public override bool CanSeek => false;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            reading.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
