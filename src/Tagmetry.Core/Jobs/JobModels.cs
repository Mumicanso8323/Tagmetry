namespace Tagmetry.Core.Jobs;

public enum JobState {
    Queued,
    Running,
    Completed,
    Failed,
    Canceled
}

public sealed record AnalysisJobRequest(
    string InputDir,
    string? OutputDir,
    string? RulesPath,
    bool EnableDuplicateDetection = true,
    bool EnableTagMetrics = true,
    bool EnableRecommendations = true);

public sealed record JobProgressUpdate(
    int Percent,
    string Stage,
    string Message,
    DateTimeOffset AtUtc);

public sealed record JobLogEntry(
    DateTimeOffset AtUtc,
    string Level,
    string Message,
    IReadOnlyDictionary<string, object?> Data);

public sealed record AnalysisJobStatus(
    Guid JobId,
    JobState State,
    int Percent,
    string Stage,
    string Message,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    bool IsCancelable);

public sealed record AnalysisJobResult(
    Guid JobId,
    JobState State,
    IReadOnlyDictionary<string, object?> Outputs,
    string? Error,
    DateTimeOffset FinishedAtUtc);
