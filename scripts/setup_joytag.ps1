#requires -Version 5.1
[CmdletBinding()]
param(
  # NOTE: param のデフォルト評価中に $PSScriptRoot が空になる環境があるため、
  # ここでは空にして、param の後で安全に決定します。
  [string]$RepoRoot = "",
  [string]$PythonExe = "",              # 例: C:\Python310\python.exe を直指定したい場合
  [string]$PyLauncherVersion = "3.10",  # py ランチャがある場合: py -3.10 を使う
  [switch]$ForceRecreate,
  [switch]$SkipInstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function New-Dir([string]$Path) {
  if (!(Test-Path -LiteralPath $Path)) {
    New-Item -ItemType Directory -Path $Path | Out-Null
  }
}

# ---- resolve RepoRoot safely (NO $PSScriptRoot in param default) ----
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
  # Prefer $PSScriptRoot when available, fallback to $PSCommandPath.
  $ScriptDir =
    if ($PSScriptRoot -and $PSScriptRoot.Trim().Length -gt 0) {
      $PSScriptRoot
    } else {
      # $PSCommandPath is available when running a script with -File
      Split-Path -Parent $PSCommandPath
    }

  if ([string]::IsNullOrWhiteSpace($ScriptDir)) {
    throw "Cannot determine script directory. Try running with: powershell -File .\scripts\setup_joytag.ps1"
  }

  $RepoRoot = (Resolve-Path (Join-Path $ScriptDir "..")).Path
} else {
  $RepoRoot = (Resolve-Path $RepoRoot).Path
}

# ---- logging ----
$logDir = Join-Path $RepoRoot "log"
New-Dir $logDir
$ts = Get-Date -Format "yyyyMMdd_HHmmss"
$logFile = Join-Path $logDir "setup_joytag_$ts.log"
Start-Transcript -Path $logFile | Out-Null

try {
  Write-Host "[INFO] RepoRoot = $RepoRoot"
  Write-Host "[INFO] LogFile  = $logFile"

  $thirdParty = Join-Path $RepoRoot "third_party"
  $runtimeDir = Join-Path $thirdParty "joytag\runtime"
  $serverDir  = Join-Path $runtimeDir "joytag_server"
  $venvDir    = Join-Path $runtimeDir ".venv"
  $reqPath    = Join-Path $runtimeDir "requirements.txt"
  $serverPy   = Join-Path $serverDir  "server.py"

  New-Dir $thirdParty
  New-Dir $runtimeDir
  New-Dir $serverDir

  # ---- choose base python ----
  $baseExe  = $null
  $baseArgs = @()

  if (-not [string]::IsNullOrWhiteSpace($PythonExe)) {
    if (!(Test-Path -LiteralPath $PythonExe)) { throw "PythonExe not found: $PythonExe" }
    $baseExe = $PythonExe
    $baseArgs = @()
    Write-Host "[INFO] Using explicit Python: $PythonExe"
  }
  else {
    $py = Get-Command py -ErrorAction SilentlyContinue
    if ($null -ne $py) {
      $baseExe = "py"
      $baseArgs = @("-$PyLauncherVersion")
      Write-Host "[INFO] Using py launcher: py -$PyLauncherVersion"
    }
    else {
      $python = Get-Command python -ErrorAction SilentlyContinue
      if ($null -eq $python) { throw "Python not found. Install Python or provide -PythonExe." }
      $baseExe = $python.Source
      $baseArgs = @()
      Write-Host "[INFO] Using python on PATH: $($python.Source)"
    }
  }

  if ([string]::IsNullOrWhiteSpace($baseExe)) {
    throw "Failed to select base python executable."
  }

  # ---- create venv ----
  if (Test-Path -LiteralPath $venvDir) {
    if ($ForceRecreate) {
      Write-Host "[INFO] Removing existing venv: $venvDir"
      Remove-Item -Recurse -Force -LiteralPath $venvDir
    }
  }

  if (!(Test-Path -LiteralPath $venvDir)) {
    Write-Host "[INFO] Creating venv: $venvDir"
    & $baseExe @baseArgs -m venv $venvDir
  }

  $venvPython = Join-Path $venvDir "Scripts\python.exe"
  if (!(Test-Path -LiteralPath $venvPython)) { throw "venv python not found: $venvPython" }

  Write-Host "[INFO] VenvPython: $venvPython"
  & $venvPython -c "import sys; print('python=', sys.version)"

  # ---- ensure requirements.txt exists (minimal) ----
  if (!(Test-Path -LiteralPath $reqPath)) {
@"
fastapi==0.115.0
uvicorn[standard]==0.30.6
"@ | Set-Content -Encoding UTF8 -LiteralPath $reqPath
    Write-Host "[INFO] Created minimal requirements.txt: $reqPath"
  } else {
    Write-Host "[INFO] requirements.txt exists: $reqPath"
  }

  if ($SkipInstall) {
    Write-Host "[INFO] SkipInstall enabled. Skipping pip install."
  } else {
    Write-Host "[INFO] Upgrading pip/setuptools/wheel..."
    & $venvPython -m pip install --upgrade pip setuptools wheel

    Write-Host "[INFO] Installing requirements..."
    & $venvPython -m pip install -r $reqPath
  }

  # ---- ensure server.py exists (minimal) ----
  if (!(Test-Path -LiteralPath $serverPy)) {
@"
import argparse
from fastapi import FastAPI
import uvicorn

app = FastAPI()

@app.get("/health")
def health():
    return {"ok": True}

@app.post("/tag")
def tag(payload: dict):
    return {"ok": True, "tags": [], "received": payload}

def main():
    p = argparse.ArgumentParser()
    p.add_argument("--host", default="127.0.0.1")
    p.add_argument("--port", type=int, default=7865)
    p.add_argument("--use-gpu", action="store_true")
    p.add_argument("--cpu", action="store_true")
    args = p.parse_args()
    print(f"[joytag_stub] host={args.host} port={args.port} use_gpu={args.use_gpu} cpu={args.cpu}")
    uvicorn.run(app, host=args.host, port=args.port, log_level="info")

if __name__ == "__main__":
    main()
"@ | Set-Content -Encoding UTF8 -LiteralPath $serverPy
    Write-Host "[INFO] Created stub server.py: $serverPy"
  } else {
    Write-Host "[INFO] server.py exists: $serverPy"
  }

  # ---- quick self-test ----
  Write-Host "[INFO] Import test..."
  & $venvPython -c "import fastapi, uvicorn; print('deps ok')"

  Write-Host ""
  Write-Host "[OK] JoyTag Python env prepared."
  Write-Host "     Venv python: $venvPython"
  Write-Host "     Server:      $serverPy"
  Write-Host ""
  Write-Host "Next test (manual):"
  Write-Host "  `"$venvPython`" `"$serverPy`" --host 127.0.0.1 --port 7865"
  Write-Host "  then open: http://127.0.0.1:7865/health"
}
catch {
  Write-Host "[ERROR] $($_.Exception.Message)"
  throw
}
finally {
  Stop-Transcript | Out-Null
  Write-Host "[INFO] Transcript saved: $logFile"
}
