using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;
using FeatureRollout;

internal sealed class ScriptedServer : HttpMessageHandler
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private readonly Channel<TaskCompletionSource<HttpResponseMessage>> requests =
        Channel.CreateUnbounded<TaskCompletionSource<HttpResponseMessage>>();

    public HttpClient CreateClient() => new(this, disposeHandler: false)
    {
        BaseAddress = new Uri("http://flags.test/"),
        Timeout = Timeout.InfiniteTimeSpan
    };

    public bool HasPendingRequest => requests.Reader.TryPeek(out _);

    public FlagClient StartClient(HttpClient http, Action<string>? failure = null) =>
        new(http, "demo", TimeSpan.FromMilliseconds(5), TestTimeout, failure);

    public async Task<TaskCompletionSource<HttpResponseMessage>> NextRequest()
    {
        using var deadline = new CancellationTokenSource(TestTimeout);
        return await requests.Reader.ReadAsync(deadline.Token);
    }

    public static async Task Until(Func<bool> ready)
    {
        using var deadline = new CancellationTokenSource(TestTimeout);
        while (!ready())
            await Task.Delay(5, deadline.Token);
    }

    public static HttpResponseMessage Snapshot(long revision, int percent, bool fallback = false,
        int schema = 1, string environment = "demo") =>
        Snapshot(new Configuration(schema, environment, revision, new()
        {
            ["checkout"] = new(fallback, percent, [], [])
        }));

    public static HttpResponseMessage Snapshot(Configuration configuration) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(configuration)
    };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/v1/config/demo", request.RequestUri!.PathAndQuery);
        Assert.Null(request.Content);
        var response = new TaskCompletionSource<HttpResponseMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await requests.Writer.WriteAsync(response, cancellationToken);
        return await response.Task.WaitAsync(cancellationToken);
    }
}
