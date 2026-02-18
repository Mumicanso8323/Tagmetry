using System.Text.Json;
using Tagmetry.Adapters.IO;

namespace Tagmetry.Adapters.Tests;

public class DatasetScannerTests {
    [Fact]
    public async Task ScanAsync_ProducesJsonlRecordsAndSummaryIndex() {
        using var fixture = new DatasetFixture();
        fixture.CreatePng("b.png", 2, 3);
        fixture.CreatePng("a.png", 1, 1);

        fixture.WriteSidecar("a.booru.txt", "tag_one, tag two");
        fixture.WriteSidecar("a.caption.txt", "  short\ncaption ");
        fixture.WriteSidecar("a.style.txt", " painterly ");
        fixture.WriteSidecar("b.tags.txt", "legacy_tag_source");
        fixture.WriteSidecar("b.txt", "fallback caption");

        var jsonl = Path.Combine(fixture.Root, "out", "dataset.jsonl");
        var summary = Path.Combine(fixture.Root, "out", "index.json");

        var sut = new DatasetScanner();
        var result = await sut.ScanAsync(fixture.Root, jsonl, summary);

        Assert.Equal(2, result.Records.Count);
        Assert.Equal(["a.png", "b.png"], result.Records.Select(x => x.RelativeImagePath));

        var a = result.Records[0];
        Assert.Equal(1, a.Width);
        Assert.Equal(1, a.Height);
        Assert.Equal("tag_one, tag two", a.CaptionSources.BooruTags);
        Assert.Equal("short caption", a.CaptionSources.ShortCaption);
        Assert.Equal("painterly", a.CaptionSources.StyleTags);
        Assert.True(a.CaptionPresence.HasBooruTags);
        Assert.True(a.CaptionPresence.HasShortCaption);
        Assert.True(a.CaptionPresence.HasStyleTags);
        Assert.Equal(32, a.Hashes.Md5.Length);
        Assert.Equal(64, a.Hashes.Sha256.Length);

        var b = result.Records[1];
        Assert.Equal("legacy_tag_source", b.CaptionSources.BooruTags);
        Assert.Equal("fallback caption", b.CaptionSources.ShortCaption);
        Assert.Null(b.CaptionSources.StyleTags);

        Assert.True(File.Exists(jsonl));
        Assert.True(File.Exists(summary));

        var lines = await File.ReadAllLinesAsync(jsonl);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"relativeImagePath\":\"a.png\"", lines[0]);
        Assert.Contains("\"relativeImagePath\":\"b.png\"", lines[1]);

        using var summaryDoc = JsonDocument.Parse(await File.ReadAllTextAsync(summary));
        var root = summaryDoc.RootElement;
        Assert.Equal(2, root.GetProperty("totalImages").GetInt32());
        Assert.Equal(2, root.GetProperty("withBooruTags").GetInt32());
        Assert.Equal(2, root.GetProperty("withShortCaption").GetInt32());
        Assert.Equal(1, root.GetProperty("withStyleTags").GetInt32());
    }

    [Fact]
    public async Task ScanAsync_ThrowsWhenDatasetFolderMissing() {
        var sut = new DatasetScanner();
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            sut.ScanAsync(missing, Path.Combine(missing, "out.jsonl"), Path.Combine(missing, "index.json")));
    }

    private sealed class DatasetFixture : IDisposable {
        public DatasetFixture() {
            Root = Path.Combine(Path.GetTempPath(), "tagmetry-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void CreatePng(string relativePath, int width, int height) {
            var fullPath = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            var pngBytes = CreateSolidPng(width, height);
            File.WriteAllBytes(fullPath, pngBytes);
        }

        public void WriteSidecar(string relativePath, string content) {
            var fullPath = Path.Combine(Root, relativePath);
            File.WriteAllText(fullPath, content);
        }

        public void Dispose() {
            try {
                if (Directory.Exists(Root)) {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch {
                // ignore cleanup races in temp directories
            }
        }

        private static byte[] CreateSolidPng(int width, int height) {
            using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(width, height);
            using var ms = new MemoryStream();
            image.SaveAsPng(ms);
            return ms.ToArray();
        }
    }
}
