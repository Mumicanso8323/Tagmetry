using System.Text.Json;
using Tagmetry.Core.Tags;

namespace Tagmetry.Core.Tests;

public class TagHealthMetricsEvaluatorTests {
    [Fact]
    public void Evaluate_ComputesM1ToM11Deterministically() {
        var samples = new List<IReadOnlyCollection<string>> {
            new[] { "cat", "cute", "blue" },
            new[] { "cat", "cute", "blue" },
            new[] { "dog", "cute", "long_token_overflow" },
            new[] { "dog", "calm", "blue" }
        };

        var report = TagHealthMetricsEvaluator.Evaluate(samples, new TagHealthMetricsOptions {
            TopKValues = [1, 2, 3],
            TargetDistribution = new Dictionary<string, double> {
                ["cat"] = 0.5,
                ["dog"] = 0.5
            },
            MinDocumentFrequencyForStopCandidate = 2,
            MinCooccurrenceForPmi = 2,
            CommunityEdgeWeightThreshold = 2,
            NearDuplicateGroupBySampleIndex = ["a", "a", null, "b"],
            MaxTokenLength = 8
        });

        Assert.Equal(4, report.SampleCount);
        Assert.Equal(12, report.TokenCount);
        Assert.Equal(6, report.UniqueTagCount);

        Assert.True(report.Entropy > 0);
        Assert.True(report.EffectiveTagCount > 0);
        Assert.InRange(report.Gini, 0, 1);
        Assert.InRange(report.Hhi, 0, 1);

        Assert.Equal(3, report.TopKMass.Count);
        Assert.True(report.TopKMass[1] <= report.TopKMass[2]);
        Assert.True(report.TopKMass[2] <= report.TopKMass[3]);

        Assert.NotNull(report.JensenShannonDivergenceToTarget);
        Assert.True(report.JensenShannonDivergenceToTarget >= 0);

        Assert.NotEmpty(report.StopTagCandidates);
        Assert.Contains(report.StopTagCandidates, x => x.Tag == "blue" || x.Tag == "cute");

        Assert.NotEmpty(report.PmiAnomalyCooccurrences);
        Assert.True(report.CommunityHint.CommunityCount >= 1);
        Assert.NotNull(report.NearDuplicateRateHook.Rate);
        Assert.Equal(0.25, report.NearDuplicateRateHook.Rate!.Value, precision: 6);
        Assert.True(report.TokenLengthOverflowRate > 0);
    }

    [Fact]
    public async Task WriteReportAsync_WritesMachineAndHumanReadableFiles() {
        var report = TagHealthMetricsEvaluator.Evaluate(new List<IReadOnlyCollection<string>> {
            new[] { "cat", "cute" },
            new[] { "dog" }
        });

        var root = Path.Combine(Path.GetTempPath(), "tagmetry-health-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var jsonPath = Path.Combine(root, "report.json");
        var mdPath = Path.Combine(root, "report.md");

        try {
            await TagHealthMetricsEvaluator.WriteReportAsync(report, jsonPath, mdPath);

            Assert.True(File.Exists(jsonPath));
            Assert.True(File.Exists(mdPath));

            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath));
            Assert.True(json.RootElement.TryGetProperty("entropy", out _));
            Assert.True(json.RootElement.TryGetProperty("tokenLengthOverflowRate", out _));

            var markdown = await File.ReadAllTextAsync(mdPath);
            Assert.Contains("# Tag Health Metrics Summary", markdown);
            Assert.Contains("M1 Entropy", markdown);
            Assert.Contains("M11 Token-length overflow rate", markdown);
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }
}
