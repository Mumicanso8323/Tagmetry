using Tagmetry.Adapters.JoyTag;
using Tagmetry.Adapters.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

// exe の隣に log\ を作り、最初から必ず書けるようにする
var bootstrapLogDir = Path.Combine(baseDir, "log");
Directory.CreateDirectory(bootstrapLogDir);
var bootstrapLogPath = Path.Combine(bootstrapLogDir, "bootstrap.log");

void BootstrapLog(string message, Exception? ex = null)
{
    try
    {
        var line = $"{DateTimeOffset.Now:O} {message}";
        if (ex is not null) line += Environment.NewLine + ex;
        File.AppendAllText(bootstrapLogPath, line + Environment.NewLine);
    }
    catch
    {
        // 絶対にここで例外を投げない
    }
}

// できるだけ早い段階で「落ちた理由」を捕まえる
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    BootstrapLog($"[FATAL] UnhandledException (IsTerminating={e.IsTerminating})",
        e.ExceptionObject as Exception);
};

TaskScheduler.UnobservedTaskException += (_, e) =>
{
    BootstrapLog("[ERROR] UnobservedTaskException", e.Exception);
    e.SetObserved();
};

try
{
    BootstrapLog($"Starting Tagmetry.Web (BaseDir={baseDir})");

    // ContentRoot を exe のある場所に寄せる（publish / 単体exe実行でのパス事故を減らす）
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = baseDir
    });

    // -----------------------------
    // Configuration:
    // appsettings が無くても起動する（optional:true）
    // -----------------------------
    try
    {
        // baseDir を基準に appsettings を探す
        builder.Configuration.SetBasePath(baseDir);

        var envName = builder.Environment.EnvironmentName;

        // ★重要：optional:true（無くても落ちない）
        builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
        builder.Configuration.AddJsonFile($"appsettings.{envName}.json", optional: true, reloadOnChange: false);

        BootstrapLog($"Config loaded (optional). EnvironmentName={envName}");
    }
    catch (Exception ex)
    {
        // config 周りで落ちても bootstrap に残す
        BootstrapLog("[WARN] Configuration load failed (continuing with defaults).", ex);
    }

    // -----------------------------
    // Services / Options
    // -----------------------------
    builder.Services.Configure<JoyTagOptions>(builder.Configuration.GetSection("JoyTag"));
    builder.Services.AddSingleton<JoyTagProcessController>();
    builder.Services.AddSingleton<JobStore>();

    // -----------------------------
    // Logging
    // -----------------------------
    // repo root（開発時は sln を辿る / publish は baseDir に落ちる）
    var repoRoot = FindRepoRoot(baseDir) ?? baseDir;

    var logsDirName = builder.Configuration["Paths:LogsDir"];
    if (string.IsNullOrWhiteSpace(logsDirName)) logsDirName = "log";

    var logDir = Path.GetFullPath(Path.Combine(repoRoot, logsDirName));
    Directory.CreateDirectory(logDir);

    // bootstrap も最終 logDir に寄せる（分散しないように）
    try
    {
        if (!Path.GetFullPath(bootstrapLogDir).Equals(logDir, StringComparison.OrdinalIgnoreCase))
        {
            bootstrapLogDir = logDir;
            bootstrapLogPath = Path.Combine(bootstrapLogDir, "bootstrap.log");
            Directory.CreateDirectory(bootstrapLogDir);
            BootstrapLog($"Bootstrap log relocated to: {bootstrapLogPath}");
        }
    }
    catch { /* ignore */ }

    builder.Logging.ClearProviders();
    builder.Logging.AddSimpleConsole(o => { o.TimestampFormat = "HH:mm:ss "; });
    builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(logDir, "web.log")));

    var app = builder.Build();

    // DI 後ログ
    var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    startupLogger.LogInformation("Application starting. BaseDir={BaseDir} RepoRoot={RepoRoot} LogDir={LogDir}",
        baseDir, repoRoot, logDir);

    // listen url
    var listenUrl = builder.Configuration["Web:ListenUrl"];
    if (string.IsNullOrWhiteSpace(listenUrl)) listenUrl = "http://127.0.0.1:5099";
    app.Urls.Clear();
    app.Urls.Add(listenUrl);

    // static files
    app.UseDefaultFiles();
    app.UseStaticFiles();

    // endpoints
    app.MapPost("/api/run", (RunRequest req, JobStore store, JoyTagProcessController joytag,
        IOptions<JoyTagOptions> joyOpt, ILoggerFactory lf, CancellationToken ct) =>
    {
        var jobId = store.Start(req);

        _ = Task.Run(async () =>
        {
            var logger = lf.CreateLogger("JobRunner");
            try
            {
                var thirdPartyDirName = builder.Configuration["Paths:ThirdPartyDir"];
                if (string.IsNullOrWhiteSpace(thirdPartyDirName)) thirdPartyDirName = "third_party";

                var thirdPartyDir = Path.GetFullPath(Path.Combine(repoRoot, thirdPartyDirName));

                if (!Directory.Exists(thirdPartyDir))
                {
                    // ここは「落とさず」ログに出して job を fail にする
                    var msg = $"third_party not found: {thirdPartyDir}";
                    store.Fail(jobId, msg);
                    logger.LogError(msg);
                    BootstrapLog("[ERROR] " + msg);
                    return;
                }

                await joytag.StartAsync(thirdPartyDir, joyOpt.Value, CancellationToken.None);

                for (int p = 0; p <= 100; p += 10)
                {
                    store.Update(jobId, p, $"Processing... {p}%");
                    await Task.Delay(300, ct);
                }

                store.Complete(jobId, new { ok = true, input = req.InputDir });
                logger.LogInformation("Job completed: {JobId}", jobId);
            }
            catch (OperationCanceledException oce)
            {
                store.Fail(jobId, "Canceled");
                logger.LogWarning(oce, "Job canceled: {JobId}", jobId);
            }
            catch (Exception ex)
            {
                store.Fail(jobId, ex.Message);
                logger.LogError(ex, "Job failed: {JobId}", jobId);
                BootstrapLog($"[ERROR] Background job failed: {jobId}", ex);
            }
            finally
            {
                try { await joytag.StopAsync(); }
                catch (Exception ex) { BootstrapLog("[WARN] joytag.StopAsync failed", ex); }
            }
        }, ct);

        return Results.Ok(new { jobId });
    });

    app.MapGet("/api/status", (Guid jobId, JobStore store) =>
    {
        var s = store.Get(jobId);
        return s is null ? Results.NotFound() : Results.Ok(s);
    });

    app.MapGet("/api/log", (int lines) =>
    {
        lines = Math.Clamp(lines, 50, 2000);
        var path = Path.Combine(logDir, "web.log");
        if (!File.Exists(path)) return Results.Ok(new { lines = Array.Empty<string>() });

        var tail = TailLines(path, lines);
        return Results.Ok(new { lines = tail });
    });

    app.MapGet("/api/report", (Guid jobId, JobStore store) =>
    {
        var r = store.GetReport(jobId);
        return r is null ? Results.NotFound() : Results.Ok(r);
    });

    app.MapGet("/api/ping", () => Results.Ok(new { ok = true, time = DateTimeOffset.Now }));

    app.Lifetime.ApplicationStopping.Register(() => startupLogger.LogInformation("Application stopping..."));
    app.Lifetime.ApplicationStopped.Register(() => startupLogger.LogInformation("Application stopped."));

    app.Run();
}
catch (Exception ex)
{
    // 絶対に痕跡を残す
    BootstrapLog("[FATAL] Process crashed.", ex);
    try
    {
        var crashPath = Path.Combine(bootstrapLogDir, $"crash_{DateTimeOffset.Now:yyyyMMdd_HHmmss}.log");
        File.WriteAllText(crashPath, ex.ToString());
    }
    catch { }
    throw;
}

