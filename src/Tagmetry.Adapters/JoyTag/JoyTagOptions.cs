namespace Tagmetry.Adapters.JoyTag;

public sealed class JoyTagOptions {
    public bool Enabled { get; set; } = true;
    public bool UseGpu { get; set; } = true;

    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7865;

    // Paths は repo root からの相対。publish時も exe 隣に third_party がある想定。
    public string PythonExeRelativePath { get; set; } = "joytag/runtime/python/python.exe";
    public string ServerScriptRelativePath { get; set; } = "joytag/runtime/joytag_server/server.py";
}
