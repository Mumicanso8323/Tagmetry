using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SixLabors.ImageSharp;

namespace Tagmetry.Adapters.IO;

public sealed class DatasetScanner {
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff"
    };

    private readonly JsonSerializerOptions _jsonOptions;

    public DatasetScanner(JsonSerializerOptions? jsonOptions = null) {
        _jsonOptions = jsonOptions ?? new JsonSerializerOptions {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    public async Task<DatasetScanSummary> ScanAsync(
        string datasetDirectory,
        string outputJsonlPath,
        string outputSummaryPath,
        CancellationToken cancellationToken = default) {

        ArgumentException.ThrowIfNullOrWhiteSpace(datasetDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputJsonlPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputSummaryPath);

        if (!Directory.Exists(datasetDirectory)) {
            throw new DirectoryNotFoundException($"Dataset directory does not exist: {datasetDirectory}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputJsonlPath) ?? ".");
        Directory.CreateDirectory(Path.GetDirectoryName(outputSummaryPath) ?? ".");

        var imagePaths = Directory
            .EnumerateFiles(datasetDirectory, "*", SearchOption.AllDirectories)
            .Where(path => ImageExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        var records = new List<DatasetImageRecord>(imagePaths.Length);
        await using var jsonlStream = new FileStream(outputJsonlPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(jsonlStream, new UTF8Encoding(false));

        foreach (var imagePath in imagePaths) {
            cancellationToken.ThrowIfCancellationRequested();

            var record = await BuildRecordAsync(datasetDirectory, imagePath, cancellationToken);
            records.Add(record);

            var line = JsonSerializer.Serialize(record, _jsonOptions);
            await writer.WriteLineAsync(line);
        }

        var summary = BuildSummary(datasetDirectory, outputJsonlPath, outputSummaryPath, records);
        var summaryJson = JsonSerializer.Serialize(summary, _jsonOptions with { WriteIndented = true });
        await File.WriteAllTextAsync(outputSummaryPath, summaryJson, new UTF8Encoding(false), cancellationToken);

        return new DatasetScanSummary(records.AsReadOnly(), summary);
    }

    private static async Task<DatasetImageRecord> BuildRecordAsync(string datasetRoot, string imagePath, CancellationToken cancellationToken) {
        var relativePath = Path.GetRelativePath(datasetRoot, imagePath).Replace('\\', '/');

        var info = await ComputeHashesAndSizeAsync(imagePath, cancellationToken);
        var captions = ReadCaptionSources(imagePath);

        return new DatasetImageRecord(
            relativePath,
            info.Width,
            info.Height,
            new DatasetImageHashes(info.Md5, info.Sha256),
            captions,
            new DatasetCaptionPresence(
                !string.IsNullOrWhiteSpace(captions.BooruTags),
                !string.IsNullOrWhiteSpace(captions.ShortCaption),
                !string.IsNullOrWhiteSpace(captions.StyleTags)));
    }

    private static DatasetSummaryIndex BuildSummary(string datasetDirectory, string jsonlPath, string summaryPath, List<DatasetImageRecord> records) {
        var totalPixels = records.Sum(x => (long)x.Width * x.Height);
        return new DatasetSummaryIndex(
            Path.GetFullPath(datasetDirectory),
            Path.GetFullPath(jsonlPath),
            Path.GetFullPath(summaryPath),
            records.Count,
            records.Count(x => x.CaptionPresence.HasBooruTags),
            records.Count(x => x.CaptionPresence.HasShortCaption),
            records.Count(x => x.CaptionPresence.HasStyleTags),
            totalPixels,
            records
                .GroupBy(x => Path.GetExtension(x.RelativeImagePath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key.ToLowerInvariant(), g => g.Count(), StringComparer.Ordinal));
    }

    private static async Task<(int Width, int Height, string Md5, string Sha256)> ComputeHashesAndSizeAsync(string imagePath, CancellationToken cancellationToken) {
        await using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var metadata = await Image.IdentifyAsync(stream, cancellationToken)
            ?? throw new InvalidDataException($"Unsupported image format: {imagePath}");

        stream.Position = 0;
        using var md5 = MD5.Create();
        using var sha256 = SHA256.Create();

        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0) {
            md5.TransformBlock(buffer, 0, read, null, 0);
            sha256.TransformBlock(buffer, 0, read, null, 0);
        }

        md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

        return (metadata.Width, metadata.Height, Convert.ToHexString(md5.Hash!).ToLowerInvariant(), Convert.ToHexString(sha256.Hash!).ToLowerInvariant());
    }

    private static DatasetCaptionSources ReadCaptionSources(string imagePath) {
        var directory = Path.GetDirectoryName(imagePath) ?? ".";
        var baseName = Path.GetFileNameWithoutExtension(imagePath);

        return new DatasetCaptionSources(
            ReadOptionalNormalizedText(Path.Combine(directory, baseName + ".booru.txt"))
                ?? ReadOptionalNormalizedText(Path.Combine(directory, baseName + ".tags.txt")),
            ReadOptionalNormalizedText(Path.Combine(directory, baseName + ".caption.txt"))
                ?? ReadOptionalNormalizedText(Path.Combine(directory, baseName + ".txt")),
            ReadOptionalNormalizedText(Path.Combine(directory, baseName + ".style.txt")));
    }

    private static string? ReadOptionalNormalizedText(string path) {
        if (!File.Exists(path)) {
            return null;
        }

        var content = File.ReadAllText(path);
        var normalized = NormalizeWhitespace(content);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string NormalizeWhitespace(string value) {
        var sb = new StringBuilder(value.Length);
        var inWhitespace = false;

        foreach (var ch in value.Trim()) {
            if (char.IsWhiteSpace(ch)) {
                if (!inWhitespace) {
                    sb.Append(' ');
                    inWhitespace = true;
                }

                continue;
            }

            sb.Append(ch);
            inWhitespace = false;
        }

        return sb.ToString();
    }
}

public sealed record DatasetScanSummary(
    IReadOnlyList<DatasetImageRecord> Records,
    DatasetSummaryIndex SummaryIndex);

public sealed record DatasetImageRecord(
    string RelativeImagePath,
    int Width,
    int Height,
    DatasetImageHashes Hashes,
    DatasetCaptionSources CaptionSources,
    DatasetCaptionPresence CaptionPresence);

public sealed record DatasetImageHashes(string Md5, string Sha256);

public sealed record DatasetCaptionSources(
    string? BooruTags,
    string? ShortCaption,
    string? StyleTags);

public sealed record DatasetCaptionPresence(
    bool HasBooruTags,
    bool HasShortCaption,
    bool HasStyleTags);

public sealed record DatasetSummaryIndex(
    string DatasetDirectory,
    string JsonlPath,
    string SummaryPath,
    int TotalImages,
    int WithBooruTags,
    int WithShortCaption,
    int WithStyleTags,
    long TotalPixels,
    IReadOnlyDictionary<string, int> ByExtension);