static string? FindRepoRoot(string start)
{
    var dir = new DirectoryInfo(start);
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Tagmetry.sln"))) return dir.FullName;
        dir = dir.Parent;
    }
    return null;
}

static string[] TailLines(string filePath, int lines)
{
    var all = File.ReadAllLines(filePath);
    return all.Skip(Math.Max(0, all.Length - lines)).ToArray();
}

record RunRequest(string InputDir);

sealed class JobStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, object> _status = new();
    private readonly Dictionary<Guid, object> _report = new();

    public Guid Start(RunRequest req)
    {
        var id = Guid.NewGuid();
        lock (_gate) _status[id] = new { jobId = id, state = "running", percent = 0, message = "started", input = req.InputDir };
        return id;
    }

    public void Update(Guid id, int percent, string message)
    {
        lock (_gate)
        {
            if (_status.ContainsKey(id))
                _status[id] = new { jobId = id, state = "running", percent, message };
        }
    }

    public void Complete(Guid id, object report)
    {
        lock (_gate)
        {
            _status[id] = new { jobId = id, state = "completed", percent = 100, message = "done" };
            _report[id] = report;
        }
    }

    public void Fail(Guid id, string error)
    {
        lock (_gate) _status[id] = new { jobId = id, state = "failed", percent = 0, message = error };
    }

    public object? Get(Guid id) { lock (_gate) return _status.TryGetValue(id, out var s) ? s : null; }
    public object? GetReport(Guid id) { lock (_gate) return _report.TryGetValue(id, out var r) ? r : null; }
}
