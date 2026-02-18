using System.Numerics;
using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Tagmetry.Adapters.IO;

public sealed class DuplicateDetectionEngine {
    public async Task<DuplicateDetectionReport> AnalyzeAsync(
        IEnumerable<string> imagePaths,
        DuplicateDetectionOptions? options = null,
        CancellationToken cancellationToken = default) {

        ArgumentNullException.ThrowIfNull(imagePaths);
        var resolvedOptions = options ?? new DuplicateDetectionOptions();

        var files = imagePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        var fingerprints = new List<ImageFingerprint>(files.Length);
        foreach (var file in files) {
            cancellationToken.ThrowIfCancellationRequested();
            fingerprints.Add(await BuildFingerprintAsync(file, cancellationToken));
        }

        var exactGroups = fingerprints
            .GroupBy(x => x.Sha256, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select((group, index) => new ExactDuplicateGroup(
                $"exact-{index + 1}",
                group.Key,
                group.Select(x => x.Path).OrderBy(x => x, StringComparer.Ordinal).ToArray()))
            .ToArray();

        var findings = BuildNearDuplicateFindings(fingerprints, exactGroups, resolvedOptions);
        var nearGroups = BuildNearDuplicateGroups(fingerprints, findings, resolvedOptions);

        return new DuplicateDetectionReport(
            fingerprints.Count,
            exactGroups,
            nearGroups,
            findings,
            DateTimeOffset.UtcNow);
    }

    private static IReadOnlyList<NearDuplicateFinding> BuildNearDuplicateFindings(
        IReadOnlyList<ImageFingerprint> fingerprints,
        IReadOnlyList<ExactDuplicateGroup> exactGroups,
        DuplicateDetectionOptions options) {

        var exactMembership = exactGroups
            .SelectMany(group => group.Paths.Select(path => new { path, group.GroupId }))
            .ToDictionary(x => x.path, x => x.GroupId, StringComparer.Ordinal);

        var findings = new List<NearDuplicateFinding>();
        for (var i = 0; i < fingerprints.Count; i++) {
            for (var j = i + 1; j < fingerprints.Count; j++) {
                var left = fingerprints[i];
                var right = fingerprints[j];

                if (exactMembership.TryGetValue(left.Path, out var leftGroup) &&
                    exactMembership.TryGetValue(right.Path, out var rightGroup) &&
                    string.Equals(leftGroup, rightGroup, StringComparison.Ordinal)) {
                    continue;
                }

                var distance = BitOperations.PopCount(left.PerceptualHash ^ right.PerceptualHash);
                var band = Classify(distance, options);
                if (band == NearDuplicateBand.None) {
                    continue;
                }

                findings.Add(new NearDuplicateFinding(
                    left.Path,
                    right.Path,
                    distance,
                    band,
                    SimilarityScore(distance)));
            }
        }

        return findings
            .OrderBy(x => x.Band)
            .ThenBy(x => x.HammingDistance)
            .ThenBy(x => x.LeftPath, StringComparer.Ordinal)
            .ThenBy(x => x.RightPath, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<NearDuplicateGroup> BuildNearDuplicateGroups(
        IReadOnlyList<ImageFingerprint> fingerprints,
        IReadOnlyList<NearDuplicateFinding> findings,
        DuplicateDetectionOptions options) {

        var indexByPath = fingerprints
            .Select((fingerprint, index) => new { fingerprint.Path, index })
            .ToDictionary(x => x.Path, x => x.index, StringComparer.Ordinal);

        var uf = new UnionFind(fingerprints.Count);
        foreach (var finding in findings.Where(x => x.Band == NearDuplicateBand.Likely)) {
            uf.Union(indexByPath[finding.LeftPath], indexByPath[finding.RightPath]);
        }

        var grouped = fingerprints
            .Select((f, idx) => new { f.Path, Root = uf.Find(idx) })
            .GroupBy(x => x.Root)
            .Where(g => g.Count() > 1)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Min(x => x.Path), StringComparer.Ordinal)
            .ToArray();

        var groups = new List<NearDuplicateGroup>();
        for (var i = 0; i < grouped.Length; i++) {
            var members = grouped[i].Select(x => x.Path).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var membersSet = members.ToHashSet(StringComparer.Ordinal);

            var groupFindings = findings
                .Where(f => membersSet.Contains(f.LeftPath) && membersSet.Contains(f.RightPath))
                .ToArray();

            var likely = groupFindings.Count(x => x.Band == NearDuplicateBand.Likely);
            var maybe = groupFindings.Count(x => x.Band == NearDuplicateBand.Maybe);
            var score = groupFindings.Length == 0
                ? SimilarityScore(options.LikelyHammingThreshold)
                : groupFindings.Average(x => x.Score);

            groups.Add(new NearDuplicateGroup(
                $"near-{i + 1}",
                members,
                score,
                likely,
                maybe));
        }

        return groups;
    }

    private static NearDuplicateBand Classify(int hammingDistance, DuplicateDetectionOptions options) {
        if (hammingDistance <= options.LikelyHammingThreshold) {
            return NearDuplicateBand.Likely;
        }

        if (hammingDistance <= options.MaybeHammingThreshold) {
            return NearDuplicateBand.Maybe;
        }

        return NearDuplicateBand.None;
    }

    private static double SimilarityScore(int hammingDistance) => 1.0 - (hammingDistance / 64.0);

    private static async Task<ImageFingerprint> BuildFingerprintAsync(string path, CancellationToken cancellationToken) {
        if (!File.Exists(path)) {
            throw new FileNotFoundException("Image file does not exist.", path);
        }

        var sha256 = await ComputeSha256Async(path, cancellationToken);
        var perceptualHash = await ComputePerceptualHashAsync(path, cancellationToken);

        return new ImageFingerprint(path, sha256, perceptualHash);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken) {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<ulong> ComputePerceptualHashAsync(string path, CancellationToken cancellationToken) {
        using var image = await Image.LoadAsync<Rgba32>(path, cancellationToken);
        image.Mutate(ctx => ctx.Resize(new ResizeOptions {
            Size = new Size(32, 32),
            Sampler = KnownResamplers.Bicubic,
            Mode = ResizeMode.Stretch
        }).Grayscale());

        var matrix = new double[32, 32];
        for (var y = 0; y < 32; y++) {
            var row = image.GetPixelRowSpan(y);
            for (var x = 0; x < 32; x++) {
                var pixel = row[x];
                matrix[y, x] = pixel.R;
            }
        }

        var dct = ComputeDct2D(matrix, 32, 32);
        var low = new List<double>(64);
        for (var v = 0; v < 8; v++) {
            for (var u = 0; u < 8; u++) {
                if (u == 0 && v == 0) {
                    continue;
                }

                low.Add(dct[v, u]);
            }
        }

        var median = Median(low);
        ulong hash = 0;
        var bit = 0;
        for (var v = 0; v < 8; v++) {
            for (var u = 0; u < 8; u++) {
                if (u == 0 && v == 0) {
                    hash |= 0UL << bit;
                    bit++;
                    continue;
                }

                if (dct[v, u] > median) {
                    hash |= 1UL << bit;
                }

                bit++;
            }
        }

        return hash;
    }

    private static double[,] ComputeDct2D(double[,] input, int width, int height) {
        var output = new double[height, width];
        for (var v = 0; v < height; v++) {
            for (var u = 0; u < width; u++) {
                var sum = 0.0;
                for (var y = 0; y < height; y++) {
                    for (var x = 0; x < width; x++) {
                        sum += input[y, x]
                            * Math.Cos(((2 * x + 1) * u * Math.PI) / (2.0 * width))
                            * Math.Cos(((2 * y + 1) * v * Math.PI) / (2.0 * height));
                    }
                }

                var cu = u == 0 ? Math.Sqrt(1.0 / width) : Math.Sqrt(2.0 / width);
                var cv = v == 0 ? Math.Sqrt(1.0 / height) : Math.Sqrt(2.0 / height);
                output[v, u] = cu * cv * sum;
            }
        }

        return output;
    }

    private static double Median(IReadOnlyList<double> values) {
        var sorted = values.OrderBy(x => x).ToArray();
        if (sorted.Length == 0) {
            return 0;
        }

        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private sealed record ImageFingerprint(string Path, string Sha256, ulong PerceptualHash);

    private sealed class UnionFind {
        private readonly int[] _parent;
        private readonly int[] _rank;

        public UnionFind(int size) {
            _parent = Enumerable.Range(0, size).ToArray();
            _rank = new int[size];
        }

        public int Find(int x) {
            if (_parent[x] != x) {
                _parent[x] = Find(_parent[x]);
            }

            return _parent[x];
        }

        public void Union(int a, int b) {
            var ra = Find(a);
            var rb = Find(b);
            if (ra == rb) {
                return;
            }

            if (_rank[ra] < _rank[rb]) {
                _parent[ra] = rb;
            }
            else if (_rank[ra] > _rank[rb]) {
                _parent[rb] = ra;
            }
            else {
                _parent[rb] = ra;
                _rank[ra]++;
            }
        }
    }
}

public sealed record DuplicateDetectionOptions(
    int LikelyHammingThreshold = 6,
    int MaybeHammingThreshold = 12);

public sealed record DuplicateDetectionReport(
    int TotalFiles,
    IReadOnlyList<ExactDuplicateGroup> ExactDuplicateGroups,
    IReadOnlyList<NearDuplicateGroup> NearDuplicateGroups,
    IReadOnlyList<NearDuplicateFinding> NearDuplicateFindings,
    DateTimeOffset GeneratedAtUtc);

public sealed record ExactDuplicateGroup(
    string GroupId,
    string ExactSha256,
    IReadOnlyList<string> Paths);

public sealed record NearDuplicateGroup(
    string GroupId,
    IReadOnlyList<string> Paths,
    double Score,
    int LikelyPairCount,
    int MaybePairCount);

public sealed record NearDuplicateFinding(
    string LeftPath,
    string RightPath,
    int HammingDistance,
    NearDuplicateBand Band,
    double Score);

public enum NearDuplicateBand {
    Likely,
    Maybe,
    None
}
