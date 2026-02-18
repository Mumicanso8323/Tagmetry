using Microsoft.Extensions.Options;
using Tagmetry.Adapters.JoyTag;
using Tagmetry.Adapters.Logging;
using Tagmetry.Core.Jobs;

var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
    Args = args,
    ContentRootPath = baseDir
});

builder.Configuration.SetBasePath(baseDir);
builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
builder.Configuration.AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: false);

builder.Services.Configure<JoyTagOptions>(builder.Configuration.GetSection("JoyTag"));
builder.Services.AddSingleton<JoyTagProcessController>();
builder.Services.AddSingleton<JobRunnerStub>();
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton(new TelemetrySettingsStore(builder.Configuration.GetValue<bool?>("Telemetry:Enabled") ?? false));

var repoRoot = FindRepoRoot(baseDir) ?? baseDir;
var logsDirName = builder.Configuration["Paths:LogsDir"];
if (string.IsNullOrWhiteSpace(logsDirName)) logsDirName = "log";
var logDir = Path.GetFullPath(Path.Combine(repoRoot, logsDirName));
Directory.CreateDirectory(logDir);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => o.TimestampFormat = "HH:mm:ss ");
builder.Logging.AddJsonConsole();
builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(logDir, "web.log")));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", (ILoggerFactory lf, TelemetrySettingsStore telemetry) => {
    var logger = lf.CreateLogger("Health");
    logger.LogInformation("Health probe OK.");
    return Results.Ok(new {
        ok = true,
        service = "Tagmetry.Web",
        telemetryEnabled = telemetry.IsEnabled,
        time = DateTimeOffset.UtcNow
    });
});

app.MapGet("/settings", (TelemetrySettingsStore telemetry) => Results.Ok(new {
    telemetryEnabled = telemetry.IsEnabled,
    telemetryMode = "opt-in"
}));

app.MapPost("/settings/telemetry", (TelemetryUpdateRequest request, TelemetrySettingsStore telemetry, ILoggerFactory lf) => {
    telemetry.Set(request.Enabled);
    lf.CreateLogger("Settings").LogInformation("Telemetry preference updated. Enabled={Enabled}", request.Enabled);
    return Results.Ok(new { telemetryEnabled = telemetry.IsEnabled });
});

app.MapPost("/jobs", async (AnalysisJobRequest request, JobStore store, JobRunnerStub runner, ILoggerFactory lf, CancellationToken ct) => {
    var logger = lf.CreateLogger("Jobs");
    var jobId = store.Create(request);

    logger.LogInformation("Job created. JobId={JobId}", jobId);

    _ = Task.Run(async () => {
        using var scope = logger.BeginScope(new Dictionary<string, object?> { ["jobId"] = jobId });

        try {
            store.MarkRunning(jobId, "queued", "Job accepted and queued.");
            store.AddLog(jobId, "Information", "Job queued.", new Dictionary<string, object?> {
                ["jobId"] = jobId
            });

            var result = await runner.RunAsync(
                jobId,
                request,
                store.CreateProgressSink(jobId),
                logger,
                store.GetToken(jobId));

            store.Complete(jobId, result);
            store.AddLog(jobId, "Information", "Job completed.", new Dictionary<string, object?> {
                ["state"] = result.State.ToString()
            });
        }
        catch (OperationCanceledException) {
            store.Cancel(jobId, "Cancellation requested.");
            store.AddLog(jobId, "Warning", "Job canceled.", new Dictionary<string, object?>());
            logger.LogWarning("Job canceled.");
        }
        catch (Exception ex) {
            store.Fail(jobId, ex.GetType().Name);
            store.AddLog(jobId, "Error", "Job failed.", new Dictionary<string, object?> {
                ["errorType"] = ex.GetType().Name
            });
            logger.LogError(ex, "Job failed.");
        }
    }, ct);

    return Results.Accepted($"/jobs/{jobId}", new { jobId });
});

app.MapGet("/jobs/{id:guid}", (Guid id, JobStore store) => {
    var status = store.GetStatus(id);
    return status is null ? Results.NotFound() : Results.Ok(status);
});

app.MapPost("/jobs/{id:guid}/cancel", (Guid id, JobStore store, ILoggerFactory lf) => {
    var logger = lf.CreateLogger("Jobs");
    var canceled = store.RequestCancel(id);
    if (!canceled) {
        return Results.NotFound(new { message = "Job not found or cannot be canceled." });
    }

    logger.LogInformation("Cancel requested. JobId={JobId}", id);
    return Results.Ok(new { jobId = id, canceled = true });
});

app.MapGet("/jobs/{id:guid}/logs", (Guid id, JobStore store) => {
    var logs = store.GetLogs(id);
    return logs is null ? Results.NotFound() : Results.Ok(new { jobId = id, logs });
});

