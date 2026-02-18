using System.Collections.ObjectModel;
using System.Text.Json;

namespace Tagmetry.Core.Tags;

public sealed class TagNormalizer {
    private readonly TagNormalizationRules _rules;
    private readonly Dictionary<string, string> _aliasMap;
    private readonly HashSet<string> _stopTags;

    public TagNormalizer(TagNormalizationRules rules) {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _aliasMap = BuildAliasMap(rules);
        _stopTags = BuildStopTags(rules);
    }

    public TagNormalizationResult Normalize(IEnumerable<string> tokens) {
        ArgumentNullException.ThrowIfNull(tokens);

        var tokenResults = new List<TagNormalizationTokenResult>();
        var normalizedTokens = new List<string>();

        foreach (var source in tokens) {
            var token = source ?? string.Empty;
            var audit = new List<TagAuditEvent>();

            var afterCaseFold = token.ToLowerInvariant();
            audit.Add(TagAuditEvent.Create(TagAuditAction.CaseFold, token, afterCaseFold));

            var afterDelimiterNormalization = NormalizeDelimiters(afterCaseFold);
            audit.Add(TagAuditEvent.Create(TagAuditAction.DelimiterNormalization, afterCaseFold, afterDelimiterNormalization));

            var afterAliasMapping = _aliasMap.TryGetValue(afterDelimiterNormalization, out var alias)
                ? alias
                : afterDelimiterNormalization;
            audit.Add(TagAuditEvent.Create(TagAuditAction.AliasMapping, afterDelimiterNormalization, afterAliasMapping));

            var isStopTag = _stopTags.Contains(afterAliasMapping);
            audit.Add(TagAuditEvent.Create(TagAuditAction.StopTagFiltering, afterAliasMapping, isStopTag ? null : afterAliasMapping,
                isStopTag ? "Filtered by stop-tag rule." : "Token retained."));

            if (!isStopTag) {
                normalizedTokens.Add(afterAliasMapping);
            }

            tokenResults.Add(new TagNormalizationTokenResult(token, isStopTag ? null : afterAliasMapping, isStopTag, audit.AsReadOnly()));
        }

        return new TagNormalizationResult(tokenResults.AsReadOnly(), normalizedTokens.AsReadOnly());
    }

    private Dictionary<string, string> BuildAliasMap(TagNormalizationRules rules) {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in rules.Aliases) {
            var key = NormalizeDelimiters(pair.Key.ToLowerInvariant());
            var value = NormalizeDelimiters(pair.Value.ToLowerInvariant());
            map[key] = value;
        }

        return map;
    }

    private HashSet<string> BuildStopTags(TagNormalizationRules rules) {
        var stopTags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in rules.StopTags) {
            stopTags.Add(NormalizeDelimiters(tag.ToLowerInvariant()));
        }

        return stopTags;
    }

    private string NormalizeDelimiters(string token) {
        var normalized = token.Trim();
        foreach (var delimiter in _rules.Delimiters.Where(x => !string.IsNullOrWhiteSpace(x)).OrderByDescending(x => x.Length, StringComparer.Ordinal)) {
            normalized = normalized.Replace(delimiter, _rules.CanonicalDelimiter, StringComparison.Ordinal);
        }

        if (string.IsNullOrEmpty(_rules.CanonicalDelimiter)) {
            return normalized;
        }

        while (normalized.Contains(_rules.CanonicalDelimiter + _rules.CanonicalDelimiter, StringComparison.Ordinal)) {
            normalized = normalized.Replace(_rules.CanonicalDelimiter + _rules.CanonicalDelimiter, _rules.CanonicalDelimiter, StringComparison.Ordinal);
        }

        return normalized.Trim();
    }
}

public sealed record TagNormalizationResult(
    IReadOnlyList<TagNormalizationTokenResult> Tokens,
    IReadOnlyList<string> NormalizedTokens);

public sealed record TagNormalizationTokenResult(
    string OriginalToken,
    string? NormalizedToken,
    bool IsFiltered,
    IReadOnlyList<TagAuditEvent> AuditTrail);

public sealed record TagAuditEvent(
    TagAuditAction Action,
    string Before,
    string? After,
    string Message) {

    public static TagAuditEvent Create(TagAuditAction action, string before, string? after, string? message = null) {
        var resolvedMessage = message ??
            (string.Equals(before, after, StringComparison.Ordinal)
                ? "No change."
                : $"Transformed '{before}' to '{after}'.");

        return new TagAuditEvent(action, before, after, resolvedMessage);
    }
}

public enum TagAuditAction {
    CaseFold,
    DelimiterNormalization,
    AliasMapping,
    StopTagFiltering
}

public sealed record TagNormalizationRules(
    string CanonicalDelimiter,
    IReadOnlyList<string> Delimiters,
    IReadOnlyDictionary<string, string> Aliases,
    IReadOnlyList<string> StopTags) {

    public static TagNormalizationRules FromJson(string json, JsonSerializerOptions? options = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var dto = JsonSerializer.Deserialize<TagNormalizationRulesDto>(json, options ?? CreateJsonOptions())
            ?? throw new InvalidOperationException("JSON ruleset cannot be deserialized.");

        return new TagNormalizationRules(
            string.IsNullOrWhiteSpace(dto.CanonicalDelimiter) ? " " : dto.CanonicalDelimiter,
            new ReadOnlyCollection<string>(dto.Delimiters ?? []),
            new ReadOnlyDictionary<string, string>(dto.Aliases ?? new Dictionary<string, string>(StringComparer.Ordinal)),
            new ReadOnlyCollection<string>(dto.StopTags ?? []));
    }

    private static JsonSerializerOptions CreateJsonOptions() => new() {
        PropertyNameCaseInsensitive = true
    };

    private sealed class TagNormalizationRulesDto {
        public string? CanonicalDelimiter { get; init; }
        public List<string>? Delimiters { get; init; }
        public Dictionary<string, string>? Aliases { get; init; }
        public List<string>? StopTags { get; init; }
    }
}
