using Microsoft.Extensions.Logging.Abstractions;
using Tagmetry.Core.Jobs;

namespace Tagmetry.Core.Tests;

public class JobRunnerStubTests {
    [Fact]
    public async Task RunAsync_ReportsProgressAndReturnsCompletedResult() {
        var runner = new JobRunnerStub();
        var sink = new CollectingSink();
        var req = new AnalysisJobRequest("/data/in", "/data/out", "rules.json");

        var result = await runner.RunAsync(Guid.NewGuid(), req, sink, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(JobState.Completed, result.State);
        Assert.True(sink.Updates.Count >= 2);
        Assert.Equal(100, sink.Updates.Last().Percent);
        Assert.Contains("artifacts", result.Outputs.Keys);
    }

    [Fact]
    public async Task RunAsync_HonorsCancellation() {
        var runner = new JobRunnerStub();
        var sink = new CollectingSink();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(Guid.NewGuid(), new AnalysisJobRequest("input", null, null), sink, NullLogger.Instance, cts.Token));
    }

    private sealed class CollectingSink : IJobProgressSink {
        public List<JobProgressUpdate> Updates { get; } = [];
        public void Report(JobProgressUpdate update) => Updates.Add(update);
    }
}
