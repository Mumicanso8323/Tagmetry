# Tagmetry
Tagmetry — Dataset intelligence and LoRA optimization toolkit for Stable Diffusion creators.

## Privacy-first defaults
- **On-device processing:** scanning, normalization, duplicate detection, and metric analysis run locally on your machine.
- **Telemetry is opt-in:** telemetry is **disabled by default** and must be explicitly enabled in settings.
- **Sanitized logs/crash breadcrumbs:** runtime logs and crash breadcrumbs are designed to avoid user image data and caption content.

## Quick Start

### Debug run
1. Run `tool.bat debug` from the repository root.
2. When the web host starts, open `http://127.0.0.1:<port>/` in your browser.
3. The active listen URL (including port) is written in console startup output and `log/web.log`.

### Publish
1. Run `tool.bat publish` from the repository root.
2. The single-file build output is written to `dist/web`.

### Logs
- `log/bootstrap.log`: startup/bootstrap and fatal crash breadcrumbs (sanitized).
- `log/web.log`: regular ASP.NET Core and application runtime logs (sanitized).

## Telemetry setting
- Web and app defaults:
  - `src/Tagmetry.Web/appsettings.json` → `Telemetry.Enabled = false`
  - `src/Tagmetry.App/appsettings.json` → `Telemetry.Enabled = false`
- You can opt in from the web UI toggle (**Enable telemetry (opt-in)**).

## Third-party notices
- Generate/update notices and per-component license records:
  - `python scripts/generate_third_party_notices.py`
- Validate notices are current (used by CI):
  - `python scripts/check_third_party_notices.py`
- Generated artifacts:
  - `THIRD_PARTY_NOTICES.md`
  - `LICENSES/*.txt`
