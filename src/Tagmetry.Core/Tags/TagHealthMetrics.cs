using System.Text;
using System.Text.Json;

namespace Tagmetry.Core.Tags;

public static class TagHealthMetricsEvaluator {
    public static TagHealthMetricsReport Evaluate(
        IEnumerable<IReadOnlyCollection<string>> datasetTags,
        TagHealthMetricsOptions? options = null) {

        ArgumentNullException.ThrowIfNull(datasetTags);
        var resolvedOptions = options ?? new TagHealthMetricsOptions();

        var samples = datasetTags.Select(tags => tags ?? Array.Empty<string>()).ToList();
        var sampleCount = samples.Count;
        var tokenCount = samples.Sum(x => x.Count);

        var frequencies = samples
            .SelectMany(x => x)
            .GroupBy(x => x, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var totalTokens = Math.Max(1, frequencies.Values.Sum());
        var probabilities = frequencies
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new KeyValuePair<string, double>(x.Key, x.Value / (double)totalTokens))
            .ToList();

        var entropy = ComputeEntropy(probabilities.Select(x => x.Value));
        var effectiveTagCount = Math.Exp(entropy);
        var gini = ComputeGini(probabilities.Select(x => x.Value));
        var hhi = probabilities.Sum(x => x.Value * x.Value);

        var topKMass = resolvedOptions.TopKValues
            .Distinct()
            .OrderBy(x => x)
            .ToDictionary(
                k => k,
                k => probabilities.Take(Math.Max(0, k)).Sum(x => x.Value));

        var jsd = ComputeJensenShannonDivergence(probabilities, resolvedOptions.TargetDistribution);
        var stopTagCandidates = ComputeStopTagCandidates(samples, resolvedOptions);
        var pmiAnomalies = ComputePmiAnomalies(samples, resolvedOptions);
        var communityHint = ComputeCommunityHint(samples, resolvedOptions);
        var nearDuplicate = ComputeNearDuplicateHook(sampleCount, resolvedOptions.NearDuplicateGroupBySampleIndex);
        var tokenLengthOverflowRate = ComputeTokenLengthOverflowRate(samples, resolvedOptions.MaxTokenLength);

        return new TagHealthMetricsReport(
            sampleCount,
            tokenCount,
            frequencies.Count,
            entropy,
            effectiveTagCount,
            gini,
            hhi,
            topKMass,
            jsd,
            stopTagCandidates,
            pmiAnomalies,
            communityHint,
            nearDuplicate,
            tokenLengthOverflowRate,
            DateTimeOffset.UtcNow);
    }

