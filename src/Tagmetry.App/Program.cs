using Tagmetry.Adapters.JoyTag;
using Tagmetry.Adapters.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

// まず「絶対に残る」ログ
var bootstrapDir = Path.Combine(baseDir, "log");
Directory.CreateDirectory(bootstrapDir);
var bootstrapPath = Path.Combine(bootstrapDir, "bootstrap_app.log");

void BootstrapLog(string message, Exception? ex = null)
{
    try
    {
        var line = $"{DateTimeOffset.Now:O} {message}";
        if (ex is not null) line += Environment.NewLine + $"ExceptionType={ex.GetType().Name}";
        File.AppendAllText(bootstrapPath, line + Environment.NewLine);
    }
    catch { }
}

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
    BootstrapLog($"Starting Tagmetry.App (BaseDir={baseDir})");

    var repoRoot = FindRepoRoot(baseDir) ?? baseDir;

    // appsettings が無くても落ちない
    IConfiguration config;
    try
    {
        config = new ConfigurationBuilder()
            .SetBasePath(baseDir)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .Build();

        BootstrapLog("Config loaded (optional).");
    }
    catch (Exception ex)
    {
        BootstrapLog("[WARN] Configuration load failed (continuing with defaults).", ex);
        config = new ConfigurationBuilder().Build();
    }

    var logsDirName = config["Paths:LogsDir"];
    if (string.IsNullOrWhiteSpace(logsDirName)) logsDirName = "log";

    var logDir = Path.GetFullPath(Path.Combine(repoRoot, logsDirName));
    Directory.CreateDirectory(logDir);

    // bootstrap も最終ログディレクトリへ寄せる
    try
    {
        var relocated = Path.Combine(logDir, "bootstrap_app.log");
        if (!Path.GetFullPath(bootstrapPath).Equals(Path.GetFullPath(relocated), StringComparison.OrdinalIgnoreCase))
        {
            bootstrapPath = relocated;
            BootstrapLog($"Bootstrap log relocated to: {bootstrapPath}");
        }
    }
    catch { }

    var services = new ServiceCollection();
    services.AddSingleton(config);
    services.Configure<JoyTagOptions>(config.GetSection("JoyTag"));
    services.AddSingleton<JoyTagProcessController>();

    services.AddLogging(b =>
    {
        b.ClearProviders();
        b.AddConfiguration(config.GetSection("Logging")); // appsettings の Logging を効かせる
        b.AddSimpleConsole(o => o.TimestampFormat = "HH:mm:ss ");
        b.AddProvider(new FileLoggerProvider(Path.Combine(logDir, "app.log")));
    });

    var sp = services.BuildServiceProvider();
    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("App");

    // args
    var inputDir = args.Length >= 2 && args[0].Equals("run", StringComparison.OrdinalIgnoreCase) ? args[1] : null;
    if (string.IsNullOrWhiteSpace(inputDir))
    {
        Console.WriteLine("Usage: Tagmetry.App run <inputDir>");
        BootstrapLog("[INFO] Usage shown (missing args).");
        return 2;
    }

    var thirdPartyDirName = config["Paths:ThirdPartyDir"];
    if (string.IsNullOrWhiteSpace(thirdPartyDirName)) thirdPartyDirName = "third_party";
    var thirdPartyDir = Path.GetFullPath(Path.Combine(repoRoot, thirdPartyDirName));

    var joy = sp.GetRequiredService<JoyTagProcessController>();
    var opt = sp.GetRequiredService<IOptions<JoyTagOptions>>().Value;

    try
    {
        await joy.StartAsync(thirdPartyDir, opt, CancellationToken.None);

        logger.LogInformation("Stub run started.");
        Console.WriteLine("OK (stub).");
        return 0;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed");
        BootstrapLog("[ERROR] App run failed.", ex);
        Console.Error.WriteLine($"Error: {ex.GetType().Name}");
        return 1;
    }
    finally
    {
        try { await joy.StopAsync(); }
        catch (Exception ex) { BootstrapLog("[WARN] joy.StopAsync failed", ex); }
    }
}
catch (Exception ex)
{
    BootstrapLog("[FATAL] Process crashed.", ex);
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
