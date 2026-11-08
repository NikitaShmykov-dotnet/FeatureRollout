using FeatureRollout;

public sealed class RolloutTests
{
    [Theory]
    [InlineData("checkout", "user-000001", 9654)]
    [InlineData("ab", "c", 6104)]
    [InlineData("a", "bc", 8830)]
    [InlineData("checkout", "пользователь", 3203)]
    [InlineData("checkout", "", 5441)]
    public void BucketMatchesReferenceVector(string key, string user, int expected) =>
        Assert.Equal(expected, Rollout.Bucket(key, user));

    [Fact]
    public async Task RolloutHasExpectedDistributionAndKeepsExistingCohort()
    {
        using var server = new ScriptedServer();
        using var http = server.CreateClient();
        await using var client = server.StartClient(http);
        var users = Enumerable.Range(0, 100_000)
            .Select(index => new FlagUser(FormattableString.Invariant($"user-{index:D6}"))).ToArray();

        async Task<bool[]> Evaluate(long revision, int percent)
        {
            (await server.NextRequest()).SetResult(ScriptedServer.Snapshot(revision, percent));
            await ScriptedServer.Until(() => client.EvaluateWithRevision("checkout", users[0], false)
                .Revision == revision);
            return users.Select(user => client.Evaluate("checkout", user, false)).ToArray();
        }

        Assert.DoesNotContain(true, await Evaluate(1, 0));
        bool[] ten = await Evaluate(2, 10);
        bool[] twenty = await Evaluate(3, 20);
        Assert.InRange(ten.Count(enabled => enabled), 9500, 10_500);
        Assert.InRange(twenty.Count(enabled => enabled), 19_500, 20_500);
        Assert.All(ten.Zip(twenty), pair => Assert.True(!pair.First || pair.Second));
        Assert.DoesNotContain(false, await Evaluate(4, 100));
    }
}
