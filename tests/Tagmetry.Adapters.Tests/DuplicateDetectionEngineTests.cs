using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Tagmetry.Adapters.IO;

namespace Tagmetry.Adapters.Tests;

public class DuplicateDetectionEngineTests {
    [Fact]
    public async Task AnalyzeAsync_FindsExactAndNearDuplicateGroupsWithScores() {
        using var fixture = new ImageFixture();

        fixture.CreateSolid("a.png", new Rgba32(255, 0, 0), addCornerMarker: false);
        fixture.Copy("a.png", "a_copy.png"); // exact duplicate
        fixture.CreateSolid("b.png", new Rgba32(250, 5, 5), addCornerMarker: false); // likely near
        fixture.CreateSolid("c.png", new Rgba32(120, 120, 120), addCornerMarker: true); // different

        var engine = new DuplicateDetectionEngine();
        var report = await engine.AnalyzeAsync([
            fixture.PathOf("a.png"),
            fixture.PathOf("a_copy.png"),
            fixture.PathOf("b.png"),
            fixture.PathOf("c.png")
        ], new DuplicateDetectionOptions(LikelyHammingThreshold: 8, MaybeHammingThreshold: 16));

        Assert.Equal(4, report.TotalFiles);

        var exact = Assert.Single(report.ExactDuplicateGroups);
        Assert.Equal(2, exact.Paths.Count);
        Assert.Contains(fixture.PathOf("a.png"), exact.Paths);
        Assert.Contains(fixture.PathOf("a_copy.png"), exact.Paths);

        Assert.NotEmpty(report.NearDuplicateFindings);
        Assert.All(report.NearDuplicateFindings, finding => {
            Assert.True(finding.Score >= 0 && finding.Score <= 1);
            Assert.True(finding.Band is NearDuplicateBand.Likely or NearDuplicateBand.Maybe);
        });

        Assert.NotEmpty(report.NearDuplicateGroups);
        Assert.All(report.NearDuplicateGroups, group => {
            Assert.True(group.Paths.Count >= 2);
            Assert.True(group.Score >= 0 && group.Score <= 1);
        });
    }

    [Fact]
    public async Task AnalyzeAsync_ThrowsForMissingFile() {
        var engine = new DuplicateDetectionEngine();
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");

        await Assert.ThrowsAsync<FileNotFoundException>(() => engine.AnalyzeAsync([missing]));
    }

    private sealed class ImageFixture : IDisposable {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tagmetry-dup-" + Guid.NewGuid().ToString("N"));

        public ImageFixture() {
            Directory.CreateDirectory(Root);
        }

        public string PathOf(string name) => Path.Combine(Root, name);

        public void CreateSolid(string name, Rgba32 color, bool addCornerMarker) {
            using var img = new Image<Rgba32>(64, 64, color);
            if (addCornerMarker) {
                img[0, 0] = new Rgba32(0, 255, 0);
                img[1, 0] = new Rgba32(0, 255, 0);
                img[0, 1] = new Rgba32(0, 255, 0);
            }

            img.SaveAsPng(PathOf(name));
        }

        public void Copy(string source, string destination) {
            File.Copy(PathOf(source), PathOf(destination));
        }

        public void Dispose() {
            try {
                if (Directory.Exists(Root)) {
                    Directory.Delete(Root, true);
                }
            }
            catch {
                // ignore cleanup failures in temp
            }
        }
    }
}