app.MapGet("/jobs/{id:guid}/result", (Guid id, JobStore store) => {
    var result = store.GetResult(id);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.Run();

static string? FindRepoRoot(string start) {
    var dir = new DirectoryInfo(start);
    while (dir != null) {
        if (File.Exists(Path.Combine(dir.FullName, "Tagmetry.sln"))) return dir.FullName;
        dir = dir.Parent;
    }

    return null;
}

internal sealed record TelemetryUpdateRequest(bool Enabled);

internal sealed class TelemetrySettingsStore(bool enabled) {
    private volatile bool _enabled = enabled;
    public bool IsEnabled => _enabled;
    public void Set(bool enabled) => _enabled = enabled;
}

internal sealed class JobStore {
    private readonly object _gate = new();
    private readonly Dictionary<Guid, JobEntry> _jobs = new();

    public Guid Create(AnalysisJobRequest request) {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        lock (_gate) {
            _jobs[id] = new JobEntry(
                request,
                new CancellationTokenSource(),
                new AnalysisJobStatus(id, JobState.Queued, 0, "queued", "Created", now, now, null, true));
        }

        return id;
    }

    public void MarkRunning(Guid id, string stage, string message) {
        lock (_gate) {
            if (!_jobs.TryGetValue(id, out var entry)) return;
            entry.Status = entry.Status with {
                State = JobState.Running,
                Stage = stage,
                Message = message,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                IsCancelable = true
            };
        }
    }

    public IJobProgressSink CreateProgressSink(Guid id) => new ProgressSink(update => {
        lock (_gate) {
            if (!_jobs.TryGetValue(id, out var entry)) return;

            entry.Status = entry.Status with {
                State = JobState.Running,
                Percent = Math.Clamp(update.Percent, 0, 100),
                Stage = update.Stage,
                Message = update.Message,
                UpdatedAtUtc = update.AtUtc,
                IsCancelable = true
            };

            entry.Logs.Add(new JobLogEntry(update.AtUtc, "Information", update.Message, new Dictionary<string, object?> {
                ["stage"] = update.Stage,
                ["percent"] = update.Percent
            }));
        }
    });

    public CancellationToken GetToken(Guid id) {
        lock (_gate) {
            if (!_jobs.TryGetValue(id, out var entry)) throw new KeyNotFoundException("Job not found.");
            return entry.Cancellation.Token;
        }
    }

    public bool RequestCancel(Guid id) {
        lock (_gate) {
            if (!_jobs.TryGetValue(id, out var entry)) return false;
            if (entry.Status.State is JobState.Completed or JobState.Failed or JobState.Canceled) return false;
            entry.Cancellation.Cancel();
            return true;
        }
    }

    public void Cancel(Guid id, string message) {
        lock (_gate) {
            if (!_jobs.TryGetValue(id, out var entry)) return;
            var now = DateTimeOffset.UtcNow;
            entry.Status = entry.Status with {
                State = JobState.Canceled,
                Message = message,
                UpdatedAtUtc = now,
                FinishedAtUtc = now,
                IsCancelable = false
            };
            entry.Result = new AnalysisJobResult(id, JobState.Canceled, new Dictionary<string, object?>(), message, now);
        }
    }

    public void Complete(Guid id, AnalysisJobResult result) {
        lock (_gate) {
            if (!_jobs.TryGetValue(id, out var entry)) return;
            var now = DateTimeOffset.UtcNow;
            entry.Status = entry.Status with {
                State = JobState.Completed,
                Percent = 100,
                Stage = "completed",
                Message = "Completed",
                UpdatedAtUtc = now,
                FinishedAtUtc = now,
                IsCancelable = false
            };
            entry.Result = result with { FinishedAtUtc = now };
        }
    }

    public void Fail(Guid id, string error) {
        lock (_gate) {
            if (!_jobs.TryGetValue(id, out var entry)) return;
            var now = DateTimeOffset.UtcNow;
            entry.Status = entry.Status with {
                State = JobState.Failed,
                Stage = "failed",
                Message = "Job failed. See logs for details.",
                UpdatedAtUtc = now,
                FinishedAtUtc = now,
                IsCancelable = false
            };
            entry.Result = new AnalysisJobResult(id, JobState.Failed, new Dictionary<string, object?>(), $"FailureType={error}", now);
        }
    }

    public void AddLog(Guid id, string level, string message, IReadOnlyDictionary<string, object?> data) {
        lock (_gate) {
            if (!_jobs.TryGetValue(id, out var entry)) return;
            entry.Logs.Add(new JobLogEntry(DateTimeOffset.UtcNow, level, message, data));
        }
    }

    public AnalysisJobStatus? GetStatus(Guid id) {
        lock (_gate) {
            return _jobs.TryGetValue(id, out var entry) ? entry.Status : null;
        }
    }

    public IReadOnlyList<JobLogEntry>? GetLogs(Guid id) {
        lock (_gate) {
            return _jobs.TryGetValue(id, out var entry) ? entry.Logs.ToArray() : null;
        }
    }

    public AnalysisJobResult? GetResult(Guid id) {
        lock (_gate) {
            return _jobs.TryGetValue(id, out var entry) ? entry.Result : null;
        }
    }

    private sealed class ProgressSink(Action<JobProgressUpdate> report) : IJobProgressSink {
        public void Report(JobProgressUpdate update) => report(update);
    }

    private sealed class JobEntry(
        AnalysisJobRequest request,
        CancellationTokenSource cancellation,
        AnalysisJobStatus status) {

        public AnalysisJobRequest Request { get; } = request;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public AnalysisJobStatus Status { get; set; } = status;
        public AnalysisJobResult? Result { get; set; }
        public List<JobLogEntry> Logs { get; } = [];
    }
}
