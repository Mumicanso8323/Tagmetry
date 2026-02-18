using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Tagmetry.Core.Tags;

public static class TagHealthRecommendationEngine {
    public static RecommendationEvaluation Evaluate(
        TagHealthMetricsReport metrics,
        IReadOnlyList<RecommendationRule> rules) {

        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(rules);

        var results = new List<RecommendationMatch>();

        foreach (var rule in rules.OrderBy(x => x.Id, StringComparer.Ordinal)) {
            var evaluations = rule.Conditions.Select(condition => EvaluateCondition(metrics, condition)).ToArray();
            if (evaluations.All(x => x.IsMatch)) {
                results.Add(new RecommendationMatch(
                    rule.Id,
                    rule.Severity,
                    rule.LikelyFailureModes,
                    rule.Actions,
                    evaluations,
                    rule.Description));
            }
        }

        return new RecommendationEvaluation(metrics.GeneratedAtUtc, results);
    }

    public static IReadOnlyList<RecommendationRule> LoadRulesFromJson(string json, JsonSerializerOptions? options = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var resolvedOptions = options ?? new JsonSerializerOptions {
            PropertyNameCaseInsensitive = true
        };

        var envelope = JsonSerializer.Deserialize<RecommendationRulesEnvelope>(json, resolvedOptions)
            ?? throw new InvalidOperationException("Unable to deserialize recommendation rules JSON.");

        return Normalize(envelope.Rules ?? []);
    }

    public static IReadOnlyList<RecommendationRule> LoadRulesFromYaml(string yaml) {
        ArgumentException.ThrowIfNullOrWhiteSpace(yaml);

        var deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        var envelope = deserializer.Deserialize<RecommendationRulesEnvelope>(yaml)
            ?? throw new InvalidOperationException("Unable to deserialize recommendation rules YAML.");

        return Normalize(envelope.Rules ?? []);
    }

    private static IReadOnlyList<RecommendationRule> Normalize(IReadOnlyList<RecommendationRule> rules) {
        return rules
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .Select(x => x with {
                Conditions = x.Conditions ?? [],
                LikelyFailureModes = x.LikelyFailureModes ?? [],
                Actions = x.Actions ?? []
            })
            .ToArray();
    }

    private static RecommendationConditionEvaluation EvaluateCondition(TagHealthMetricsReport metrics, RecommendationCondition condition) {
        var signal = ResolveSignal(metrics, condition.Signal);
        if (!signal.HasValue) {
            return new RecommendationConditionEvaluation(condition.Signal, condition.Operator, condition.Value, null, false, "Signal not found.");
        }

        var isMatch = condition.Operator switch {
            RecommendationOperator.GreaterThan => signal.Value > condition.Value,
            RecommendationOperator.GreaterThanOrEqual => signal.Value >= condition.Value,
            RecommendationOperator.LessThan => signal.Value < condition.Value,
            RecommendationOperator.LessThanOrEqual => signal.Value <= condition.Value,
            RecommendationOperator.Equal => Math.Abs(signal.Value - condition.Value) < 1e-12,
            RecommendationOperator.NotEqual => Math.Abs(signal.Value - condition.Value) >= 1e-12,
            _ => false
        };

        return new RecommendationConditionEvaluation(condition.Signal, condition.Operator, condition.Value, signal.Value, isMatch,
            isMatch ? "Condition matched." : "Condition did not match.");
    }

    private static double? ResolveSignal(TagHealthMetricsReport metrics, string signal) {
        return signal switch {
            "sampleCount" => metrics.SampleCount,
            "tokenCount" => metrics.TokenCount,
            "uniqueTagCount" => metrics.UniqueTagCount,
            "entropy" => metrics.Entropy,
            "effectiveTagCount" => metrics.EffectiveTagCount,
            "gini" => metrics.Gini,
            "hhi" => metrics.Hhi,
            "jsdToTarget" => metrics.JensenShannonDivergenceToTarget,
            "stopTagCandidatesCount" => metrics.StopTagCandidates.Count,
            "pmiAnomaliesCount" => metrics.PmiAnomalyCooccurrences.Count,
            "communityCount" => metrics.CommunityHint.CommunityCount,
            "modularityHint" => metrics.CommunityHint.ModularityHint,
            "nearDuplicateRate" => metrics.NearDuplicateRateHook.Rate,
            "tokenLengthOverflowRate" => metrics.TokenLengthOverflowRate,
            _ when signal.StartsWith("topKMass:", StringComparison.Ordinal) => ResolveTopK(metrics, signal),
            _ => null
        };
    }

    private static double? ResolveTopK(TagHealthMetricsReport metrics, string signal) {
        var parts = signal.Split(':', 2);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var k)) {
            return null;
        }

        return metrics.TopKMass.TryGetValue(k, out var value) ? value : null;
    }

    private sealed class RecommendationRulesEnvelope {
        public List<RecommendationRule>? Rules { get; init; }
    }
}

public sealed record RecommendationRule(
    string Id,
    string Description,
    RecommendationSeverity Severity,
    IReadOnlyList<RecommendationCondition> Conditions,
    IReadOnlyList<string> LikelyFailureModes,
    IReadOnlyList<string> Actions);

public sealed record RecommendationCondition(string Signal, RecommendationOperator Operator, double Value);

public enum RecommendationOperator {
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Equal,
    NotEqual
}

public enum RecommendationSeverity {
    Info,
    Warning,
    Critical
}

public sealed record RecommendationConditionEvaluation(
    string Signal,
    RecommendationOperator Operator,
    double ExpectedValue,
    double? ActualValue,
    bool IsMatch,
    string Explanation);

public sealed record RecommendationMatch(
    string RuleId,
    RecommendationSeverity Severity,
    IReadOnlyList<string> LikelyFailureModes,
    IReadOnlyList<string> Actions,
    IReadOnlyList<RecommendationConditionEvaluation> Conditions,
    string Description);

public sealed record RecommendationEvaluation(
    DateTimeOffset EvaluatedAtUtc,
    IReadOnlyList<RecommendationMatch> Matches);
