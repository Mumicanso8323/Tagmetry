using Tagmetry.Core.Tags;

namespace Tagmetry.Core.Tests;

public class TagNormalizerTests {
    [Fact]
    public void Normalize_AppliesPipelineAndEmitsAuditTrailInDeterministicOrder() {
        var rules = TagNormalizationRules.FromJson(
            """
            {
              "canonicalDelimiter": " ",
              "delimiters": ["_", "-", "/"],
              "aliases": {
                "sci fi": "science fiction",
                "bw": "black and white"
              },
              "stopTags": ["meta", "discard me"]
            }
            """);

        var sut = new TagNormalizer(rules);

        var result = sut.Normalize(["SCI_FI", "bW", "meta", "safe-tag"]);

        Assert.Equal(["science fiction", "black and white", "safe tag"], result.NormalizedTokens);

        Assert.Collection(result.Tokens,
            token => {
                Assert.Equal("SCI_FI", token.OriginalToken);
                Assert.Equal("science fiction", token.NormalizedToken);
                Assert.False(token.IsFiltered);
                Assert.Equal([
                    TagAuditAction.CaseFold,
                    TagAuditAction.DelimiterNormalization,
                    TagAuditAction.AliasMapping,
                    TagAuditAction.StopTagFiltering
                ], token.AuditTrail.Select(a => a.Action));
            },
            token => {
                Assert.Equal("bW", token.OriginalToken);
                Assert.Equal("black and white", token.NormalizedToken);
            },
            token => {
                Assert.Equal("meta", token.OriginalToken);
                Assert.Null(token.NormalizedToken);
                Assert.True(token.IsFiltered);
                Assert.Equal(TagAuditAction.StopTagFiltering, token.AuditTrail.Last().Action);
                Assert.Contains("Filtered by stop-tag rule.", token.AuditTrail.Last().Message);
            },
            token => {
                Assert.Equal("safe-tag", token.OriginalToken);
                Assert.Equal("safe tag", token.NormalizedToken);
            });
    }

    [Fact]
    public void Normalize_HandlesOverlappingDelimitersWithoutNondeterminism() {
        var rules = TagNormalizationRules.FromJson(
            """
            {
              "canonicalDelimiter": "-",
              "delimiters": ["--", "_"],
              "aliases": {},
              "stopTags": []
            }
            """);

        var sut = new TagNormalizer(rules);

        var result = sut.Normalize(["A----B", "A__B"]);

        Assert.Equal(["a-b", "a-b"], result.NormalizedTokens);
        Assert.All(result.Tokens, token => Assert.Equal(4, token.AuditTrail.Count));
    }

    [Fact]
    public void Normalize_ThrowsForNullInputSequence() {
        var rules = TagNormalizationRules.FromJson(
            """
            {
              "canonicalDelimiter": " ",
              "delimiters": [],
              "aliases": {},
              "stopTags": []
            }
            """);

        var sut = new TagNormalizer(rules);

        Assert.Throws<ArgumentNullException>(() => sut.Normalize(null!));
    }

    [Fact]
    public void RulesFromJson_UsesDefaultsForMissingOptionalFields() {
        var rules = TagNormalizationRules.FromJson("{}");
        var sut = new TagNormalizer(rules);

        var result = sut.Normalize([" MixedCase "]);

        Assert.Equal(["mixedcase"], result.NormalizedTokens);
        Assert.Equal("No change.", result.Tokens[0].AuditTrail[1].Message);
    }
}
