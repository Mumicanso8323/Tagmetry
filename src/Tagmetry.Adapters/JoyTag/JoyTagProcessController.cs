using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net.Http;

namespace Tagmetry.Adapters.JoyTag;

public sealed class JoyTagProcessController : IAsyncDisposable {
    private readonly ILogger _logger;
    private readonly HttpClient _http = new();
    private Process? _proc;

    public JoyTagProcessController(ILogger<JoyTagProcessController> logger) {
        _logger = logger;
    }

    public async Task StartAsync(string thirdPartyDir, JoyTagOptions opt, CancellationToken ct) {
        if (!opt.Enabled) {
            _logger.LogInformation("JoyTag is disabled by config.");
            return;
        }
        if (_proc != null && !_proc.HasExited) {
            _logger.LogInformation("JoyTag already running (pid={Pid}).", _proc.Id);
            return;
        }

        var pythonExe = Path.GetFullPath(Path.Combine(thirdPartyDir, opt.PythonExeRelativePath));
        var script = Path.GetFullPath(Path.Combine(thirdPartyDir, opt.ServerScriptRelativePath));
        var workDir = Path.GetDirectoryName(script)!;

        if (!File.Exists(pythonExe)) throw new FileNotFoundException("python.exe not found", pythonExe);
        if (!File.Exists(script)) throw new FileNotFoundException("server.py not found", script);

        // ここは JoyTag 実装に合わせて引数を調整（まずは形だけ）
        var args = $"\"{script}\" --host {opt.Host} --port {opt.Port} " + (opt.UseGpu ? "--use-gpu" : "--cpu");

        _logger.LogInformation("Starting JoyTag: {Python} {Args}", pythonExe, args);

        var psi = new ProcessStartInfo {
            FileName = pythonExe,
            Arguments = args,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _proc.OutputDataReceived += (_, e) => { if (e.Data != null) _logger.LogInformation("[joytag] {Line}", e.Data); };
        _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) _logger.LogWarning("[joytag] {Line}", e.Data); };

        if (!_proc.Start()) throw new InvalidOperationException("Failed to start JoyTag process.");
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();

        // health check
        var baseUrl = $"http://{opt.Host}:{opt.Port}";
        await WaitForHealthyAsync(baseUrl, TimeSpan.FromSeconds(20), ct);
        _logger.LogInformation("JoyTag healthy at {Url}", baseUrl);
    }

    private async Task WaitForHealthyAsync(string baseUrl, TimeSpan timeout, CancellationToken ct) {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout) {
            ct.ThrowIfCancellationRequested();
            try {
                // JoyTag側に /health が無い場合は適宜変更
                var res = await _http.GetAsync(baseUrl + "/health", ct);
                if (res.IsSuccessStatusCode) return;
            } catch { /* not ready */ }

            await Task.Delay(300, ct);
        }
        throw new TimeoutException("JoyTag health check timed out.");
    }

    public Task StopAsync() {
        if (_proc == null) return Task.CompletedTask;

        try {
            if (!_proc.HasExited) {
                _logger.LogInformation("Stopping JoyTag (pid={Pid})...", _proc.Id);
                _proc.Kill(entireProcessTree: true);
            }
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Failed to stop JoyTag.");
        } finally {
            _proc.Dispose();
            _proc = null;
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() {
        await StopAsync();
        _http.Dispose();
    }
}
