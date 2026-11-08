using System.Diagnostics;
using System.Globalization;
using FeatureRollout;

if (args is ["bucket", var key, var id])
{
    Console.WriteLine(Rollout.Bucket(key, id).ToString(CultureInfo.InvariantCulture));
    return;
}

if (args.Length > 1)
    throw new ArgumentException("Usage: FeatureRollout.Demo [server-url] | bucket <flag-key> <user-id>");

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stop.Cancel();
};

using var http = new HttpClient { BaseAddress = new Uri(args.FirstOrDefault() ?? "http://localhost:5080/") };
await using var flags = new FlagClient(http, "demo", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1),
    message => Console.Error.WriteLine(message));

var users = Enumerable.Range(0, 100_000)
    .Select(index => new FlagUser(FormattableString.Invariant($"user-{index:D6}"))).ToArray();
var preview = new FlagUser("preview-user");
var legacy = new FlagUser("legacy-user");
long? lastRevision = null;
bool[]? previous = null;
using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));

try
{
    do
    {
        var previewResult = flags.EvaluateWithRevision("checkout", preview, false);
        var legacyResult = flags.EvaluateWithRevision("checkout", legacy, false);
        var revision = previewResult.Revision;
        if (revision != legacyResult.Revision)
            continue;

        Console.WriteLine($"revision={revision?.ToString() ?? "fallback"} " +
            $"preview={Checkout(previewResult.Enabled)} legacy={Checkout(legacyResult.Enabled)}");
        if (revision is null || revision == lastRevision)
            continue;

        bool[] cohort = new bool[users.Length];
        var watch = Stopwatch.StartNew();
        int evaluated = 0;
        for (; evaluated < users.Length; evaluated++)
        {
            var result = flags.EvaluateWithRevision("checkout", users[evaluated], false);
            if (result.Revision != revision)
                break;
            cohort[evaluated] = result.Enabled;
        }
        watch.Stop();

        if (evaluated != users.Length)
            continue;

        int removed = previous?.Zip(cohort).Count(pair => pair.First && !pair.Second) ?? 0;
        Console.WriteLine(FormattableString.Invariant(
            $"enabled={cohort.Count(value => value)}/{users.Length} removed={removed} evaluate_ms={watch.Elapsed.TotalMilliseconds:F2}"));
        lastRevision = revision;
        previous = cohort;
    }
    while (await timer.WaitForNextTickAsync(stop.Token));
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
    // Ctrl+C cancels the display loop, then await using stops the SDK.
}

static string Checkout(bool enabled) => enabled ? "express" : "classic";
