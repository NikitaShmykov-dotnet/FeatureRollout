using System.Text.Json;

namespace FeatureRollout;

/// <summary>Downloads environment snapshots in the background and evaluates flags locally.</summary>
public sealed class FlagClient : IAsyncDisposable
{
    private readonly HttpClient http;
    private readonly string environment;
    private readonly TimeSpan requestTimeout;
    private readonly Action<string>? reportFailure;
    private readonly TimeProvider clock;
    private readonly CancellationTokenSource stop = new();
    private readonly Task refreshLoop;
    private readonly object disposalLock = new();
    private Task? disposal;
    private Snapshot? current;

    /// <summary>Starts the first refresh without waiting for a snapshot.</summary>
    /// <param name="http">Caller-owned HTTP client with a base URL ending in a slash.</param>
    /// <param name="environment">Environment key, up to 64 ASCII letters, digits, hyphens or underscores.</param>
    /// <param name="refreshInterval">Time between refresh ticks, from 1 ms to 4,294,967,294 ms.</param>
    /// <param name="requestTimeout">Deadline for response headers and body, from 1 ms to 4,294,967,294 ms.</param>
    /// <param name="reportFailure">Optional synchronous observer. Keep it short. Its exceptions are ignored.</param>
    /// <param name="timeProvider">Clock for refresh ticks and deadlines. Defaults to the system clock.</param>
    public FlagClient(HttpClient http, string environment, TimeSpan refreshInterval,
        TimeSpan requestTimeout, Action<string>? reportFailure = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (http.BaseAddress is not { Scheme: "http" or "https" } address ||
            !address.AbsolutePath.EndsWith('/') || address.Query.Length > 0 || address.Fragment.Length > 0)
            throw new ArgumentException("Set an HTTP base address with a trailing slash.", nameof(http));
        if (!Configuration.IsKey(environment))
            throw new ArgumentException("Invalid environment key.", nameof(environment));
        if (refreshInterval.TotalMilliseconds is < 1 or > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(refreshInterval));
        if (requestTimeout.TotalMilliseconds is < 1 or > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));

        this.http = http;
        this.environment = environment;
        this.requestTimeout = requestTimeout;
        this.reportFailure = reportFailure;
        clock = timeProvider ?? TimeProvider.System;
        refreshLoop = RefreshLoop(refreshInterval);
    }

    /// <summary>Evaluates a nonempty flag key for a user. Returns the fallback if the flag or snapshot is absent.</summary>
    public bool Evaluate(string flagKey, FlagUser user, bool fallback) =>
        EvaluateWithRevision(flagKey, user, fallback).Enabled;

    /// <summary>Returns the decision and the revision read for that decision. The revision is null before startup completes.</summary>
    public Evaluation EvaluateWithRevision(string flagKey, FlagUser user, bool fallback)
    {
        ArgumentException.ThrowIfNullOrEmpty(flagKey);
        ArgumentNullException.ThrowIfNull(user);
        var snapshot = Volatile.Read(ref current);
        if (snapshot is null || !snapshot.Flags.TryGetValue(flagKey, out var flag))
            return new Evaluation(snapshot?.Revision, fallback);

        if (flag.Overrides.TryGetValue(user.Id, out bool enabled))
            return new Evaluation(snapshot.Revision, enabled);

        if (user.Attributes is not null)
        {
            foreach (var rule in flag.Rules)
                if (user.Attributes.TryGetValue(rule.Attribute, out string? value) &&
                    string.Equals(value, rule.EqualsValue, StringComparison.Ordinal))
                    return new Evaluation(snapshot.Revision, rule.Enabled);
        }

        bool rolledOut = flag.RolloutPercent == 100 ||
            (flag.RolloutPercent > 0 && Rollout.Bucket(flagKey, user.Id) < flag.RolloutPercent * 100);
        return new Evaluation(snapshot.Revision, rolledOut || flag.Default);
    }

    private async Task RefreshLoop(TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval, clock);
        try
        {
            do
            {
                await Refresh(stop.Token).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Disposal cancels both the request and the next scheduled refresh.
        }
    }

    private async Task Refresh(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(requestTimeout, clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var response = await http.GetAsync($"v1/config/{environment}",
                HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > Configuration.MaxBytes)
                throw new InvalidDataException("Snapshot exceeds size limit.");

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var body = new MemoryStream();
            byte[] buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
            {
                if (body.Length + read > Configuration.MaxBytes)
                    throw new InvalidDataException("Snapshot exceeds size limit.");
                body.Write(buffer, 0, read);
            }

            if (response.Content.Headers.ContentLength is long expected && body.Length != expected)
                throw new InvalidDataException("Incomplete snapshot.");

            var config = JsonSerializer.Deserialize<Configuration>(
                body.GetBuffer().AsSpan(0, (int)body.Length), Configuration.Json);
            if (config is null || config.Environment != environment)
                throw new InvalidDataException("Unexpected snapshot environment.");

            var snapshot = config.Freeze();
            if (snapshot.Revision > (Volatile.Read(ref current)?.Revision ?? 0))
                Volatile.Write(ref current, snapshot);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ReportFailure("Snapshot request timed out.");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException
            or InvalidDataException)
        {
            ReportFailure("Snapshot refresh failed.");
        }
    }

    private void ReportFailure(string message)
    {
        try
        {
            reportFailure?.Invoke(message);
        }
        catch (Exception)
        {
            // An observer failure must not disable refresh or fault disposal.
        }
    }

    /// <summary>Cancels refresh and waits for it to finish. The caller retains ownership of the HTTP client.</summary>
    public ValueTask DisposeAsync()
    {
        lock (disposalLock)
            return new ValueTask(disposal ??= StopAsync());
    }

    private async Task StopAsync()
    {
        await stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await refreshLoop.ConfigureAwait(false);
        }
        finally
        {
            stop.Dispose();
        }
    }
}
