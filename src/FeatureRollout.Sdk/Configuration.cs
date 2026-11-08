using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FeatureRollout;

public sealed record FlagRule(
    [property: JsonRequired] string Attribute,
    [property: JsonRequired] string EqualsValue,
    [property: JsonRequired] bool Enabled);

public sealed record FlagDefinition(
    [property: JsonRequired] bool Default,
    [property: JsonRequired] int RolloutPercent,
    [property: JsonRequired] Dictionary<string, bool> Overrides,
    [property: JsonRequired] FlagRule[] Rules);

public sealed record Configuration(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string Environment,
    [property: JsonRequired] long Revision,
    [property: JsonRequired] Dictionary<string, FlagDefinition> Flags)
{
    public const int CurrentSchema = 1;
    public const int MaxBytes = 1024 * 1024;

    public static JsonSerializerOptions Json { get; } = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            AllowDuplicateProperties = false
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static bool IsKey(string? value) =>
        value is { Length: > 0 and <= 64 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public void Validate()
    {
        if (SchemaVersion != CurrentSchema || Revision < 1 || !IsKey(Environment) ||
            Flags is null || Flags.Count > 500)
            throw new InvalidDataException("Invalid snapshot envelope.");

        foreach (var (key, flag) in Flags)
        {
            if (!IsKey(key) || flag is null || flag.RolloutPercent is < 0 or > 100 ||
                flag.Overrides is null || flag.Overrides.Count > 500 ||
                flag.Overrides.Keys.Any(id => id.Length is 0 or > 256) ||
                flag.Rules is null || flag.Rules.Length > 50 ||
                flag.Rules.Any(rule => rule is null || !IsKey(rule.Attribute) ||
                    rule.EqualsValue is null || rule.EqualsValue.Length > 256))
                throw new InvalidDataException("Invalid flag definition.");
        }
    }

    internal Snapshot Freeze()
    {
        Validate();
        return new Snapshot(Revision, Flags.ToFrozenDictionary(
            pair => pair.Key,
            pair => new LocalFlag(pair.Value.Default, pair.Value.RolloutPercent,
                pair.Value.Overrides.ToFrozenDictionary(StringComparer.Ordinal), pair.Value.Rules),
            StringComparer.Ordinal));
    }
}

internal sealed record Snapshot(long Revision, FrozenDictionary<string, LocalFlag> Flags);

internal sealed record LocalFlag(bool Default, int RolloutPercent,
    FrozenDictionary<string, bool> Overrides, FlagRule[] Rules);

/// <summary>Inputs for local evaluation. Attributes stay in the caller's process.</summary>
public sealed record FlagUser
{
    /// <param name="id">Nonempty user ID with well-formed UTF-16.</param>
    /// <param name="attributes">Optional attributes. Keep the supplied dictionary stable during evaluation.</param>
    public FlagUser(string id, IReadOnlyDictionary<string, string>? attributes = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        Rollout.ValidateUserIdEncoding(id);
        Id = id;
        Attributes = attributes;
    }

    public string Id { get; }
    public IReadOnlyDictionary<string, string>? Attributes { get; }
}

public readonly record struct Evaluation(long? Revision, bool Enabled);