    public static async Task WriteReportAsync(
        TagHealthMetricsReport report,
        string jsonReportPath,
        string markdownSummaryPath,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default) {

        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonReportPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(markdownSummaryPath);

        Directory.CreateDirectory(Path.GetDirectoryName(jsonReportPath) ?? ".");
        Directory.CreateDirectory(Path.GetDirectoryName(markdownSummaryPath) ?? ".");

        var options = jsonSerializerOptions ?? new JsonSerializerOptions {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        var json = JsonSerializer.Serialize(report, options);
        await File.WriteAllTextAsync(jsonReportPath, json, Encoding.UTF8, cancellationToken);

        var markdown = ToMarkdownSummary(report);
        await File.WriteAllTextAsync(markdownSummaryPath, markdown, Encoding.UTF8, cancellationToken);
    }

    public static string ToMarkdownSummary(TagHealthMetricsReport report) {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.AppendLine("# Tag Health Metrics Summary");
        sb.AppendLine();
        sb.AppendLine($"Generated at: `{report.GeneratedAtUtc:O}`");
        sb.AppendLine();
        sb.AppendLine("## Core Distribution Metrics (M1–M6)");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|---|---:|");
        sb.AppendLine($"| M1 Entropy | {report.Entropy:F6} |");
        sb.AppendLine($"| M2 Effective Tag Count | {report.EffectiveTagCount:F6} |");
        sb.AppendLine($"| M3 Gini | {report.Gini:F6} |");
        sb.AppendLine($"| M4 HHI | {report.Hhi:F6} |");
        sb.AppendLine($"| M6 JSD to Target | {(report.JensenShannonDivergenceToTarget.HasValue ? report.JensenShannonDivergenceToTarget.Value.ToString("F6") : "n/a")} |");
        sb.AppendLine();
        sb.AppendLine("### M5 Top-K Mass");
        foreach (var pair in report.TopKMass.OrderBy(x => x.Key)) {
            sb.AppendLine($"- Top-{pair.Key}: `{pair.Value:F6}`");
        }

        sb.AppendLine();
        sb.AppendLine("## Quality / Anomaly Metrics (M7–M11)");
        sb.AppendLine();
        sb.AppendLine("### M7 Stop-tag candidates (low IDF)");
        if (report.StopTagCandidates.Count == 0) {
            sb.AppendLine("- none");
        }
        else {
            foreach (var candidate in report.StopTagCandidates.Take(10)) {
                sb.AppendLine($"- `{candidate.Tag}` df={candidate.DocumentFrequency}, idf={candidate.InverseDocumentFrequency:F6}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("### M8 PMI anomalies");
        if (report.PmiAnomalyCooccurrences.Count == 0) {
            sb.AppendLine("- none");
        }
        else {
            foreach (var anomaly in report.PmiAnomalyCooccurrences.Take(10)) {
                sb.AppendLine($"- (`{anomaly.LeftTag}`, `{anomaly.RightTag}`) support={anomaly.CooccurrenceCount}, pmi={anomaly.Pmi:F6}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"### M9 Community hint\n- communities: `{report.CommunityHint.CommunityCount}`\n- modularity-like score: `{report.CommunityHint.ModularityHint:F6}`");

        sb.AppendLine();
        sb.AppendLine($"### M10 Near-duplicate rate hook\n- {(report.NearDuplicateRateHook.Rate.HasValue ? report.NearDuplicateRateHook.Rate.Value.ToString("F6") : "n/a")} ({report.NearDuplicateRateHook.Note})");

        sb.AppendLine();
        sb.AppendLine($"### M11 Token-length overflow rate\n- `{report.TokenLengthOverflowRate:F6}`");

        return sb.ToString();
    }

    private static double ComputeEntropy(IEnumerable<double> probabilities) {
        var entropy = 0.0;
        foreach (var p in probabilities.Where(x => x > 0.0)) {
            entropy -= p * Math.Log(p);
        }

        return entropy;
    }

    private static double ComputeGini(IEnumerable<double> probabilities) {
        var sorted = probabilities.OrderBy(x => x).ToArray();
        if (sorted.Length == 0) {
            return 0.0;
        }

        var cumulative = 0.0;
        var weighted = 0.0;
        for (var i = 0; i < sorted.Length; i++) {
            cumulative += sorted[i];
            weighted += cumulative;
        }

        return (sorted.Length + 1 - 2 * weighted) / sorted.Length;
    }

    private static double? ComputeJensenShannonDivergence(
        IReadOnlyList<KeyValuePair<string, double>> observed,
        IReadOnlyDictionary<string, double>? targetDistribution) {

        if (targetDistribution is null || targetDistribution.Count == 0) {
            return null;
        }

        var observedMap = observed.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        var targetSum = targetDistribution.Values.Sum();
        if (targetSum <= 0) {
            return null;
        }

        var keys = observedMap.Keys.Union(targetDistribution.Keys, StringComparer.Ordinal).ToArray();

        var jsd = 0.0;
        foreach (var key in keys) {
            var p = observedMap.GetValueOrDefault(key);
            var q = targetDistribution.TryGetValue(key, out var rawQ) ? rawQ / targetSum : 0.0;
            var m = 0.5 * (p + q);

            if (p > 0) {
                jsd += 0.5 * p * Math.Log(p / m, 2);
            }

            if (q > 0) {
                jsd += 0.5 * q * Math.Log(q / m, 2);
            }
        }

        return jsd;
    }

    private static IReadOnlyList<StopTagCandidate> ComputeStopTagCandidates(
        IReadOnlyList<IReadOnlyCollection<string>> samples,
        TagHealthMetricsOptions options) {

        if (samples.Count == 0) {
            return Array.Empty<StopTagCandidate>();
        }

        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var sample in samples) {
            foreach (var tag in sample.Distinct(StringComparer.Ordinal)) {
                documentFrequency[tag] = documentFrequency.GetValueOrDefault(tag) + 1;
            }
        }

        var totalDocs = samples.Count;
        return documentFrequency
            .Select(pair => {
                var idf = Math.Log((totalDocs + 1.0) / (pair.Value + 1.0)) + 1.0;
                return new StopTagCandidate(pair.Key, pair.Value, idf);
            })
            .Where(x => x.DocumentFrequency >= options.MinDocumentFrequencyForStopCandidate)
            .OrderBy(x => x.InverseDocumentFrequency)
            .ThenByDescending(x => x.DocumentFrequency)
            .ThenBy(x => x.Tag, StringComparer.Ordinal)
            .Take(options.MaxStopTagCandidates)
            .ToArray();
    }

    private static IReadOnlyList<PmiAnomaly> ComputePmiAnomalies(
        IReadOnlyList<IReadOnlyCollection<string>> samples,
        TagHealthMetricsOptions options) {

        if (samples.Count == 0) {
            return Array.Empty<PmiAnomaly>();
        }

        var docCount = samples.Count;
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        var pairDf = new Dictionary<(string, string), int>();

        foreach (var sample in samples.Select(x => x.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray())) {
            for (var i = 0; i < sample.Length; i++) {
                df[sample[i]] = df.GetValueOrDefault(sample[i]) + 1;
                for (var j = i + 1; j < sample.Length; j++) {
                    var key = (sample[i], sample[j]);
                    pairDf[key] = pairDf.GetValueOrDefault(key) + 1;
                }
            }
        }

        return pairDf
            .Where(x => x.Value >= options.MinCooccurrenceForPmi)
            .Select(pair => {
                var pxy = pair.Value / (double)docCount;
                var px = df[pair.Key.Item1] / (double)docCount;
                var py = df[pair.Key.Item2] / (double)docCount;
                var pmi = Math.Log(pxy / (px * py), 2);
                return new PmiAnomaly(pair.Key.Item1, pair.Key.Item2, pair.Value, pmi);
            })
            .OrderByDescending(x => x.Pmi)
            .ThenByDescending(x => x.CooccurrenceCount)
            .ThenBy(x => x.LeftTag, StringComparer.Ordinal)
            .ThenBy(x => x.RightTag, StringComparer.Ordinal)
            .Take(options.MaxPmiAnomalies)
            .ToArray();
    }

    private static CommunityHint ComputeCommunityHint(
        IReadOnlyList<IReadOnlyCollection<string>> samples,
        TagHealthMetricsOptions options) {

        var pairWeights = new Dictionary<(string, string), int>();
        foreach (var sample in samples.Select(x => x.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray())) {
            for (var i = 0; i < sample.Length; i++) {
                for (var j = i + 1; j < sample.Length; j++) {
                    var key = (sample[i], sample[j]);
                    pairWeights[key] = pairWeights.GetValueOrDefault(key) + 1;
                }
            }
        }

        if (pairWeights.Count == 0) {
            return new CommunityHint(0, 0.0, Array.Empty<CommunityPreview>());
        }

        var threshold = Math.Max(1, options.CommunityEdgeWeightThreshold);
        var adjacency = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var pair in pairWeights.Where(x => x.Value >= threshold)) {
            if (!adjacency.TryGetValue(pair.Key.Item1, out var left)) {
                left = new HashSet<string>(StringComparer.Ordinal);
                adjacency[pair.Key.Item1] = left;
            }

            if (!adjacency.TryGetValue(pair.Key.Item2, out var right)) {
                right = new HashSet<string>(StringComparer.Ordinal);
                adjacency[pair.Key.Item2] = right;
            }

            left.Add(pair.Key.Item2);
            right.Add(pair.Key.Item1);
        }

        var communities = new List<CommunityPreview>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in adjacency.Keys.OrderBy(x => x, StringComparer.Ordinal)) {
            if (!visited.Add(node)) {
                continue;
            }

            var queue = new Queue<string>();
            queue.Enqueue(node);
            var component = new List<string>();
            while (queue.Count > 0) {
                var current = queue.Dequeue();
                component.Add(current);
                foreach (var next in adjacency[current].OrderBy(x => x, StringComparer.Ordinal)) {
                    if (visited.Add(next)) {
                        queue.Enqueue(next);
                    }
                }
            }

            communities.Add(new CommunityPreview(component.OrderBy(x => x, StringComparer.Ordinal).Take(options.CommunityPreviewSize).ToArray(), component.Count));
        }

        var nodeCount = adjacency.Count;
        var edgeCount = adjacency.Sum(x => x.Value.Count) / 2.0;
        var modularityHint = nodeCount == 0 ? 0.0 : (communities.Count / (double)nodeCount) * (edgeCount / Math.Max(edgeCount, 1.0));

        return new CommunityHint(
            communities.Count,
            modularityHint,
            communities.OrderByDescending(x => x.Size).ThenBy(x => string.Join("\u0001", x.PreviewTags), StringComparer.Ordinal).ToArray());
    }

    private static NearDuplicateRateHook ComputeNearDuplicateHook(int sampleCount, IReadOnlyList<string?>? groupBySampleIndex) {
        if (sampleCount == 0) {
            return new NearDuplicateRateHook(0.0, "No samples.");
        }

        if (groupBySampleIndex is null || groupBySampleIndex.Count != sampleCount) {
            return new NearDuplicateRateHook(null, "No near-duplicate grouping provided.");
        }

        var duplicateSamples = groupBySampleIndex
            .GroupBy(x => x ?? string.Empty, StringComparer.Ordinal)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1)
            .Sum(g => g.Count());

        return new NearDuplicateRateHook(duplicateSamples / (double)sampleCount, "Computed from provided grouping.");
    }

    private static double ComputeTokenLengthOverflowRate(IReadOnlyList<IReadOnlyCollection<string>> samples, int maxTokenLength) {
        if (maxTokenLength <= 0) {
            return 0.0;
        }

        var tokens = samples.SelectMany(x => x).ToList();
        if (tokens.Count == 0) {
            return 0.0;
        }

        var overflow = tokens.Count(x => x.Length > maxTokenLength);
        return overflow / (double)tokens.Count;
    }
}

public sealed record TagHealthMetricsOptions {
    public IReadOnlyList<int> TopKValues { get; init; } = [1, 5, 10];
    public IReadOnlyDictionary<string, double>? TargetDistribution { get; init; }
    public int MinDocumentFrequencyForStopCandidate { get; init; } = 2;
    public int MaxStopTagCandidates { get; init; } = 50;
    public int MinCooccurrenceForPmi { get; init; } = 2;
    public int MaxPmiAnomalies { get; init; } = 50;
    public int CommunityEdgeWeightThreshold { get; init; } = 2;
    public int CommunityPreviewSize { get; init; } = 5;
    public IReadOnlyList<string?>? NearDuplicateGroupBySampleIndex { get; init; }
    public int MaxTokenLength { get; init; } = 64;
}

public sealed record TagHealthMetricsReport(
    int SampleCount,
    int TokenCount,
    int UniqueTagCount,
    double Entropy,
    double EffectiveTagCount,
    double Gini,
    double Hhi,
    IReadOnlyDictionary<int, double> TopKMass,
    double? JensenShannonDivergenceToTarget,
    IReadOnlyList<StopTagCandidate> StopTagCandidates,
    IReadOnlyList<PmiAnomaly> PmiAnomalyCooccurrences,
    CommunityHint CommunityHint,
    NearDuplicateRateHook NearDuplicateRateHook,
    double TokenLengthOverflowRate,
    DateTimeOffset GeneratedAtUtc);

public sealed record StopTagCandidate(string Tag, int DocumentFrequency, double InverseDocumentFrequency);

public sealed record PmiAnomaly(string LeftTag, string RightTag, int CooccurrenceCount, double Pmi);

public sealed record CommunityHint(int CommunityCount, double ModularityHint, IReadOnlyList<CommunityPreview> Communities);

public sealed record CommunityPreview(IReadOnlyList<string> PreviewTags, int Size);

public sealed record NearDuplicateRateHook(double? Rate, string Note);
