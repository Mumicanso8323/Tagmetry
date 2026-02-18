using Tagmetry.Core.Tags;

namespace Tagmetry.Core.Tests;

public class TagHealthRecommendationEngineTests {
    [Fact]
    public void Evaluate_ReturnsExplainableMatchesFromJsonRules() {
        var report = BuildReport();
        var rules = TagHealthRecommendationEngine.LoadRulesFromJson(
            """
            {
              "rules": [
                {
                  "id": "dup-high",
                  "description": "Near duplicates are elevated.",
                  "severity": "Critical",
                  "conditions": [
                    { "signal": "nearDuplicateRate", "operator": "GreaterThan", "value": 0.2 },
                    { "signal": "topKMass:1", "operator": "GreaterThanOrEqual", "value": 0.3 }
                  ],
                  "likelyFailureModes": ["duplicate-heavy ingestion"],
                  "actions": ["dedupe by perceptual hash", "re-sample long tail"]
                },
                {
                  "id": "overflow", 
                  "description": "Overflowed tags may break model tokenizer.",
                  "severity": "Warning",
                  "conditions": [
                    { "signal": "tokenLengthOverflowRate", "operator": "GreaterThan", "value": 0.05 }
                  ],
                  "likelyFailureModes": ["overly long generated tags"],
                  "actions": ["truncate or normalize long tokens"]
                }
              ]
            }
            """);

        var evaluation = TagHealthRecommendationEngine.Evaluate(report, rules);

        Assert.Equal(2, evaluation.Matches.Count);
        var first = evaluation.Matches.Single(x => x.RuleId == "dup-high");
        Assert.Equal(RecommendationSeverity.Critical, first.Severity);
        Assert.All(first.Conditions, condition => Assert.True(condition.IsMatch));
        Assert.All(first.Conditions, condition => Assert.NotNull(condition.ActualValue));
        Assert.Contains("duplicate-heavy ingestion", first.LikelyFailureModes);
        Assert.Contains("dedupe by perceptual hash", first.Actions);

        var overflow = evaluation.Matches.Single(x => x.RuleId == "overflow");
        Assert.Equal("Overflowed tags may break model tokenizer.", overflow.Description);
        Assert.Single(overflow.Conditions);
        Assert.True(overflow.Conditions[0].IsMatch);
        Assert.Equal("Condition matched.", overflow.Conditions[0].Explanation);
    }

    [Fact]
    public void LoadRulesFromYaml_ParsesAndEvaluates() {
        var report = BuildReport();
        var rules = TagHealthRecommendationEngine.LoadRulesFromYaml(
            """
            rules:
              - id: jsd-drift
                description: Distribution drift from target prior.
                severity: Warning
                conditions:
                  - signal: jsdToTarget
                    operator: GreaterThanOrEqual
                    value: 0.1
                likelyFailureModes:
                  - target prior mismatch
                actions:
                  - update balancing strategy
            """);

        var evaluation = TagHealthRecommendationEngine.Evaluate(report, rules);

        var match = Assert.Single(evaluation.Matches);
        Assert.Equal("jsd-drift", match.RuleId);
        Assert.Equal(RecommendationSeverity.Warning, match.Severity);
        Assert.Equal("target prior mismatch", Assert.Single(match.LikelyFailureModes));
        Assert.Equal("update balancing strategy", Assert.Single(match.Actions));
    }

    [Fact]
    public void Evaluate_ReportsMissingSignalAsNonMatch() {
        var report = BuildReport();
        var rules = new[] {
            new RecommendationRule(
                "bad-signal",
                "Uses unknown signal",
                RecommendationSeverity.Info,
                [new RecommendationCondition("unknownMetric", RecommendationOperator.GreaterThan, 0.1)],
                ["rule misconfiguration"],
                ["fix signal name"]) 
        };

        var evaluation = TagHealthRecommendationEngine.Evaluate(report, rules);
        Assert.Empty(evaluation.Matches);

        // EvaluateCondition is internal to match path; ensure rule still loadable and safely skipped.
    }

    private static TagHealthMetricsReport BuildReport() {
        return new TagHealthMetricsReport(
            SampleCount: 100,
            TokenCount: 1000,
            UniqueTagCount: 120,
            Entropy: 3.5,
            EffectiveTagCount: 33.1,
            Gini: 0.45,
            Hhi: 0.13,
            TopKMass: new Dictionary<int, double> {
                [1] = 0.4,
                [5] = 0.75
            },
            JensenShannonDivergenceToTarget: 0.2,
            StopTagCandidates: [new StopTagCandidate("masterpiece", 92, 1.01)],
            PmiAnomalyCooccurrences: [new PmiAnomaly("1girl", "watermark", 12, 4.2)],
            CommunityHint: new CommunityHint(3, 0.31, [new CommunityPreview(["cat", "cute"], 10)]),
            NearDuplicateRateHook: new NearDuplicateRateHook(0.3, "hook"),
            TokenLengthOverflowRate: 0.07,
            GeneratedAtUtc: DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    }
}
