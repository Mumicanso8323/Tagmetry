using Microsoft.Extensions.Logging;

namespace Tagmetry.Core.Jobs;

public sealed class JobRunnerStub {
    private static readonly (int Percent, string Stage, string Message)[] PipelineStages = [
        (5, "validate", "Validating request and input paths."),
        (20, "scan", "Scanning dataset and sidecars."),
        (45, "normalize", "Normalizing tags and building audit trail."),
        (65, "metrics", "Computing health metrics M1-M11."),
        (80, "recommend", "Running rule-based recommendations."),
        (95, "dedupe", "Computing exact and near-duplicate groups."),
        (100, "finalize", "Preparing result artifacts.")
    ];

    public async Task<AnalysisJobResult> RunAsync(
        Guid jobId,
        AnalysisJobRequest request,
        IJobProgressSink progress,
        ILogger logger,
        CancellationToken cancellationToken) {

        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(logger);

        logger.LogInformation("Job pipeline started. JobId={JobId}", jobId);

        foreach (var stage in PipelineStages) {
            cancellationToken.ThrowIfCancellationRequested();

            var update = new JobProgressUpdate(stage.Percent, stage.Stage, stage.Message, DateTimeOffset.UtcNow);
            progress.Report(update);
            logger.LogInformation("Job progress update. JobId={JobId} Stage={Stage} Percent={Percent} Message={Message}",
                jobId, stage.Stage, stage.Percent, stage.Message);

            await Task.Delay(150, cancellationToken);
        }

        var now = DateTimeOffset.UtcNow;
        var outputs = new Dictionary<string, object?> {
            ["duplicateDetectionEnabled"] = request.EnableDuplicateDetection,
            ["tagMetricsEnabled"] = request.EnableTagMetrics,
            ["recommendationsEnabled"] = request.EnableRecommendations,
            ["artifacts"] = new Dictionary<string, object?> {
                ["datasetJsonl"] = "dataset.jsonl",
                ["summaryIndex"] = "summary.json",
                ["metricsJson"] = "metrics.json",
                ["metricsMarkdown"] = "metrics.md",
                ["recommendationsJson"] = "recommendations.json",
                ["duplicatesJson"] = "duplicates.json"
            }
        };

        logger.LogInformation("Job pipeline finished successfully. JobId={JobId}", jobId);
        return new AnalysisJobResult(jobId, JobState.Completed, outputs, null, now);
    }
}

public interface IJobProgressSink {
    void Report(JobProgressUpdate update);
}
